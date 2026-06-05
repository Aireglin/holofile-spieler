using System.Windows.Threading;
using UfoEncounter.Audio;
using UfoEncounter.Sim;
using UfoEncounter.Util;

namespace UfoEncounter.Encounters;

/// <summary>
/// The "director": picks and times encounters, either on demand (test buttons)
/// or autonomously (random mode). It runs each encounter as an ordered list of
/// PHASES (approach → observation → escalation → departure when multi-phase),
/// coordinating lights, electrics and a live, distance-driven audio mix. A
/// "dread" level scales how close/long/aggressive things get, and rare
/// "signature events" upgrade an encounter into something spectacular.
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
    private readonly DispatcherTimer _audioTimer;
    private Random _rng = new();
    private CancellationTokenSource? _current;

    // Live audio targets, consumed (and smoothed) by the audio timer.
    private double _proxTarget;
    private bool _subBassOn;
    private double _droneVol, _subVol, _statVol;

    // ---- Tunables (bound to the UI) ----
    public bool RealismLock { get; set; } = true;
    public double MinAglFeet { get; set; } = 1500;
    public double FrequencyPerMin { get; set; } = 1.0;
    public double MinDurationSec { get; set; } = 4;
    public double MaxDurationSec { get; set; } = 12;
    public double Intensity { get; set; } = 0.6;
    /// <summary>0..1 — escalation aggressiveness, proximity and signature odds.</summary>
    public double Dread { get; set; } = 0.4;
    /// <summary>Run encounters as multi-phase mini-stories.</summary>
    public bool MultiPhase { get; set; } = true;

    public double BlackoutMinSec { get; set; } = 8;
    public double BlackoutMaxSec { get; set; } = 25;

    public string LightObjectTitle { get; set; } = "";
    public string MothershipTitle { get; set; } = "";
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
        _audioTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _audioTimer.Tick += OnAudioTick;
    }

    public void SetSeed(int? seed)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        _trace?.Invoke(seed.HasValue ? $"Seed set to {seed}." : "Seed randomized.");
    }

    public void SetRandomMode(bool on)
    {
        if (on) { ScheduleNext(); _scheduler.Start(); _trace?.Invoke("Random mode ON."); }
        else { _scheduler.Stop(); _trace?.Invoke("Random mode OFF."); }
    }

    private void ScheduleNext()
    {
        double avgGap = 60.0 / Math.Max(0.05, FrequencyPerMin);
        _scheduler.Interval = TimeSpan.FromSeconds(avgGap * (0.5 + _rng.NextDouble()));
    }

    private async void OnSchedulerTick(object? sender, EventArgs e)
    {
        ScheduleNext();
        if (IsEncounterActive) return;
        if (!CanTriggerNow()) { _trace?.Invoke("Skipped (realism-lock)."); return; }
        await TriggerAsync(EncounterScenario.Catalog[_rng.Next(EncounterScenario.Catalog.Count)]);
    }

    private bool CanTriggerNow()
    {
        if (!_sim.IsConnected) return false;
        if (!RealismLock) return true;
        var s = _sim.State;
        return s.OnGround < 0.5 && s.AltitudeAgl >= MinAglFeet;
    }

    // ---- Phases ----

    private sealed record Phase(
        string Name, double Dur, LightPattern? Light, DisruptionKind Disruption,
        double Proximity, bool Whoosh, bool SubBass, bool Silence);

    public async Task TriggerAsync(EncounterScenario scenario, double? durationSec = null)
    {
        if (IsEncounterActive) return;

        double visual = durationSec ?? RandomDuration();
        bool signature = _rng.NextDouble() < SignatureChance();
        var phases = (MultiPhase || signature)
            ? BuildMultiPhase(scenario, signature)
            : BuildSinglePhase(scenario, visual);

        var ct = (_current = new CancellationTokenSource()).Token;
        IsEncounterActive = true;
        EncounterStateChanged?.Invoke(scenario.Name, true);
        if (signature) _trace?.Invoke("✦ SIGNATURE EVENT ✦");
        _trace?.Invoke($"▶ {scenario.Name}{(MultiPhase || signature ? " (mehrphasig)" : "")}");
        _log.Record(signature ? $"{scenario.Name} ✦" : scenario.Name, _sim.State);

        bool mothership = scenario.Mothership || signature;
        string title = mothership ? MothershipTitle : LightObjectTitle;
        bool lightsStarted = false;
        Task elec = Task.CompletedTask;

        _audio.StartLayers();
        _audioTimer.Start();
        try
        {
            foreach (var ph in phases)
            {
                _proxTarget = ph.Proximity;
                _subBassOn = ph.SubBass;
                if (ph.Silence) { _proxTarget = 0; _subBassOn = false; _audio.HardSilence(); }
                if (ph.Whoosh) _audio.PlayWhoosh(0.5 + 0.5 * Dread);

                if (LightsEnabled && ph.Light is LightPattern pat)
                {
                    if (!lightsStarted) { _lights.Start(pat, mothership ? 1 : LightCount, ph.Dur, title); lightsStarted = true; }
                    else _lights.SetPattern(pat);
                }

                if (ph.Disruption != DisruptionKind.None && elec.IsCompleted)
                    elec = _disruptor.RunAsync(BuildPlan(ph.Disruption, ph.Dur, signature), ct);

                _trace?.Invoke($"  · {ph.Name} ({ph.Dur:0.#}s)");
                try { await Task.Delay(TimeSpan.FromSeconds(ph.Dur), ct); }
                catch (OperationCanceledException) { break; }
            }

            _audio.HardSilence();
            _lights.Stop();
            try { await elec; } catch (OperationCanceledException) { }
        }
        finally
        {
            _audioTimer.Stop();
            _audio.StopAll();
            _lights.Stop();
            IsEncounterActive = false;
            EncounterStateChanged?.Invoke(scenario.Name, false);
            _trace?.Invoke($"■ {scenario.Name} ended.");
        }
    }

    private double SignatureChance() => 0.015 + 0.05 * Dread; // ~1.5% .. 6.5%

    private List<Phase> BuildSinglePhase(EncounterScenario s, double visual) => new()
    {
        new Phase("Encounter", visual, s.Lights, s.Disruption,
            Proximity: s.Mothership ? 0.5 : 0.6, Whoosh: true, SubBass: s.Mothership, Silence: false),
    };

    private List<Phase> BuildMultiPhase(EncounterScenario s, bool signature)
    {
        double d = Dread;
        var approach = s.Mothership || signature ? LightPattern.Mothership : LightPattern.PopUp;
        var observe = s.Mothership || signature ? LightPattern.Mothership : LightPattern.Wingman;
        var climax = signature ? LightPattern.Mothership : (s.Lights ?? LightPattern.TicTac);

        var escalationDisruption = signature
            ? DisruptionKind.DeepBlackout
            : (s.Disruption != DisruptionKind.None ? s.Disruption
               : (d > 0.6 ? DisruptionKind.Stutter : DisruptionKind.None));

        double escDur = signature ? 16 + 12 * d : 4 + 8 * d;

        return new List<Phase>
        {
            new("Annäherung", 3.5 + 2 * (1 - d), approach, DisruptionKind.None,
                Proximity: 0.25, Whoosh: true, SubBass: false, Silence: false),
            new("Beobachtung", 4 + 3 * (1 - d), observe, DisruptionKind.None,
                Proximity: 0.5, Whoosh: false, SubBass: true, Silence: false),
            new("Eskalation", escDur, climax, escalationDisruption,
                Proximity: Math.Min(1.0, 0.85 + 0.15 * d + (signature ? 0.15 : 0)), Whoosh: true, SubBass: true, Silence: false),
            new("Abgang", 2.5, LightPattern.TicTac, DisruptionKind.None,
                Proximity: 0.0, Whoosh: false, SubBass: false, Silence: true),
        };
    }

    private DisruptionPlan BuildPlan(DisruptionKind kind, double phaseDur, bool signature)
    {
        if (kind == DisruptionKind.DeepBlackout)
        {
            double lo = Math.Min(BlackoutMinSec, BlackoutMaxSec);
            double hi = Math.Max(BlackoutMinSec, BlackoutMaxSec);
            double dur = lo + _rng.NextDouble() * (hi - lo);
            if (signature) dur = Math.Max(dur, BlackoutMaxSec); // signature: long & dark
            return new DisruptionPlan
            {
                Kind = DisruptionKind.DeepBlackout,
                StartDelay = TimeSpan.FromSeconds(_rng.NextDouble() * 3),
                Duration = TimeSpan.FromSeconds(dur),
            };
        }

        return new DisruptionPlan
        {
            Kind = kind,
            Duration = TimeSpan.FromSeconds(phaseDur),
            StutterStep = TimeSpan.FromMilliseconds(450 - 250 * Intensity),
        };
    }

    // ---- Live audio mix (distance-driven, smoothed) ----

    private void OnAudioTick(object? sender, EventArgs e)
    {
        // Real proximity from the nearest spawned object overrides the phase floor.
        double proxFromLights = 0;
        if (_lights.NearestMeters is double m)
            proxFromLights = Math.Clamp(1 - (m - 60) / 1400.0, 0, 1);
        double prox = Math.Max(_proxTarget, proxFromLights);

        double droneTarget = Math.Clamp(Intensity * (0.15 + 0.85 * prox), 0, 1);
        double subTarget = _subBassOn ? Math.Clamp((0.4 + 0.6 * Dread) * prox, 0, 1) : 0;
        double statTarget = _disruptor.IsActive ? 0.45 * (0.5 + 0.5 * Intensity) : 0;

        _droneVol = Lerp(_droneVol, droneTarget, 0.12);
        _subVol = Lerp(_subVol, subTarget, 0.12);
        _statVol = Lerp(_statVol, statTarget, 0.20);

        _audio.SetDroneVolume(_droneVol);
        _audio.SetSubBassVolume(_subVol);
        _audio.SetStaticVolume(_statVol);
    }

    private static double Lerp(double a, double b, double k) => a + (b - a) * k;

    /// <summary>Spawn lights on their own for a quick visual test.</summary>
    public void TestLights(LightPattern pattern, double durationSec, bool mothership = false)
    {
        if (!LightsEnabled) { _trace?.Invoke("Lights are disabled."); return; }
        string title = mothership ? MothershipTitle : LightObjectTitle;
        _lights.Start(pattern, mothership ? 1 : LightCount, durationSec, title);
        Task.Delay(TimeSpan.FromSeconds(durationSec)).ContinueWith(
            _ => _lights.Stop(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private double RandomDuration()
    {
        double lo = Math.Min(MinDurationSec, MaxDurationSec);
        double hi = Math.Max(MinDurationSec, MaxDurationSec);
        return lo + _rng.NextDouble() * (hi - lo);
    }

    public void PanicStop()
    {
        _scheduler.Stop();
        _current?.Cancel();
        _audioTimer.Stop();
        _audio.StopAll();
        _lights.Stop();
        _trace?.Invoke("⛔ PANIC STOP — power restored, lights removed, sound off, random mode off.");
    }
}
