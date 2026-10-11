using UfoEncounter.Sim;

namespace UfoEncounter.Encounters;

/// <summary>
/// How the aircraft electrics misbehave during an encounter.
/// </summary>
public enum DisruptionKind
{
    None,
    /// <summary>One continuous blackout for the whole duration.</summary>
    Blackout,
    /// <summary>Rapid on/off/on/off flickering ("stutter").</summary>
    Stutter,
    /// <summary>Everything off (battery + alternators + avionics); timing is
    /// decoupled from the encounter (own start delay and duration).</summary>
    DeepBlackout,
    /// <summary>Flicker first, then (maybe) collapse into a full deep blackout.</summary>
    StutterToBlackout,
}

/// <summary>How hard a total blackout hits — chosen per aircraft type in the UI.</summary>
public enum ElectricalMode
{
    /// <summary>Piston/GA (C172, DA62): electrical switches only, engine keeps running.</summary>
    Switches = 0,
    /// <summary>Piston/GA: switches + engine shutdown (auto-restart on restore).</summary>
    SwitchesAndEngine = 1,
    /// <summary>Jets/turbines: engine auto-shutdown takes the generators and
    /// everything else down with it.</summary>
    FullShutdown = 2,
}

public sealed record DisruptionPlan
{
    public DisruptionKind Kind { get; init; } = DisruptionKind.Blackout;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(6);
    /// <summary>Wait this long after the encounter begins before cutting power
    /// (lets the blackout begin out of sync with the encounter).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.Zero;
    /// <summary>For Stutter: nominal length of a single phase; the actual on/off
    /// dwell times are randomised around this for an irregular flicker.</summary>
    public TimeSpan StutterStep { get; init; } = TimeSpan.FromMilliseconds(350);
    /// <summary>Also cut the alternator(s) / engine generators.</summary>
    public bool CutAlternator { get; init; } = true;
    /// <summary>Also kill avionics master (instrument screens) where supported.</summary>
    public bool CutAvionics { get; init; } = true;
    /// <summary>For DeepBlackout: also shut the engine(s) down and auto-restart on restore.</summary>
    public bool CutEngine { get; init; } = false;
    /// <summary>For StutterToBlackout: how long the blackout part lasts.</summary>
    public TimeSpan BlackoutDuration { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>For StutterToBlackout: probability (0..1) the flicker escalates.</summary>
    public double EscalateChance { get; init; } = 0.5;
    /// <summary>Use ONLY engine auto-shutdown/start for the blackout (kills turbines
    /// + their generators on jets where the per-channel toggles don't fully work).
    /// Leave off for GA pistons (e.g. the C172) where the toggles work well.</summary>
    public bool UseFullShutdown { get; init; }
}

/// <summary>
/// Drives the aircraft electrical channels to simulate instrument failure, then
/// guarantees power is restored. MSFS only reliably exposes <c>TOGGLE_*</c>
/// events, so we track a believed on/off state (seeded from the live SimVar when
/// the disruption actually starts) and toggle toward the target.
///
/// Restore guarantees: the finally block of every run restores; PANIK-STOP and
/// closing the app call <see cref="AbortAndRestoreNow"/>, which restores
/// synchronously (before SimConnect is closed); if the connection drops mid-run,
/// the restore is replayed when the same flight reconnects.
///
/// Whether anything visibly fails depends on the aircraft model: glass-cockpit
/// / study-level aircraft (e.g. the Vision Jet) keep essential buses powered.
/// </summary>
public sealed class ElectricalDisruptor
{
    private readonly SimConnectClient _sim;
    private readonly Action<string>? _log;
    private Random _rng = new();
    private CancellationTokenSource? _runCts;

    // Toggling alternators 1-4 covers singles (C172), twins (DA62) and beyond;
    // absent alternators are harmless no-ops.
    private static readonly SimConnectClient.SimEvent[] AltEvents =
    {
        SimConnectClient.SimEvent.TOGGLE_ALTERNATOR1,
        SimConnectClient.SimEvent.TOGGLE_ALTERNATOR2,
        SimConnectClient.SimEvent.TOGGLE_ALTERNATOR3,
        SimConnectClient.SimEvent.TOGGLE_ALTERNATOR4,
    };
    private bool _battery = true;
    private readonly bool[] _alts = { true, true, true, true };
    private bool _avionics = true;
    private bool _engineCut;

