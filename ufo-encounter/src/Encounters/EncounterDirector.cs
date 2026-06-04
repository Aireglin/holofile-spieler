using System.Windows.Threading;
using UfoEncounter.Audio;
using UfoEncounter.Sim;
using UfoEncounter.Util;

namespace UfoEncounter.Encounters;

/// <summary>
/// The "director": picks and times encounters, either on demand (test buttons)
/// or autonomously (random mode driven by the frequency slider). Owns the
/// realism-lock and seeded-run logic and coordinates electrics + hum + lights.
/// </summary>
public sealed class EncounterDirector
{
    private readonly SimConnectClient _sim;
    private readonly ElectricalDisruptor _disruptor;
    private readonly LightChoreographer _lights;
    private readonly AudioEngine _audio;
    private readonly Logbook _log;
    private readonly Action<string>? _trace;

    private readonly DispatcherTimer _scheduler;
    private Random _rng = new();
    private CancellationTokenSource? _current;

    // ---- Tunables (bound to the UI) ----
    public bool RealismLock { get; set; } = true;
    public double MinAglFeet { get; set; } = 1500;
    /// <summary>Average encounters per minute in random mode.</summary>
    public double FrequencyPerMin { get; set; } = 1.0;
    public double MinDurationSec { get; set; } = 4;
    public double MaxDurationSec { get; set; } = 12;
    /// <summary>Master intensity 0..1 (loudness + flicker aggressiveness).</summary>
    public double Intensity { get; set; } = 0.6;

    // Deep-blackout timing, intentionally independent of the encounter window.
    public double BlackoutMinSec { get; set; } = 8;
    public double BlackoutMaxSec { get; set; } = 25;

    // Lights
    public string LightObjectTitle { get; set; } = "";
    public int LightCount { get; set; } = 2;
    public bool LightsEnabled { get; set; } = true;

    public bool IsRandomMode => _scheduler.IsEnabled;
    public bool IsEncounterActive { get; private set; }

    public event Action<string, bool>? EncounterStateChanged; // (name, active)

    public EncounterDirector(SimConnectClient sim, ElectricalDisruptor disruptor,
        LightChoreographer lights, AudioEngine audio, Logbook log, Action<string>? trace = null)
    {
        _sim = sim;
        _disruptor = disruptor;
        _lights = lights;
        _audio = audio;
        _log = log;
        _trace = trace;

        _scheduler = new DispatcherTimer();
        _scheduler.Tick += OnSchedulerTick;
    }

    /// <summary>Seed the RNG for reproducible runs (good for demos/streaming).</summary>
    public void SetSeed(int? seed)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        _trace?.Invoke(seed.HasValue ? $"Seed set to {seed}." : "Seed randomized.");
    }

    public void SetRandomMode(bool on)
    {
        if (on)
        {
            ScheduleNext();
            _scheduler.Start();
            _trace?.Invoke("Random mode ON.");
        }
        else
        {
            _scheduler.Stop();
            _trace?.Invoke("Random mode OFF.");
        }
    }

    private void ScheduleNext()
    {
        double avgGap = 60.0 / Math.Max(0.05, FrequencyPerMin);
        double gap = avgGap * (0.5 + _rng.NextDouble()); // 0.5x .. 1.5x
        _scheduler.Interval = TimeSpan.FromSeconds(gap);
    }

    private async void OnSchedulerTick(object? sender, EventArgs e)
    {
        ScheduleNext();
        if (IsEncounterActive) return;
        if (!CanTriggerNow()) { _trace?.Invoke("Skipped (realism-lock)."); return; }

        var scenario = EncounterScenario.Catalog[_rng.Next(EncounterScenario.Catalog.Count)];
        await TriggerAsync(scenario);
    }

    private bool CanTriggerNow()
    {
        if (!_sim.IsConnected) return false;
        if (!RealismLock) return true;
        var s = _sim.State;
        return s.OnGround < 0.5 && s.AltitudeAgl >= MinAglFeet;
    }

    /// <summary>Run a scenario now. <paramref name="durationSec"/> overrides the
    /// random visual duration.</summary>
    public async Task TriggerAsync(EncounterScenario scenario, double? durationSec = null)
    {
        if (IsEncounterActive) return;

        double visual = durationSec ?? RandomDuration();
        var ct = (_current = new CancellationTokenSource()).Token;

        IsEncounterActive = true;
        EncounterStateChanged?.Invoke(scenario.Name, true);
        _trace?.Invoke($"▶ {scenario.Name} ({visual:0.#}s)");
        _log.Record(scenario.Name, _sim.State);

        try
        {
            if (scenario.Hum)
            {
                double loud = Math.Clamp(scenario.HumIntensity * (0.4 + 0.6 * Intensity), 0, 1);
                _audio.StartHum(loud, scenario.HumBaseHz);
            }

            if (LightsEnabled && scenario.Lights is LightPattern pattern)
                _lights.Start(pattern, LightCount, visual, LightObjectTitle);

            var elec = _disruptor.RunAsync(BuildPlan(scenario, visual), ct);

            // Visuals (hum + lights) follow the encounter window...
            try { await Task.Delay(TimeSpan.FromSeconds(visual), ct); }
            catch (OperationCanceledException) { }
            _audio.Stop();
            _lights.Stop();

            // ...while a (deep) blackout may linger and restore on its own clock.
            try { await elec; } catch (OperationCanceledException) { }
        }
        finally
        {
            _audio.Stop();
            _lights.Stop();
            IsEncounterActive = false;
            EncounterStateChanged?.Invoke(scenario.Name, false);
            _trace?.Invoke($"■ {scenario.Name} ended.");
        }
    }

    private DisruptionPlan BuildPlan(EncounterScenario scenario, double visual)
    {
        if (scenario.Disruption == DisruptionKind.DeepBlackout)
        {
            double lo = Math.Min(BlackoutMinSec, BlackoutMaxSec);
            double hi = Math.Max(BlackoutMinSec, BlackoutMaxSec);
            return new DisruptionPlan
            {
                Kind = DisruptionKind.DeepBlackout,
                StartDelay = TimeSpan.FromSeconds(_rng.NextDouble() * 5),     // 0..5s in
                Duration = TimeSpan.FromSeconds(lo + _rng.NextDouble() * (hi - lo)),
            };
        }

        return new DisruptionPlan
        {
            Kind = scenario.Disruption,
            Duration = TimeSpan.FromSeconds(visual),
            StutterStep = TimeSpan.FromMilliseconds(450 - 250 * Intensity),
            CutAlternator = true,
            CutAvionics = true,
        };
    }

    /// <summary>Spawn lights on their own for a quick visual test.</summary>
    public void TestLights(LightPattern pattern, double durationSec)
    {
        if (!LightsEnabled) { _trace?.Invoke("Lights are disabled."); return; }
        _lights.Start(pattern, LightCount, durationSec, LightObjectTitle);
        Task.Delay(TimeSpan.FromSeconds(durationSec)).ContinueWith(
            _ => _lights.Stop(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private double RandomDuration()
    {
        double lo = Math.Min(MinDurationSec, MaxDurationSec);
        double hi = Math.Max(MinDurationSec, MaxDurationSec);
        return lo + _rng.NextDouble() * (hi - lo);
    }

    /// <summary>Abort everything immediately; the disruptor restores power in its
    /// own finally block, the hum and lights stop here.</summary>
    public void PanicStop()
    {
        _scheduler.Stop();
        _current?.Cancel();
        _audio.Stop();
        _lights.Stop();
        _trace?.Invoke("⛔ PANIC STOP — power restored, lights removed, random mode off.");
    }
}
