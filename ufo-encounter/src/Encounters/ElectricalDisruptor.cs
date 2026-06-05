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
    /// <summary>Everything off (battery + both alternators + avionics); timing is
    /// decoupled from the encounter (own start delay and duration).</summary>
    DeepBlackout,
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
}

/// <summary>
/// Drives the aircraft electrical channels to simulate instrument failure, then
/// guarantees power is restored. MSFS only reliably exposes <c>TOGGLE_*</c>
/// events, so we track a believed on/off state (seeded from the live SimVar)
/// and toggle toward the target — and always restore to ON in a finally block.
///
/// Whether anything visibly fails depends on the aircraft model: glass-cockpit
/// / study-level aircraft (e.g. the Vision Jet) keep essential buses powered
/// and may barely react. That is expected; DeepBlackout maximises the attempt
/// by also cutting both alternators and the avionics master.
/// </summary>
public sealed class ElectricalDisruptor
{
    private readonly SimConnectClient _sim;
    private readonly Action<string>? _log;
    private readonly Random _rng = new();

    // Believed state of each channel (true = on).
    private bool _battery = true;
    private bool _alt1 = true;
    private bool _alt2 = true;
    private bool _avionics = true;
    private bool _engineCut;

    public bool IsActive { get; private set; }

    public ElectricalDisruptor(SimConnectClient sim, Action<string>? log = null)
    {
        _sim = sim;
        _log = log;
    }

    public async Task RunAsync(DisruptionPlan plan, CancellationToken ct)
    {
        if (plan.Kind == DisruptionKind.None || !_sim.IsConnected) return;
        if (IsActive) return;

        IsActive = true;
        SeedFromSim();
        try
        {
            if (plan.StartDelay > TimeSpan.Zero)
                await Task.Delay(plan.StartDelay, ct);

            switch (plan.Kind)
            {
                case DisruptionKind.Blackout:
                    _log?.Invoke($"Blackout for {plan.Duration.TotalSeconds:0.#}s.");
                    SetPower(false, plan);
                    await Task.Delay(plan.Duration, ct);
                    break;

                case DisruptionKind.DeepBlackout:
                    _log?.Invoke($"DEEP blackout: all power off for {plan.Duration.TotalSeconds:0.#}s "
                                 + $"(delay {plan.StartDelay.TotalSeconds:0.#}s){(plan.CutEngine ? " + engine" : "")}.");
                    SetAllPower(false);
                    if (plan.CutEngine)
                    {
                        _sim.Transmit(SimConnectClient.SimEvent.ENGINE_AUTO_SHUTDOWN);
                        _engineCut = true;
                    }
                    await Task.Delay(plan.Duration, ct);
                    break;

                case DisruptionKind.Stutter:
                    _log?.Invoke($"Stutter for {plan.Duration.TotalSeconds:0.#}s.");
                    var until = DateTime.UtcNow + plan.Duration;
                    var on = true;
                    double baseMs = plan.StutterStep.TotalMilliseconds;
                    while (DateTime.UtcNow < until)
                    {
                        on = !on;
                        SetPower(on, plan);
                        // Irregular dwell: random around the nominal step; off-phases
                        // skew a little longer for a "dying" feel.
                        double ms = baseMs * (0.3 + _rng.NextDouble() * 1.6);
                        if (!on) ms *= 1.0 + _rng.NextDouble() * 0.6;
                        await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
                    }
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke("Electrical disruption cancelled.");
        }
        finally
        {
            // Failsafe: power always comes back on, even on cancel/exception.
            RestorePower();
            IsActive = false;
        }
    }

    /// <summary>Re-sync believed state from the live SimVars.</summary>
    private void SeedFromSim()
    {
        var s = _sim.State;
        _battery = s.MasterBattery > 0.5;
        _alt1 = s.MasterAlternator > 0.5;
        _alt2 = true;     // no reliable per-frame SimVar mapped; assume on
        _avionics = true;
        _engineCut = false;
    }

    private void SetPower(bool on, DisruptionPlan plan)
    {
        SetBattery(on);
        if (plan.CutAlternator) SetAlt1(on);
        if (plan.CutAvionics) SetAvionics(on);
    }

    private void SetAllPower(bool on)
    {
        SetBattery(on);
        SetAlt1(on);
        SetAlt2(on);
        SetAvionics(on);
    }

    /// <summary>Restore is forced regardless of believed state.</summary>
    private void RestorePower()
    {
        if (!_battery) { _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_MASTER_BATTERY); _battery = true; }
        if (!_alt1) { _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_ALTERNATOR1); _alt1 = true; }
        if (!_alt2) { _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_ALTERNATOR2); _alt2 = true; }
        if (!_avionics) { _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_AVIONICS_MASTER); _avionics = true; }
        if (_engineCut) { _sim.Transmit(SimConnectClient.SimEvent.ENGINE_AUTO_START); _engineCut = false; }
        _log?.Invoke("Power restored.");
    }

    private void SetBattery(bool on)
    {
        if (_battery == on) return;
        _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_MASTER_BATTERY);
        _battery = on;
    }

    private void SetAlt1(bool on)
    {
        if (_alt1 == on) return;
        _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_ALTERNATOR1);
        _alt1 = on;
    }

    private void SetAlt2(bool on)
    {
        if (_alt2 == on) return;
        _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_ALTERNATOR2);
        _alt2 = on;
    }

    private void SetAvionics(bool on)
    {
        if (_avionics == on) return;
        _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_AVIONICS_MASTER);
        _avionics = on;
    }
}