    // Restore that could not be sent because the connection was down.
    private bool _restorePending;
    private DateTime _pendingSince;

    public bool IsActive { get; private set; }

    public ElectricalDisruptor(SimConnectClient sim, Action<string>? log = null)
    {
        _sim = sim;
        _log = log;
        _sim.Connected += OnReconnected;
        _sim.SimQuit += () =>
        {
            // MSFS closed: the aircraft is gone, nothing to restore any more.
            _restorePending = false;
            ResetBelieved();
        };
    }

    public void SetRandom(Random rng) => _rng = rng;

    public async Task RunAsync(DisruptionPlan plan, CancellationToken ct)
    {
        if (plan.Kind == DisruptionKind.None || !_sim.IsConnected) return;
        if (IsActive) return;

        IsActive = true;
        ResetBelieved();
        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _runCts.Token;
        try
        {
            if (plan.StartDelay > TimeSpan.Zero)
                await Task.Delay(plan.StartDelay, token);
            SeedFromSim();   // read the switches only now, after the delay

            switch (plan.Kind)
            {
                case DisruptionKind.Blackout:
                    _log?.Invoke($"Elektrik: Blackout für {plan.Duration.TotalSeconds:0.#} s.");
                    SetPower(false, plan);
                    await Task.Delay(plan.Duration, token);
                    break;

                case DisruptionKind.DeepBlackout:
                    _log?.Invoke($"Elektrik: TOTAL-BLACKOUT für {plan.Duration.TotalSeconds:0.#} s"
                                 + $"{(plan.CutEngine || plan.UseFullShutdown ? " + Motor aus" : "")}.");
                    CutEverything(plan);
                    await Task.Delay(plan.Duration, token);
                    break;

                case DisruptionKind.Stutter:
                    _log?.Invoke($"Elektrik: Flackern für {plan.Duration.TotalSeconds:0.#} s.");
                    await StutterAsync(plan, plan.Duration, token);
                    break;

                case DisruptionKind.StutterToBlackout:
                    _log?.Invoke($"Elektrik: Flackern für {plan.Duration.TotalSeconds:0.#} s – vielleicht mehr …");
                    await StutterAsync(plan, plan.Duration, token);
                    if (_rng.NextDouble() < plan.EscalateChance)
                    {
                        _log?.Invoke($"… das Flackern kippt in einen TOTAL-BLACKOUT für "
                                     + $"{plan.BlackoutDuration.TotalSeconds:0.#} s{(plan.CutEngine || plan.UseFullShutdown ? " + Motor aus" : "")}.");
                        CutEverything(plan);
                        await Task.Delay(plan.BlackoutDuration, token);
                    }
                    else
                    {
                        _log?.Invoke("… das Flackern verebbt, der Strom hält.");
                    }
                    break;
            }
        }
        catch (OperationCanceledException) { /* restored below */ }
        finally
        {
            // Failsafe: power always comes back on, even on cancel/exception.
            // (Idempotent — a synchronous AbortAndRestoreNow may already have done it.)
            RestorePower();
            IsActive = false;
        }
    }

    /// <summary>Cancel a running disruption and restore power RIGHT NOW,
    /// synchronously — used by PANIK-STOP and when the app closes, so the restore
    /// is sent before SimConnect is disposed.</summary>
    public void AbortAndRestoreNow()
    {
        _runCts?.Cancel();
        if (IsActive) RestorePower();
    }

    private void CutEverything(DisruptionPlan plan)
    {
        if (plan.UseFullShutdown)
        {
            _sim.Transmit(SimConnectClient.SimEvent.ENGINE_AUTO_SHUTDOWN);
            _engineCut = true;
            return;
        }
        SetAllPower(false);
        if (plan.CutEngine)
        {
            _sim.Transmit(SimConnectClient.SimEvent.ENGINE_AUTO_SHUTDOWN);
            _engineCut = true;
        }
    }

