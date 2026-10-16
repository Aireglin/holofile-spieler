using System.Windows.Threading;
using UfoEncounter.Audio;
using UfoEncounter.Sim;
using UfoEncounter.Util;

namespace UfoEncounter.Encounters;

/// <summary>Per-encounter tuning. Random mode rolls a fresh one; manual triggers
/// use the slider values. Never written back into the user's settings.</summary>
public sealed record EncounterTuning(int LightCount, double Dread, double SpeedScale, double MinDistance,
    double BlinkShare);

/// <summary>
/// The "director": picks and times encounters, either on demand or autonomously
/// (random mode). Each encounter runs as a chain of PHASES — optional arrival, a
/// few chained behaviours (one of them the scenario's own "core"), then a fly-away
/// — coordinating lights, electrics and a live, distance-driven audio mix. Only
/// one encounter or test runs at a time.
/// </summary>
public sealed class EncounterDirector
{
    private const double HolyGrailMinAglFeet = 1500;

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
    private CancellationTokenSource? _testCts;
    private int _testSession = -1;
    // Last "random mode waits" reason, so the log says it once instead of every tick.
    private string? _waitReason;

    // Live audio targets, consumed (and smoothed) by the audio timer.
    private double _proxTarget;
    private bool _subBassOn;
    private double _droneVol, _subVol, _statVol;
    private double _activeDread = 0.4;

    // ---- Tunables (bound to the UI) ----
    public bool RealismLock { get; set; } = true;
    public double MinAglFeet { get; set; } = 1500;
    private double _frequencyPerMin = 1.0;
    public double FrequencyPerMin
    {
        get => _frequencyPerMin;
        set
        {
            double oldF = Math.Max(0.05, _frequencyPerMin), newF = Math.Max(0.05, value);
            _frequencyPerMin = value;
            if (newF == oldF || !IsRandomMode || NextRandomEncounterUtc is not DateTime next) return;
            // Slider moved while random mode runs: rescale the remaining wait so the
            // change applies now (no new dice roll, seeded runs stay repeatable).
            double remaining = Math.Max(0, (next - DateTime.UtcNow).TotalSeconds);
            var gap = TimeSpan.FromSeconds(Math.Max(1, remaining * oldF / newF));
            _scheduler.Interval = gap;   // restarts the running timer from now
            NextRandomEncounterUtc = DateTime.UtcNow + gap;
        }
    }
    public double MinDurationSec { get; set; } = 4;
    public double MaxDurationSec { get; set; } = 12;
    public double Intensity { get; set; } = 0.6;
    /// <summary>0..1 — escalation aggressiveness, proximity and signature odds.</summary>
    public double Dread { get; set; } = 0.4;
    /// <summary>Run encounters as chained multi-phase mini-stories.</summary>
    public bool MultiPhase { get; set; } = true;
    public bool WhooshEnabled { get; set; } = false;
    public double WhooshVolume { get; set; } = 0.35;
    /// <summary>In random mode, bias scenario choice by time of day (night → lights,
    /// day → solid/mothership).</summary>
    public bool DayNightBias { get; set; } = true;
    /// <summary>Radio-static layer during failures (off: the hum alone is preferred).</summary>
    public bool StaticEnabled { get; set; } = false;

    public double BlackoutMinSec { get; set; } = 8;
    public double BlackoutMaxSec { get; set; } = 25;
    /// <summary>Probability (0..1) that an automatic encounter's electrical effect
    /// fires — "sometimes a failure, sometimes just lights".</summary>
    public double DisruptionChance { get; set; } = 0.75;
    public ElectricalMode Mode { get; set; } = ElectricalMode.SwitchesAndEngine;

    public string LightObjectTitle { get; set; } = "";
    public string MothershipTitle { get; set; } = "";
    public int LightCount { get; set; } = 2;
    public double SpeedScale { get; set; } = 0.6;
    public double MinDistanceMeters { get; set; }
    public bool LightsEnabled { get; set; } = true;
    /// <summary>0..1 — share of the lights besides the main light that blink.</summary>
    public double BlinkShare { get; set; } = 0.3;
    /// <summary>0..1 — chance that an encounter's main light gets orbiting sparks.</summary>
    public double SatelliteChance { get; set; } = 0.35;
    /// <summary>Pick light styles by role from the package (see <see cref="LightMix"/>);
    /// off = titles exactly as listed.</summary>
    public bool AutoMix { get; set; } = true;
    /// <summary>Pattern tests show satellites (not for the mothership).</summary>
    public bool TestWithSatellites { get; set; }
    /// <summary>Titles of the installed light package (null = not found); the mix
    /// only uses package titles that exist there or are listed.</summary>
    public IReadOnlySet<string>? InstalledTitles { get; set; }

    // Holy Grail "beam" — bright object(s) hovering above the aircraft.
    public string BeamTitle { get; set; } = "";
    public int BeamCount { get; set; } = 3;
    public double BeamHeight { get; set; } = 30;

    /// <summary>MSFS 2024 title of the stock F/A-18 Super Hornet.</summary>
    public const string DefaultJetTitle = "FA18E SuperHornet";

    /// <summary>Aircraft title for the chasing jet (default: the stock F/A-18).</summary>
    public string JetTitle { get; set; } = DefaultJetTitle;

    public bool IsRandomMode => _scheduler.IsEnabled;
    public bool IsEncounterActive { get; private set; }
    public DateTime? NextRandomEncounterUtc { get; private set; }

