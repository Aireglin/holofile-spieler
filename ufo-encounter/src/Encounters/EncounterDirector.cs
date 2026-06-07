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
    /// <summary>Play the one-shot approach whoosh (off by default — too prominent).</summary>
    public bool WhooshEnabled { get; set; } = false;
    /// <summary>Whoosh loudness 0..1 when enabled.</summary>
    public double WhooshVolume { get; set; } = 0.35;
    /// <summary>In random mode, bias scenario choice by time of day (night → lights,
    /// day → solid/mothership).</summary>
    public bool DayNightBias { get; set; } = true;
    /// <summary>Play the radio-static/crackle layer during electrical failures
    /// (off by default — the hum alone is preferred).</summary>
    public bool StaticEnabled { get; set; } = false;

    public double BlackoutMinSec { get; set; } = 8;
    public double BlackoutMaxSec { get; set; } = 25;
    /// <summary>Probability (0..1) that an encounter's electrical effect actually
    /// fires this run — so it's "sometimes a failure, sometimes just lights".</summary>
    public double DisruptionChance { get; set; } = 0.75;
    /// <summary>Total blackout also shuts the engine down (and auto-restarts it).</summary>
    public bool DeepBlackoutCutsEngine { get; set; } = true;

    public string LightObjectTitle { get; set; } = "";
    public string MothershipTitle { get; set; } = "";
    public int LightCount { get; set; } = 2;
    public bool LightsEnabled { get; set; } = true;
    /// <summary>Spawn objects as aircraft (AICreateNonATCAircraft) — needed for
    /// titles that are aircraft (e.g. a flyable UFO).</summary>
    public bool SpawnAsAircraft { get; set; } = false;

    // Holy Grail "beam" — bright object(s) hovering above the aircraft.
    public string BeamTitle { get; set; } = "";
    public int BeamCount { get; set; } = 3;
    public double BeamHeight { get; set; } = 30;

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
        await TriggerAsync(PickScenario());
    }

    /// <summary>Weighted random scenario pick. With day/night bias on, night
    /// favours light-only scenarios and day favours the solid mothership.</summary>
    private EncounterScenario PickScenario()
    {
        var cat = EncounterScenario.Catalog;
        if (!DayNightBias) return cat[_rng.Next(cat.Count)];

        int tod = (int)Math.Round(_sim.State.TimeOfDay); // 1=dawn 2=day 3=dusk 4=night
        var weights = new double[cat.Count];
        double total = 0;
        for (int i = 0; i < cat.Count; i++)
        {
            double w = 1.0;
            bool hasLights = cat[i].Lights is not null && !cat[i].Mothership;
            if (tod == 4) w = cat[i].Mothership ? 0.6 : (hasLights ? 3.0 : 1.0); // night
            else if (tod == 2) w = cat[i].Mothership ? 3.0 : 1.0;                 // day
            weights[i] = w;
            total += w;
        }

        double r = _rng.NextDouble() * total;
        for (int i = 0; i < cat.Count; i++)
        {
            r -= weights[i];
            if (r <= 0) return cat[i];
        }
        return cat[^1];
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
        // A signature event sometimes becomes the full "Holy Grail".
        if (signature && _rng.NextDouble() < 0.5) { await RunHolyGrailAsync(); return; }
        // Per-run roll: does the electrical effect fire at all this time?
        bool allowElec = signature || _rng.NextDouble() < DisruptionChance;
        var phases = (MultiPhase || signature)
            ? BuildMultiPhase(scenario, signature, allowElec)
            : BuildSinglePhase(scenario, visual, allowElec);

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
                if (ph.Whoosh && WhooshEnabled) _audio.PlayWhoosh(WhooshVolume);

                if (LightsEnabled && ph.Light is LightPattern pat)
                {
                    if (!lightsStarted) { _lights.Start(pat, mothership ? 1 : LightCount, ph.Dur, title, SpawnAsAircraft); lightsStarted = true; }
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

    private List<Phase> BuildSinglePhase(EncounterScenario s, double visual, bool allowElec) => new()
    {
        new Phase("Encounter", visual, s.Lights, allowElec ? s.Disruption : DisruptionKind.None,
            Proximity: s.Mothership ? 0.5 : 0.6, Whoosh: true, SubBass: s.Mothership, Silence: false),
    };

    private List<Phase> BuildMultiPhase(EncounterScenario s, bool signature, bool allowElec)
    {
        double d = Dread;
        var approach = s.Mothership || signature ? LightPattern.Mothership : LightPattern.PopUp;
        var observe = s.Mothership || signature ? LightPattern.Mothership : LightPattern.Wingman;
        // Mothership encounters keep the slow object even on departure (it just
        // vanishes); others dart away.
        bool ship = s.Mothership || signature;
        var climax = signature ? LightPattern.Mothership : (s.Lights ?? LightPattern.TicTac);
        var leave = ship ? LightPattern.Mothership : LightPattern.TicTac;

        var escalationDisruption = !allowElec
            ? DisruptionKind.None
            : signature
                ? DisruptionKind.DeepBlackout
                : (s.Disruption != DisruptionKind.None ? s.Disruption
                   : (d > 0.6 ? DisruptionKind.Stutter : DisruptionKind.None));

        // A flicker may collapse into a blackout (the requested transition).
        if (escalationDisruption == DisruptionKind.Stutter && _rng.NextDouble() < 0.5)
            escalationDisruption = DisruptionKind.StutterToBlackout;

        double escDur = signature ? 16 + 12 * d : 4 + 8 * d;

        return new List<Phase>
        {
            new("Annäherung", 3.5 + 2 * (1 - d), approach, DisruptionKind.None,
                Proximity: 0.25, Whoosh: true, SubBass: false, Silence: false),
            new("Beobachtung", 4 + 3 * (1 - d), observe, DisruptionKind.None,
                Proximity: 0.5, Whoosh: false, SubBass: true, Silence: false),
            new("Eskalation", escDur, climax, escalationDisruption,
                Proximity: Math.Min(1.0, 0.85 + 0.15 * d + (signature ? 0.15 : 0)), Whoosh: true, SubBass: true, Silence: false),
            new("Abgang", 2.5, leave, DisruptionKind.None,
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
                CutEngine = DeepBlackoutCutsEngine,
            };
        }

        if (kind == DisruptionKind.StutterToBlackout)
        {
            double lo = Math.Min(BlackoutMinSec, BlackoutMaxSec);
            double hi = Math.Max(BlackoutMinSec, BlackoutMaxSec);
            return new DisruptionPlan
            {
                Kind = DisruptionKind.StutterToBlackout,
                Duration = TimeSpan.FromSeconds(Math.Min(phaseDur, 4)), // flicker phase
                BlackoutDuration = TimeSpan.FromSeconds(lo + _rng.NextDouble() * (hi - lo)),
                EscalateChance = 0.6,
                CutEngine = DeepBlackoutCutsEngine,
                StutterStep = TimeSpan.FromMilliseconds(450 - 250 * Intensity),
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
        double statTarget = (StaticEnabled && _disruptor.IsActive) ? 0.45 * (0.5 + 0.5 * Intensity) : 0;

        _droneVol = Lerp(_droneVol, droneTarget, 0.12);
        _subVol = Lerp(_subVol, subTarget, 0.12);
        _statVol = Lerp(_statVol, statTarget, 0.20);

        _audio.SetDroneVolume(_droneVol);
        _audio.SetSubBassVolume(_subVol);
        _audio.SetStaticVolume(_statVol);
    }

    private static double Lerp(double a, double b, double k) => a + (b - a) * k;

    /// <summary>The "Holy Grail" (Tempus Fugit): UFO dances → vanishes →
    /// power + engine die in the dark → a bright beam appears overhead with a
    /// jumpscare, hovers in total silence → vanishes → systems restart.</summary>
    public async Task RunHolyGrailAsync()
    {
        if (IsEncounterActive) return;
        if (!_sim.IsConnected) { _trace?.Invoke("Holy Grail: not connected."); return; }
        if (_sim.State.OnGround > 0.5) { _trace?.Invoke("Holy Grail: skipped (on ground)."); return; }
        if (_sim.State.AltitudeAgl < 1000)
            _trace?.Invoke("⚠ Holy Grail: Motor wird abgeschaltet — auf ausreichende Höhe achten!");
        var ct = (_current = new CancellationTokenSource()).Token;
        IsEncounterActive = true;
        EncounterStateChanged?.Invoke("Holy Grail", true);
        _trace?.Invoke("✦✦ HOLY GRAIL (Tempus Fugit) ✦✦");
        _log.Record("Holy Grail ✦✦", _sim.State);

        double savedMin = _lights.MinDistanceMeters;
        Task elec = Task.CompletedTask;
        try
        {
            // 1) UFO appears and dances; electronics MAY misbehave.
            _audio.StartLayers();
            _audio.SetDroneVolume(Math.Clamp(0.55 * (0.4 + 0.6 * Intensity), 0, 1));
            _audio.SetSubBassVolume(0.4);
            string ufoTitle = !string.IsNullOrWhiteSpace(LightObjectTitle) ? LightObjectTitle : MothershipTitle;
            if (LightsEnabled) _lights.Start(LightPattern.PopUp, Math.Max(1, LightCount), 8, ufoTitle, SpawnAsAircraft);
            if (_rng.NextDouble() < 0.5)
                elec = _disruptor.RunAsync(new DisruptionPlan
                {
                    Kind = DisruptionKind.Stutter,
                    Duration = TimeSpan.FromSeconds(5),
                    StutterStep = TimeSpan.FromMilliseconds(300),
                }, ct);
            await Task.Delay(TimeSpan.FromSeconds(8), ct);

            // 2) UFO suddenly gone; brief quiet.
            _lights.Stop();
            try { await elec; } catch (OperationCanceledException) { }
            _audio.HardSilence();

            // 3) Power AND engine fail for certain; dramatic dark seconds.
            double darkBefore = 4 + 2 * _rng.NextDouble();
            double beamDur = 5 + 2 * _rng.NextDouble();
            elec = _disruptor.RunAsync(new DisruptionPlan
            {
                Kind = DisruptionKind.DeepBlackout,
                Duration = TimeSpan.FromSeconds(darkBefore + beamDur + 1.0), // restore just after the beam
                CutEngine = true,
            }, ct);
            await Task.Delay(TimeSpan.FromSeconds(darkBefore), ct);

            // 4) The beam appears overhead + the jumpscare. Total silence otherwise.
            _audio.PlayJumpscare(0.5);
            string beamTitle = !string.IsNullOrWhiteSpace(BeamTitle) ? BeamTitle
                : (!string.IsNullOrWhiteSpace(MothershipTitle) ? MothershipTitle : LightObjectTitle);
            _lights.MinDistanceMeters = 0;            // the beam must stay close above
            _lights.BeamHeight = BeamHeight;
            if (LightsEnabled) _lights.Start(LightPattern.Beam, Math.Max(1, BeamCount), beamDur, beamTitle, SpawnAsAircraft);
            await Task.Delay(TimeSpan.FromSeconds(beamDur), ct);

            // 5) Beam suddenly gone.
            _lights.Stop();

            // 6) Systems restart (the deep blackout restores power + engine).
            try { await elec; } catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _lights.MinDistanceMeters = savedMin;
            _audio.StopAll();
            _lights.Stop();
            IsEncounterActive = false;
            EncounterStateChanged?.Invoke("Holy Grail", false);
            _trace?.Invoke("■ Holy Grail ended.");
        }
    }

    /// <summary>Spawn lights on their own for a quick visual test.</summary>
    public void TestLights(LightPattern pattern, double durationSec, bool mothership = false)
    {
        if (!LightsEnabled) { _trace?.Invoke("Lights are disabled."); return; }
        string title = mothership ? MothershipTitle : LightObjectTitle;
        _lights.Start(pattern, mothership ? 1 : LightCount, durationSec, title, SpawnAsAircraft);
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