    /// <summary>Irregular on/off flicker for the given span (off-phases skew longer).</summary>
    private async Task StutterAsync(DisruptionPlan plan, TimeSpan span, CancellationToken ct)
    {
        var until = DateTime.UtcNow + span;
        var on = true;
        double baseMs = plan.StutterStep.TotalMilliseconds;
        while (DateTime.UtcNow < until)
        {
            on = !on;
            SetPower(on, plan);
            double ms = baseMs * (0.3 + _rng.NextDouble() * 1.6);
            if (!on) ms *= 1.0 + _rng.NextDouble() * 0.6;
            await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
        }
    }

    private void ResetBelieved()
    {
        _battery = true;
        for (int i = 0; i < _alts.Length; i++) _alts[i] = true;
        _avionics = true;
        _engineCut = false;
    }

    /// <summary>Re-sync believed state from the live SimVars.</summary>
    private void SeedFromSim()
    {
        var s = _sim.State;
        _battery = s.MasterBattery > 0.5;
        _alts[0] = s.MasterAlternator > 0.5;
        for (int i = 1; i < _alts.Length; i++) _alts[i] = true; // no per-frame SimVar; assume on
        _avionics = true;
        _engineCut = false;
    }

    private void SetPower(bool on, DisruptionPlan plan)
    {
        SetBattery(on);
        if (plan.CutAlternator) SetAllAlternators(on);
        if (plan.CutAvionics) SetAvionics(on);
    }

    private void SetAllPower(bool on)
    {
        SetBattery(on);
        SetAllAlternators(on);
        SetAvionics(on);
    }

    private bool NeedsRestore => !_battery || !_avionics || _engineCut || _alts.Any(a => !a);

    /// <summary>Restore everything we switched off. If the connection is down the
    /// toggles would be lost, so the restore is kept pending for the reconnect.</summary>
    private void RestorePower()
    {
        if (!NeedsRestore) return;
        if (!_sim.IsConnected)
        {
            if (!_restorePending)
            {
                _restorePending = true;
                _pendingSince = DateTime.UtcNow;
                _log?.Invoke("⚠ Keine Verbindung – Strom/Motor werden wiederhergestellt, sobald MSFS wieder verbunden ist.");
            }
            return;
        }
        if (!_battery) { _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_MASTER_BATTERY); _battery = true; }
        for (int i = 0; i < _alts.Length; i++)
            if (!_alts[i]) { _sim.Transmit(AltEvents[i]); _alts[i] = true; }
        if (!_avionics) { _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_AVIONICS_MASTER); _avionics = true; }
        if (_engineCut) { _sim.Transmit(SimConnectClient.SimEvent.ENGINE_AUTO_START); _engineCut = false; }
        _restorePending = false;
        _log?.Invoke("Elektrik: Strom wiederhergestellt.");
    }

    private void OnReconnected()
    {
        if (!_restorePending) return;
        if (DateTime.UtcNow - _pendingSince > TimeSpan.FromMinutes(1))
        {
            // Too long ago — probably a different flight now; toggling could switch
            // a healthy aircraft OFF. Leave it to the pilot.
            _restorePending = false;
            ResetBelieved();
            _log?.Invoke("⚠ Ausstehende Strom-Wiederherstellung verworfen (Verbindung war zu lange weg). Bitte Schalter im Cockpit prüfen.");
            return;
        }
        RestorePower();
    }

    private void SetBattery(bool on)
    {
        if (_battery == on) return;
        _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_MASTER_BATTERY);
        _battery = on;
    }

    private void SetAllAlternators(bool on)
    {
        for (int i = 0; i < _alts.Length; i++)
        {
            if (_alts[i] == on) continue;
            _sim.Transmit(AltEvents[i]);
            _alts[i] = on;
        }
    }

    private void SetAvionics(bool on)
    {
        if (_avionics == on) return;
        _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_AVIONICS_MASTER);
        _avionics = on;
    }
}