    public event Action<string, bool>? EncounterStateChanged; // (name, active)
    /// <summary>Short live status ("Szenario · Phase"), empty when idle.</summary>
    public event Action<string>? StatusChanged;
    public event Action<bool>? RandomModeChanged;

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

        // Connection gone (MSFS closed/crashed): end whatever runs. The finally blocks
        // silence the audio, remove objects, clear the status and free the stage. Cut
        // power stays pending in the disruptor and is replayed on reconnect.
        _sim.Disconnected += () =>
        {
            if (IsEncounterActive || _testCts != null)
                _trace?.Invoke("Verbindung getrennt – laufende Begegnung/Test abgebrochen.");
            _testCts?.Cancel();
            _current?.Cancel();
        };
    }

    private EncounterTuning UserTuning => new(LightCount, Dread, SpeedScale, MinDistanceMeters, BlinkShare);

    private EncounterTuning RandomTuning() => new(
        LightCount: _rng.Next(1, 6),                    // 1..5
        Dread: _rng.NextDouble(),                       // escalation + Holy-Grail odds
        SpeedScale: 0.4 + _rng.NextDouble() * 0.5,      // 0.4..0.9
        MinDistance: 120 + _rng.NextDouble() * 700,     // 120..820 m
        BlinkShare: Math.Pow(_rng.NextDouble(), 1.6) * 0.6);   // 0..60 %, mostly calm

    /// <summary>Seed every random source for (more) repeatable runs.</summary>
    public void SetSeed(int? seed)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        _lights.SetRandom(new Random(_rng.Next()));
        _disruptor.SetRandom(new Random(_rng.Next()));
        _trace?.Invoke(seed.HasValue ? $"Seed {seed} gesetzt." : "Seed: zufällig.");
    }

    public void SetRandomMode(bool on)
    {
        if (on == IsRandomMode) return;
        if (on)
        {
            ScheduleNext();
            _scheduler.Start();
            _trace?.Invoke("Zufallsmodus AN – Szenario, Anzahl, Muster, Abstand, Tempo, Dread und Blink-Anteil werden je Begegnung gewürfelt.");
        }
        else
        {
            _scheduler.Stop();
            NextRandomEncounterUtc = null;
            _trace?.Invoke("Zufallsmodus AUS.");
        }
        RandomModeChanged?.Invoke(on);
    }

    private void ScheduleNext()
    {
        double avgGap = 60.0 / Math.Max(0.05, FrequencyPerMin);
        var gap = TimeSpan.FromSeconds(avgGap * (0.5 + _rng.NextDouble()));
        _scheduler.Interval = gap;
        NextRandomEncounterUtc = DateTime.UtcNow + gap;
    }

    private bool Busy => IsEncounterActive || _testCts != null || _disruptor.IsActive;

    /// <summary>An encounter or test is running (random ticks are skipped meanwhile).</summary>
    public bool IsBusy => Busy;

    /// <summary>Claim the stage: refuse while an encounter or an electrical test
    /// runs, cancel a running pattern test.</summary>
    private bool TryBeginExclusive()
    {
        if (IsEncounterActive)
        {
            _trace?.Invoke("Es läuft bereits eine Begegnung – bitte warten oder PANIK-STOP.");
            return false;
        }
        if (_disruptor.IsActive)
        {
            // Not cancelled here: IsActive only clears in RunAsync's finally on a later
            // dispatcher pass, so the new encounter's first failure would be dropped.
            _trace?.Invoke("Es läuft gerade ein Elektrik-Test – bitte kurz warten oder PANIK-STOP.");
            return false;
        }
        var test = _testCts;
        _testCts = null;
        _testSession = -1;
        test?.Cancel();
        return true;
    }

    private async void OnSchedulerTick(object? sender, EventArgs e)
    {
        ScheduleNext();
        if (Busy) return;
        if (!_sim.IsConnected) { LogWait("Zufallsmodus wartet auf die Verbindung zu MSFS."); return; }
        if (!CanTriggerNow())   // connected here, so only the realism lock can refuse
        {
            LogWait("Zufallsmodus wartet (Realismus-Lock: am Boden oder unter Min-AGL).");
            return;
        }
        _waitReason = null;
        try { await RunRandomEncounterAsync(); }
        catch (Exception ex) { _trace?.Invoke($"Fehler im Zufallsmodus: {ex.Message}"); }
    }

    private void LogWait(string reason)
    {
        if (reason == _waitReason) return;
        _waitReason = reason;
        _trace?.Invoke(reason);
    }

    /// <summary>A fully randomized encounter (random mode, hotkey, "Begegnung jetzt").</summary>
    public async Task RunRandomEncounterAsync()
    {
        if (!_sim.IsConnected) { _trace?.Invoke("Keine Verbindung zum Sim."); return; }
        if (_rng.NextDouble() < 0.08) { await RunJetChaseAsync(); return; }   // rare scripted special

        var scenario = PickScenario();
        // Non-mothership scenarios get a randomized core pattern so the same
        // scenario never looks the same twice.
        if (!scenario.Mothership && _rng.NextDouble() < 0.75)
            scenario = scenario with { Lights = RandomLightPattern() };
        await TriggerAsync(scenario, manual: false, RandomTuning());
    }

    /// <summary>Weighted random scenario pick. With day/night bias on, night
    /// favours light-only scenarios and day favours the solid mothership.</summary>
    private EncounterScenario PickScenario()
    {
        var cat = EncounterScenario.Catalog;
        if (!DayNightBias) return cat[_rng.Next(cat.Count)];

        int tod = (int)Math.Round(_sim.State.TimeOfDay); // 0=dawn 1=day 2=dusk 3=night
        var weights = new double[cat.Count];
        double total = 0;
        for (int i = 0; i < cat.Count; i++)
        {
            double w = 1.0;
            bool hasLights = cat[i].Lights is not null && !cat[i].Mothership;
            if (tod == 3) w = cat[i].Mothership ? 0.6 : (hasLights ? 3.0 : 1.0); // night
            else if (tod == 1) w = cat[i].Mothership ? 3.0 : 1.0;                 // day
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

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    /// <summary>Object title for a role, falling back to the other title field.</summary>
    private string ResolveTitle(bool ship) => ship
        ? FirstNonEmpty(MothershipTitle, LightObjectTitle)
        : FirstNonEmpty(LightObjectTitle, MothershipTitle);

    private bool IsDay => (int)Math.Round(_sim.State.TimeOfDay) == 1;

    /// <summary>Titles for one session (fixed at spawn): the role mix, or one plain
    /// pick per object with <see cref="AutoMix"/> off. Satellites only for non-ship
    /// sessions whose list allows them; then 1 main light + 3..6 sparks. Returns the
    /// titles and how many of them (at the end) are satellites.</summary>
    private (string[] Titles, int Satellites) SessionTitles(string csv, int count, bool ship,
        bool satellites, double blinkShare, bool smallFollowers = false)
    {
        var entries = LightMix.Entries(csv);
        count = ship ? 1 : Math.Clamp(count, 1, 8);
        if (!AutoMix) return (LightMix.PickUniform(entries, count, _rng), 0);
        bool orbit = satellites && !ship && LightMix.CanOrbit(entries, InstalledTitles);
        int leaders = orbit ? 1 : count;
        var titles = LightMix.Compose(entries, leaders, orbit ? Math.Clamp(count + 1, 3, 6) : 0,
            blinkShare, IsDay, smallFollowers, InstalledTitles, _rng);
        return (titles, Math.Max(0, titles.Length - leaders));
    }

    // ---- Phases ----

    private sealed record Phase(
        string Name, double Dur, LightPattern? Light, DisruptionKind Disruption,
        double Proximity, bool Whoosh, bool SubBass);

    /// <summary>Run a scenario. <paramref name="manual"/> = a scenario button was
    /// pressed: its electrical effect is guaranteed instead of rolled.</summary>
    public async Task TriggerAsync(EncounterScenario scenario, bool manual = false,
        EncounterTuning? tuning = null, bool? forceMultiPhase = null)
    {
        if (!TryBeginExclusive()) return;
        if (!_sim.IsConnected) { _trace?.Invoke("Keine Verbindung zum Sim."); return; }
        var tu = tuning ?? UserTuning;

        bool signature = _rng.NextDouble() < SignatureChance(tu.Dread);
        if (signature && _rng.NextDouble() < 0.5)
        {
            if (_sim.State.AltitudeAgl >= HolyGrailMinAglFeet) { await RunHolyGrailAsync(explicitTest: false); return; }
            _trace?.Invoke("(Holy Grail ausgelassen – zu niedrig für einen Motorausfall.)");
        }
        // Random extra failures follow the slider; a scenario button only
        // guarantees the scenario's OWN failure.
        bool allowRandom = signature || _rng.NextDouble() < DisruptionChance;
        bool allowCore = manual || allowRandom;
        bool chain = forceMultiPhase ?? MultiPhase;
        var phases = (chain || signature)
            ? BuildChain(scenario, signature, allowCore, allowRandom, tu.Dread)
            : BuildSinglePhase(scenario, RandomDuration(), allowCore);

        var ct = (_current = new CancellationTokenSource()).Token;
        IsEncounterActive = true;
        _activeDread = tu.Dread;
        EncounterStateChanged?.Invoke(scenario.Name, true);
        if (signature) _trace?.Invoke("✦ SIGNATURE-EVENT ✦");
        _trace?.Invoke($"▶ {scenario.Name}{(chain || signature ? " (mehrphasig)" : "")}");
        _log.Record(signature ? $"{scenario.Name} ✦" : scenario.Name, _sim.State);

        bool ship = scenario.Mothership || signature;
        string title = ResolveTitle(ship);
        // Titles are fixed at spawn, so roles and satellites hold for the whole chain;
        // Tic-Tac jumps are busy enough with fewer blinkers.
        bool satellites = !ship && _rng.NextDouble() < SatelliteChance;
        double blink = phases.Any(p => p.Light == LightPattern.TicTac) ? tu.BlinkShare / 2 : tu.BlinkShare;
        int session = -1;
        Task elec = Task.CompletedTask;

        _audio.StartLayers();
        _audioTimer.Start();
        try
        {
            foreach (var ph in phases)
            {
                _proxTarget = ph.Proximity;
                _subBassOn = ph.SubBass;
                if (ph.Whoosh && WhooshEnabled) _audio.PlayWhoosh(WhooshVolume);

                if (LightsEnabled && ph.Light is LightPattern pat)
                {
                    if (session < 0)
                    {
                        var (titles, sats) = SessionTitles(title, tu.LightCount, ship, satellites, blink);
                        session = _lights.Start(pat, titles, sats, ph.Dur, tu.SpeedScale, tu.MinDistance);
                    }
                    else
                        _lights.SetPattern(pat, ph.Dur);
                }

                if (ph.Disruption != DisruptionKind.None && elec.IsCompleted)
                    elec = _disruptor.RunAsync(BuildPlan(ph.Disruption, ph.Dur, signature), ct);

                StatusChanged?.Invoke($"{scenario.Name} · {ph.Name}");
                _trace?.Invoke($"  · {ph.Name} ({ph.Dur:0.#} s)");
                try { await Task.Delay(TimeSpan.FromSeconds(ph.Dur), ct); }
                catch (OperationCanceledException) { break; }
            }

            // Fly-away finish: objects shoot off (the mothership glides away
            // majestically) into the distance before being despawned.
            if (session >= 0 && !ct.IsCancellationRequested)
            {
                _proxTarget = 0;
                _subBassOn = false;
                _lights.BeginDepart(ship, session);
                StatusChanged?.Invoke($"{scenario.Name} · Wegflug");
                try { await Task.Delay(TimeSpan.FromSeconds(ship ? 6 : 4.5), ct); }
                catch (OperationCanceledException) { }
            }

            _audioTimer.Stop();
            SilenceNow();
            if (session >= 0) _lights.Stop(session);
            if (!elec.IsCompleted) StatusChanged?.Invoke($"{scenario.Name} · Stromausfall hält an …");
            try { await elec; } catch (OperationCanceledException) { }
        }
        finally
        {
            _audioTimer.Stop();
            _audio.StopAll();
            if (session >= 0) _lights.Stop(session);
            IsEncounterActive = false;
            EncounterStateChanged?.Invoke(scenario.Name, false);
            StatusChanged?.Invoke("");
            _trace?.Invoke($"■ {scenario.Name} beendet.");
        }
    }

    private static double SignatureChance(double dread) => 0.015 + 0.05 * dread; // ~1.5% .. 6.5%

    private List<Phase> BuildSinglePhase(EncounterScenario s, double visual, bool allowElec) => new()
    {
        new Phase(s.Name, visual, s.Lights, allowElec ? s.Disruption : DisruptionKind.None,
            Proximity: s.Mothership ? 0.5 : 0.6, Whoosh: true, SubBass: s.Mothership),
    };

    private static readonly LightPattern[] ChainPatterns =
    {
        LightPattern.PaceAhead, LightPattern.Abeam, LightPattern.Above, LightPattern.Below,
        LightPattern.Wingman, LightPattern.Formation, LightPattern.PopUp, LightPattern.Orbit,
        LightPattern.Inspect, LightPattern.TicTac,
    };

    private LightPattern RandomLightPattern() => ChainPatterns[_rng.Next(ChainPatterns.Length)];

    /// <summary>Builds a randomized chain of patterns with sprinkled failures, so an
    /// encounter flows from one behaviour into another. One segment is the
    /// scenario's own "core" (its pattern, and its failure when it has one), so
    /// a scenario button always delivers that scenario. Mothership/signature stay
    /// slow and majestic. The fly-away finish is added by the caller.</summary>
    private List<Phase> BuildChain(EncounterScenario s, bool signature, bool allowCore, bool allowRandom, double d)
    {
        var phases = new List<Phase>();
        bool ship = s.Mothership || signature;

        if (ship)
        {
            phases.Add(new("Annäherung", 5 + 3 * (1 - d), LightPattern.Mothership, DisruptionKind.None,
                Proximity: 0.3, Whoosh: true, SubBass: false));
            var disr = DisruptionKind.None;
            if (allowCore && (signature || s.Disruption == DisruptionKind.DeepBlackout)) disr = DisruptionKind.DeepBlackout;
            else if (allowCore && s.Disruption != DisruptionKind.None) disr = s.Disruption;
            else if (allowRandom && _rng.NextDouble() < 0.5 + 0.4 * d) disr = DisruptionKind.DeepBlackout;
            phases.Add(new("Präsenz", signature ? 16 + 12 * d : 10 + 10 * d, LightPattern.Mothership, disr,
                Proximity: Math.Min(1.0, 0.6 + 0.3 * d), Whoosh: false, SubBass: true));
            return phases;
        }

        // Optional arrival.
        double r = _rng.NextDouble();
        if (r < 0.35)
            phases.Add(new(PatternNames.German(LightPattern.BrakeHover), 5, LightPattern.BrakeHover,
                DisruptionKind.None, Proximity: 0.85, Whoosh: true, SubBass: false));
        else if (r < 0.65)
            phases.Add(new(PatternNames.German(LightPattern.Approach), 9 + 4 * _rng.NextDouble(), LightPattern.Approach,
                DisruptionKind.None, Proximity: 0.4, Whoosh: false, SubBass: false));

        // 2–4 chained behaviours; one is the scenario's core.
        int segs = _rng.Next(2, 5);
        int core = _rng.Next(segs);
        // Only one failure runs at a time. When the core carries the scenario's own
        // failure, the segments before it stay calm so a still-running random
        // failure can never swallow it.
        bool coreOwnsFailure = allowCore && s.Disruption != DisruptionKind.None;
        for (int i = 0; i < segs; i++)
        {
            LightPattern pat;
            var disr = DisruptionKind.None;
            if (i == core)
            {
                pat = s.Lights ?? RandomLightPattern();
                if (coreOwnsFailure) disr = s.Disruption;
                else if (allowRandom && _rng.NextDouble() < 0.35 + 0.4 * d) disr = RandomDisruption();
            }
            else
            {
                pat = RandomLightPattern();
                bool mayFail = allowRandom && !(coreOwnsFailure && i < core);
                if (mayFail && _rng.NextDouble() < 0.35 + 0.4 * d) disr = RandomDisruption();
            }
            bool sub = disr != DisruptionKind.None || _rng.NextDouble() < 0.5;
            phases.Add(new(PatternNames.German(pat), SegDuration(pat), pat, disr,
                Proximity: ProximityFor(pat), Whoosh: false, SubBass: sub));
        }
        return phases;
    }

    private double SegDuration(LightPattern p) => p switch
    {
        LightPattern.Inspect or LightPattern.Orbit => 18 + _rng.NextDouble() * 22,   // long sightings
        LightPattern.PaceAhead or LightPattern.Abeam or LightPattern.Above
            or LightPattern.Below or LightPattern.Wingman or LightPattern.Formation => 8 + _rng.NextDouble() * 12,
        LightPattern.TicTac => 4 + _rng.NextDouble() * 5,
        _ => 6 + _rng.NextDouble() * 6,
    };

    private DisruptionKind RandomDisruption()
    {
        double r = _rng.NextDouble();
        if (r < 0.5) return DisruptionKind.Stutter;
        if (r < 0.8) return DisruptionKind.StutterToBlackout;
        return DisruptionKind.DeepBlackout;
    }

    private static double ProximityFor(LightPattern p) => p switch
    {
        LightPattern.PaceAhead or LightPattern.Abeam or LightPattern.Wingman or LightPattern.BrakeHover => 0.8,
        LightPattern.Above or LightPattern.Below => 0.75,
        LightPattern.TicTac => 0.7,
        LightPattern.Inspect or LightPattern.PopUp or LightPattern.Formation => 0.6,
        LightPattern.Orbit => 0.4,
        _ => 0.5,
    };

    private bool CutEngine => Mode != ElectricalMode.Switches;
    private bool FullShutdown => Mode == ElectricalMode.FullShutdown;

    private DisruptionPlan BuildPlan(DisruptionKind kind, double phaseDur, bool signature)
    {
        double lo = Math.Min(BlackoutMinSec, BlackoutMaxSec);
        double hi = Math.Max(BlackoutMinSec, BlackoutMaxSec);
        var step = TimeSpan.FromMilliseconds(450 - 250 * Intensity);

        if (kind == DisruptionKind.DeepBlackout)
        {
            double dur = lo + _rng.NextDouble() * (hi - lo);
            if (signature) dur = Math.Max(dur, hi); // signature: long & dark
            return new DisruptionPlan
            {
                Kind = DisruptionKind.DeepBlackout,
                StartDelay = TimeSpan.FromSeconds(_rng.NextDouble() * 3),
                Duration = TimeSpan.FromSeconds(dur),
                CutEngine = CutEngine,
                UseFullShutdown = FullShutdown,
            };
        }

        if (kind == DisruptionKind.StutterToBlackout)
        {
            return new DisruptionPlan
            {
                Kind = DisruptionKind.StutterToBlackout,
                Duration = TimeSpan.FromSeconds(Math.Min(phaseDur, 4)), // flicker phase
                BlackoutDuration = TimeSpan.FromSeconds(lo + _rng.NextDouble() * (hi - lo)),
                EscalateChance = 0.6,
                CutEngine = CutEngine,
                UseFullShutdown = FullShutdown,
                StutterStep = step,
            };
        }

        return new DisruptionPlan { Kind = kind, Duration = TimeSpan.FromSeconds(phaseDur), StutterStep = step };
    }

    /// <summary>Test plans for the single-effect buttons (same rules as encounters).</summary>
    public DisruptionPlan TestPlan(DisruptionKind kind) => kind switch
    {
        DisruptionKind.DeepBlackout => new DisruptionPlan
        {
            Kind = kind, Duration = TimeSpan.FromSeconds(8), CutEngine = CutEngine, UseFullShutdown = FullShutdown,
        },
        DisruptionKind.StutterToBlackout => new DisruptionPlan
        {
            Kind = kind, Duration = TimeSpan.FromSeconds(4), BlackoutDuration = TimeSpan.FromSeconds(8),
            EscalateChance = 1.0, CutEngine = CutEngine, UseFullShutdown = FullShutdown,
            StutterStep = TimeSpan.FromMilliseconds(450 - 250 * Intensity),
        },
        _ => new DisruptionPlan
        {
            Kind = kind, Duration = TimeSpan.FromSeconds(6),
            StutterStep = TimeSpan.FromMilliseconds(450 - 250 * Intensity),
        },
    };

    // ---- Live audio mix (distance-driven, smoothed) ----

    private void OnAudioTick(object? sender, EventArgs e)
    {
        // Real proximity from the nearest spawned object overrides the phase floor.
        double proxFromLights = 0;
        if (_lights.NearestMeters is double m)
            proxFromLights = Math.Clamp(1 - (m - 60) / 1400.0, 0, 1);
        double prox = Math.Max(_proxTarget, proxFromLights);

        double droneTarget = Math.Clamp(Intensity * (0.15 + 0.85 * prox), 0, 1);
        double subTarget = _subBassOn ? Math.Clamp((0.4 + 0.6 * _activeDread) * prox, 0, 1) : 0;
        double statTarget = (StaticEnabled && _disruptor.IsActive) ? 0.45 * (0.5 + 0.5 * Intensity) : 0;

        _droneVol = Lerp(_droneVol, droneTarget, 0.12);
        _subVol = Lerp(_subVol, subTarget, 0.12);
        _statVol = Lerp(_statVol, statTarget, 0.20);

        _audio.SetDroneVolume(_droneVol);
        _audio.SetSubBassVolume(_subVol);
        _audio.SetStaticVolume(_statVol);
    }

    /// <summary>Total silence now — also resets the mix so the timer can't fade it back.</summary>
    private void SilenceNow()
    {
        _proxTarget = 0;
        _subBassOn = false;
        _droneVol = _subVol = _statVol = 0;
        _audio.HardSilence();
    }

    private static double Lerp(double a, double b, double k) => a + (b - a) * k;

    /// <summary>The "Holy Grail" (Tempus Fugit): UFO dances → vanishes →
    /// power + engine die in the dark → a bright beam appears overhead with a
    /// jumpscare, hovers in total silence → vanishes → systems restart.</summary>
    public async Task RunHolyGrailAsync(bool explicitTest = true)
    {
        if (!TryBeginExclusive()) return;
        if (!_sim.IsConnected) { _trace?.Invoke("Holy Grail: keine Verbindung zum Sim."); return; }
        if (_sim.State.OnGround > 0.5) { _trace?.Invoke("Holy Grail: übersprungen (am Boden)."); return; }
        if (explicitTest && _sim.State.AltitudeAgl < 1000)
            _trace?.Invoke("⚠ Holy Grail: Der Motor wird abgeschaltet – auf ausreichende Höhe achten!");

        var ct = (_current = new CancellationTokenSource()).Token;
        IsEncounterActive = true;
        _activeDread = Dread;
        EncounterStateChanged?.Invoke("Holy Grail", true);
        _trace?.Invoke("✦✦ HOLY GRAIL (Tempus Fugit) ✦✦");
        _log.Record("Holy Grail ✦✦", _sim.State);

        Task elec = Task.CompletedTask;
        try
        {
            // 1) UFO appears and dances; electronics MAY misbehave.
            _audio.StartLayers();
            _audio.SetDroneVolume(Math.Clamp(0.55 * (0.4 + 0.6 * Intensity), 0, 1));
            _audio.SetSubBassVolume(0.4);
            if (LightsEnabled)
                _lights.Start(LightPattern.PopUp,
                    SessionTitles(ResolveTitle(false), Math.Max(1, LightCount), false, false, BlinkShare).Titles,
                    0, 8, SpeedScale, 0);
            StatusChanged?.Invoke("Holy Grail · Das UFO tanzt");
            if (_rng.NextDouble() < 0.5)
                elec = _disruptor.RunAsync(new DisruptionPlan
                {
                    Kind = DisruptionKind.Stutter,
                    Duration = TimeSpan.FromSeconds(5),
                    StutterStep = TimeSpan.FromMilliseconds(300),
                }, ct);
            await Task.Delay(TimeSpan.FromSeconds(8), ct);

            // 2) UFO suddenly gone; quiet.
            _lights.Stop();
            try { await elec; } catch (OperationCanceledException) { }
            SilenceNow();

            // 3) Power AND engine fail for certain; dramatic dark seconds.
            double darkBefore = 4 + 2 * _rng.NextDouble();
            double beamDur = 8 + _rng.NextDouble() * 9;   // 8–17 s overhead
            StatusChanged?.Invoke("Holy Grail · Dunkelheit");
            elec = _disruptor.RunAsync(new DisruptionPlan
            {
                Kind = DisruptionKind.DeepBlackout,
                Duration = TimeSpan.FromSeconds(darkBefore + beamDur + 1.0), // restore just after the beam
                CutEngine = true,
                UseFullShutdown = FullShutdown,
            }, ct);
            await Task.Delay(TimeSpan.FromSeconds(darkBefore), ct);

            // 4) The beam appears overhead + the jumpscare. Total silence otherwise.
            _audio.PlayJumpscare(0.5);
            _lights.BeamHeight = BeamHeight;
            // A bright centre with a ring of small sparks (with package titles).
            if (LightsEnabled)
                _lights.Start(LightPattern.Beam,
                    SessionTitles(FirstNonEmpty(BeamTitle, MothershipTitle, LightObjectTitle), Math.Max(1, BeamCount),
                        false, false, BlinkShare, smallFollowers: true).Titles,
                    0, beamDur, SpeedScale, 0);
            StatusChanged?.Invoke("Holy Grail · Lichtstrahl");
            await Task.Delay(TimeSpan.FromSeconds(beamDur), ct);

            // 5) Beam suddenly gone. 6) Systems restart (the blackout restores).
            _lights.Stop();
            StatusChanged?.Invoke("Holy Grail · Systeme starten neu");
            try { await elec; } catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _audio.StopAll();
            _lights.Stop();
            IsEncounterActive = false;
            EncounterStateChanged?.Invoke("Holy Grail", false);
            StatusChanged?.Invoke("");
            _trace?.Invoke("■ Holy Grail beendet.");
        }
    }

    /// <summary>JAL1628-style chase: the UFO sits ahead, then accelerates away
    /// at impossible speed; moments later a fighter jet (F/A-18) overtakes very
    /// close, chasing it — too slow to catch up.</summary>
    public async Task RunJetChaseAsync()
    {
        if (!TryBeginExclusive()) return;
        if (!_sim.IsConnected) { _trace?.Invoke("Jet-Verfolgung: keine Verbindung zum Sim."); return; }
        if (_sim.State.OnGround > 0.5) { _trace?.Invoke("Jet-Verfolgung: übersprungen (am Boden)."); return; }

        var ct = (_current = new CancellationTokenSource()).Token;
        IsEncounterActive = true;
        EncounterStateChanged?.Invoke("Jet-Verfolgung", true);
        _trace?.Invoke("▶ Jet-Verfolgung (JAL1628-Stil)");
        _log.Record("Jet-Verfolgung", _sim.State);

        // Empty jet title: fall back to the UFO titles.
        string jetTitle = FirstNonEmpty(JetTitle, MothershipTitle, LightObjectTitle);
        try
        {
            _audio.StartLayers();
            _audio.SetDroneVolume(Math.Clamp(0.5 * (0.4 + 0.6 * Intensity), 0, 1));
            _audio.SetSubBassVolume(0.35);

            // 1) UFO paces ahead: a single steady light.
            if (LightsEnabled)
                _lights.Start(LightPattern.PopUp, SessionTitles(ResolveTitle(true), 1, true, false, BlinkShare).Titles,
                    0, 7, SpeedScale, 0);
            StatusChanged?.Invoke("Jet-Verfolgung · UFO voraus");
            await Task.Delay(TimeSpan.FromSeconds(5 + _rng.NextDouble() * 4), ct);

            // 2) UFO accelerates away into the distance, then is gone.
            _audio.SetSubBassVolume(0.7);
            _lights.BeginDepart(majestic: false);
            StatusChanged?.Invoke("Jet-Verfolgung · UFO schießt davon");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            _lights.Stop();
            _audio.SetSubBassVolume(0.2);

            // 3) Beat, then the jet overtakes very close.
            await Task.Delay(TimeSpan.FromSeconds(2 + _rng.NextDouble() * 2), ct);
            if (jetTitle.Length == 0)
            {
                _trace?.Invoke("Kein Jet- oder UFO-Titel gesetzt – Jet-Phase übersprungen (Tab „Objekte & Lichter“).");
            }
            else if (LightsEnabled)
            {
                _lights.Start(LightPattern.Overtake, 1, 10, jetTitle, SpeedScale, 0);
                StatusChanged?.Invoke("Jet-Verfolgung · Jet überholt");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                _lights.Stop();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _audio.StopAll();
            _lights.Stop();
            IsEncounterActive = false;
            EncounterStateChanged?.Invoke("Jet-Verfolgung", false);
            StatusChanged?.Invoke("");
            _trace?.Invoke("■ Jet-Verfolgung beendet.");
        }
    }

    /// <summary>Preview one pattern, ending with the same fly-away as a real
    /// encounter. Cancellable; a newer test replaces an older one; never runs
    /// during an encounter.</summary>
    public async void TestLights(LightPattern pattern, double durationSec, bool mothership = false)
    {
        if (IsEncounterActive) { _trace?.Invoke("Es läuft eine Begegnung – Test übersprungen."); return; }
        if (!LightsEnabled) { _trace?.Invoke("Objekte sind ausgeschaltet (Tab „Objekte & Lichter“)."); return; }

        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        bool satellites = TestWithSatellites && !mothership;
        var (titles, sats) = SessionTitles(ResolveTitle(mothership), LightCount, mothership, satellites, BlinkShare);
        if (satellites && sats == 0)
            _trace?.Invoke("Trabanten brauchen Titel aus dem Licht-Paket (UFOE_…) und „Lichtstile automatisch mischen“ "
                           + "(bei älterem Licht-Paket: neu erzeugen und MSFS neu starten) – Test ohne Trabanten.");
        int session = _lights.Start(pattern, titles, sats, durationSec, SpeedScale, MinDistanceMeters);
        if (session < 0)
        {
            if (_testCts == cts) _testCts = null;
            return;
        }
        _testSession = session;
        StatusChanged?.Invoke($"Test · {PatternNames.German(pattern)}");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSec), cts.Token);
            _lights.BeginDepart(mothership, session);
            StatusChanged?.Invoke("Test · Wegflug");
            await Task.Delay(TimeSpan.FromSeconds(mothership ? 6 : 4.5), cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _lights.Stop(session);
            if (_testCts == cts) { _testCts = null; _testSession = -1; }
            if (_testCts == null && !IsEncounterActive) StatusChanged?.Invoke("");
        }
    }

    private const double DiagnosticSeconds = 20;

    /// <summary>The package's test objects in the order they are shown, each with what
    /// to look for. Answers settle node offsets, the flash_phase unit and whether a
    /// flare-only light and the older light format are visible.</summary>
    private static readonly (string Title, string Look)[] DiagnosticSteps =
    {
        (LightPackageGenerator.TitlePrefix + "Test_Versatz",
            "Siehst du DREI Punkte (rot, grün, weiß – etwa 4 m auseinander) oder nur EINEN? Wenn drei: welche Farbe ist links, rechts, oben?"),
        (LightPackageGenerator.TitlePrefix + "Test_Phase_Sek",
            "Vier Farben (rot, grün, blau, weiß) in einem kleinen waagrechten Quadrat – von hinten wirken sie oft wie zwei Punkte. "
            + "Laufen die Farben nacheinander durch (Lauflicht: rot → grün → blau → weiß), oder blinken alle gleichzeitig?"),
        (LightPackageGenerator.TitlePrefix + "Test_Phase_Anteil",
            "Wieder die vier Farben. Lauflicht (gleichmäßig nacheinander, ca. 1 s Abstand), schnell hintereinander, oder alle gleichzeitig? Anders als eben?"),
        (LightPackageGenerator.TitlePrefix + "Test_FlareOnly",
            "Siehst du einen weißen Lichtpunkt – ja oder nein?"),
        (LightPackageGenerator.TitlePrefix + "Test_Macro_Weiss",
            "Älteres Lichtformat (MSFS 2020): siehst du einen weißen Lichtpunkt – ja oder nein?"),
    };

    /// <summary>Night test of the package's diagnostic objects: one after another,
    /// ~20 s each, ahead of the aircraft, with a log line saying what to look for.
    /// Runs like a pattern test (cancelled by PANIK-STOP, an encounter or another
    /// test); refused during an encounter.</summary>
    public async Task RunDiagnosticsAsync()
    {
        if (IsEncounterActive) { _trace?.Invoke("Es läuft eine Begegnung – Diagnose übersprungen."); return; }
        if (!LightsEnabled) { _trace?.Invoke("Objekte sind ausgeschaltet (Tab „Objekte & Lichter“)."); return; }
        if (!_sim.IsConnected) { _trace?.Invoke("Diagnose: keine Verbindung zum Sim."); return; }
        if (_sim.State.OnGround > 0.5) { _trace?.Invoke("Diagnose: bitte in der Luft starten (am Boden erscheinen keine Objekte)."); return; }
        var installed = InstalledTitles;
        // All or nothing: the closing question covers every step.
        if (installed == null || DiagnosticSteps.Any(d => !installed.Contains(d.Title)))
        {
            _trace?.Invoke(installed == null
                ? "Diagnose: Licht-Paket nicht gefunden. Erst im Tab „Objekte & Lichter“ auf „Licht-Paket erzeugen / aktualisieren“ klicken und MSFS neu starten."
                : "Diagnose: Im installierten Licht-Paket fehlen die Test-Objekte (ältere Version). Bitte „Licht-Paket erzeugen / aktualisieren“ klicken und MSFS neu starten.");
            return;
        }
        if (!TryBeginExclusive()) return;

        var steps = DiagnosticSteps;
        // _testSession stays -1: the live sliders must not move the test objects.
        var cts = _testCts = new CancellationTokenSource();
        int session = -1;
        _trace?.Invoke($"Diagnose: {steps.Length} Test-Objekte nacheinander, je ca. {DiagnosticSeconds:0} s voraus (am besten nachts). "
                       + "Merke dir bei jedem, was du siehst.");
        try
        {
            for (int i = 0; i < steps.Length; i++)
            {
                var (title, look) = steps[i];
                _trace?.Invoke($"Diagnose {i + 1}/{steps.Length} · {title}: {look}");
                StatusChanged?.Invoke($"Diagnose {i + 1}/{steps.Length} · {title}");
                session = _lights.Start(LightPattern.PaceAhead, new[] { title }, 0, DiagnosticSeconds, 0.3, 0);
                if (session < 0) return;   // Start logged why (connection lost, on the ground)
                await Task.Delay(TimeSpan.FromSeconds(DiagnosticSeconds), cts.Token);
                _lights.Stop(session);
                if (i < steps.Length - 1) await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            }
            _trace?.Invoke("Diagnose fertig. Bitte melde kurz für jeden Test, was du gesehen hast: "
                           + "1 Versatz – drei Punkte (welche Farbe wo?) oder einer · 2 Phase_Sek und 3 Phase_Anteil – "
                           + "Lauflicht, schnell hintereinander oder gleichzeitig · 4 FlareOnly – Punkt ja/nein · "
                           + "5 Macro – Punkt ja/nein. Ist ein Objekt gar nicht erschienen, auch das.");
        }
        catch (OperationCanceledException)
        {
            _trace?.Invoke("Diagnose abgebrochen.");
        }
        finally
        {
            if (session >= 0) _lights.Stop(session);
            if (_testCts == cts) _testCts = null;
            if (_testCts == null && !IsEncounterActive) StatusChanged?.Invoke("");
        }
    }

    /// <summary>Apply the speed / min-distance sliders live to a running pattern
    /// test (never to an encounter, so its tuning stays as rolled).</summary>
    public void ApplyLiveTestTuning()
    {
        if (_testCts != null && !IsEncounterActive && _testSession >= 0)
            _lights.UpdateLiveTuning(_testSession, SpeedScale, MinDistanceMeters);
    }

    private double RandomDuration()
    {
        double lo = Math.Min(MinDurationSec, MaxDurationSec);
        double hi = Math.Max(MinDurationSec, MaxDurationSec);
        return lo + _rng.NextDouble() * (hi - lo);
    }

    /// <summary>Stop everything immediately and restore power/engine synchronously.</summary>
    public void PanicStop()
    {
        SetRandomMode(false);
        _testCts?.Cancel();
        _current?.Cancel();
        _disruptor.AbortAndRestoreNow();
        _audioTimer.Stop();
        _audio.StopAll();
        _audio.StopJumpscare();
        _lights.Stop();
        StatusChanged?.Invoke("");
        _trace?.Invoke("⛔ PANIK-STOP – Strom/Motor wiederhergestellt, Objekte entfernt, Ton aus, Zufallsmodus aus.");
    }
}
