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
}

public sealed record DisruptionPlan
{
    public DisruptionKind Kind { get; init; } = DisruptionKind.Blackout;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(6);
    /// <summary>For Stutter: length of a single off- or on-phase.</summary>
    public TimeSpan StutterStep { get; init; } = TimeSpan.FromMilliseconds(350);
    /// <summary>Also cut the alternator (engine generator), not just the battery.</summary>
    public bool CutAlternator { get; init; } = true;
    /// <summary>Also kill avionics master (instrument screens) where supported.</summary>
    public bool CutAvionics { get; init; } = true;
}

/// <summary>
/// Drives the aircraft electrical channels to simulate instrument failure, then
/// guarantees power is restored. MSFS only reliably exposes <c>TOGGLE_*</c>
/// events, so we track a believed on/off state (seeded from the live SimVar)
/// and toggle toward the target — and always restore to ON in a finally block.
///
/// Whether anything visibly fails depends on the aircraft model (study-level
/// aircraft may ignore these events); that is expected and documented.
/// </summary>
public sealed class ElectricalDisruptor
{
    private readonly SimConnectClient _sim;
    private readonly Action<string>? _log;

    // Believed state of each channel (true = on).
    private bool _battery = true;
    private bool _alternator = true;
    private bool _avionics = true;

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
            switch (plan.Kind)
            {
                case DisruptionKind.Blackout:
                    _log?.Invoke($"Electrical blackout for {plan.Duration.TotalSeconds:0.#}s.");
                    SetPower(false, plan);
                    await Task.Delay(plan.Duration, ct);
                    break;

                case DisruptionKind.Stutter:
                    _log?.Invoke($"Electrical stutter for {plan.Duration.TotalSeconds:0.#}s.");
                    var until = DateTime.UtcNow + plan.Duration;
                    var on = true;
                    while (DateTime.UtcNow < until)
                    {
                        on = !on;
                        SetPower(on, plan);
                        await Task.Delay(plan.StutterStep, ct);
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
            RestorePower(plan);
            IsActive = false;
        }
    }

    /// <summary>Re-sync believed state from the live SimVars.</summary>
    private void SeedFromSim()
    {
        var s = _sim.State;
        _battery = s.MasterBattery > 0.5;
        _alternator = s.MasterAlternator > 0.5;
        // No reliable per-frame avionics SimVar mapped; assume on at start.
        _avionics = true;
    }

    private void SetPower(bool on, DisruptionPlan plan)
    {
        SetBattery(on);
        if (plan.CutAlternator) SetAlternator(on);
        if (plan.CutAvionics) SetAvionics(on);
    }

    /// <summary>Restore is forced regardless of believed state.</summary>
    private void RestorePower(DisruptionPlan plan)
    {
        if (!_battery) { _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_MASTER_BATTERY); _battery = true; }
        if (plan.CutAlternator && !_alternator) { _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_ALTERNATOR1); _alternator = true; }
        if (plan.CutAvionics && !_avionics) { _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_AVIONICS_MASTER); _avionics = true; }
        _log?.Invoke("Power restored.");
    }

    private void SetBattery(bool on)
    {
        if (_battery == on) return;
        _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_MASTER_BATTERY);
        _battery = on;
    }

    private void SetAlternator(bool on)
    {
        if (_alternator == on) return;
        _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_ALTERNATOR1);
        _alternator = on;
    }

    private void SetAvionics(bool on)
    {
        if (_avionics == on) return;
        _sim.Transmit(SimConnectClient.SimEvent.TOGGLE_AVIONICS_MASTER);
        _avionics = on;
    }
}
