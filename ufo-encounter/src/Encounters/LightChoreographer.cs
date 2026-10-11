using System.Diagnostics;
using UfoEncounter.Sim;
using UfoEncounter.Util;

namespace UfoEncounter.Encounters;

public enum LightPattern
{
    /// <summary>Instant jumps to random points, no transition (Tic-Tac feel).</summary>
    TicTac,
    /// <summary>Holds station off the wing, mirroring the aircraft.</summary>
    Wingman,
    /// <summary>Sweeps from far ahead past the aircraft and away.</summary>
    FlyBy,
    /// <summary>Several lights in a loose V, gently oscillating ahead.</summary>
    Formation,
    /// <summary>Pops up ahead and dances in tight loops.</summary>
    PopUp,
    /// <summary>Hovers in a tight cluster directly above the aircraft (the
    /// "beam" — its objects' own lights bathe the plane from above).</summary>
    Beam,
    /// <summary>Slow, long, wide circle around the aircraft.</summary>
    Orbit,
    /// <summary>Sits ahead, then accelerates away forward extremely fast.</summary>
    Streak,
    /// <summary>Comes from far behind and overtakes very close, passing ahead.</summary>
    Overtake,
    /// <summary>Paces directly ahead of the aircraft.</summary>
    PaceAhead,
    /// <summary>Holds level, off to one side (formation abeam).</summary>
    Abeam,
    /// <summary>Holds above the aircraft.</summary>
    Above,
    /// <summary>Holds just below the aircraft.</summary>
    Below,
    /// <summary>Slowly transits between positions around you (long sighting).</summary>
    Inspect,
    /// <summary>Comes in from far away and settles into close formation.</summary>
    Approach,
    /// <summary>Shoots in from ahead, brakes hard ~12 m in front and hovers.</summary>
    BrakeHover,
    /// <summary>Accelerates away along its current bearing, then despawns.</summary>
    Depart,
    /// <summary>A single massive object drifting slowly around a far anchor.</summary>
    Mothership,
}

public static class PatternNames
{
    public static string German(LightPattern p) => p switch
    {
        LightPattern.TicTac => "Tic-Tac (Sprünge)",
        LightPattern.Wingman => "Wingman (hinten seitlich)",
        LightPattern.FlyBy => "Vorbeiflug",
        LightPattern.Formation => "Formation",
        LightPattern.PopUp => "Pop-up (Tanz vorn)",
        LightPattern.Beam => "Lichtstrahl",
        LightPattern.Orbit => "Umkreisung (weit)",
        LightPattern.Streak => "Davonschießen",
        LightPattern.Overtake => "Überholen",
        LightPattern.PaceAhead => "Begleiter vorn",
        LightPattern.Abeam => "Begleiter seitlich",
        LightPattern.Above => "Begleiter oben",
        LightPattern.Below => "Begleiter unten",
        LightPattern.Inspect => "Inspektion (nah, lang)",
        LightPattern.Approach => "Annäherung & Halten",
        LightPattern.BrakeHover => "Anschießen & Bremsen",
        LightPattern.Depart => "Wegflug",
        LightPattern.Mothership => "Mutterschiff",
        _ => p.ToString(),
    };
}

/// <summary>
/// Spawns SimObjects and animates them relative to the player aircraft. Offsets
/// are computed in an aircraft-local frame (right / forward / up, in metres) and
/// converted to lat/lon/alt.
///
/// Motion runs in lock-step with the simulator: every SIM_FRAME the fresh player
/// state arrives and all objects are repositioned immediately (no separate timer,
/// so there is no beat between our updates and the sim's frames). Time comes from
/// a high-resolution Stopwatch and smoothing is time-based, so motion stays even
/// regardless of frame rate. Pattern switches cross-fade from where each object
/// really is, so chained behaviours flow into each other.
/// </summary>
public sealed class LightChoreographer
{
    private const double MetersPerDegLat = 111320.0;
    private const double FeetPerMeter = 3.28084;
    private const double SmoothTau = 0.25;      // s, smoothing time constant
    private const double BlendSeconds = 2.5;    // cross-fade length on pattern change

    /// <summary>Per-object randomness, rolled per pattern.</summary>
    private sealed class Vars
    {
        public double Phase;        // 0..2π
        public double Speed;        // ~0.7..1.5 time multiplier
        public double Amp;          // ~0.6..1.6 size multiplier
        public int Side;            // ±1 left/right bias
        public double Dist;         // base distance multiplier ~0.7..1.4
        public double DartChance;   // 0..1 chance of an oversized jump (Tic-Tac)
        public double JumpMin, JumpMax; // Tic-Tac dwell range (s)
    }

    private sealed class Light
    {
        public uint Index;
        public uint Slot;
        public string Title = "";
        public bool AsAircraft;
        public uint? ObjectId;
        public double SpawnedAt;
        public double AssignedAt;
        public int FreezeSteps;
        public int LightsAsserted;
        public bool Warned;
        public Vars V = new();
        public double[] Current = new double[3];   // jump target / anchor (right, fwd, up)
        public double[] Smoothed = new double[3];
        public double[] From = new double[3];      // cross-fade origin
        public double[] LastSent = new double[3];  // what the sim really got
        public double[] DepartFrom = new double[3];
        public double MinR;                        // eased minimum-distance radius
        public double NextJump;
    }

    private readonly SimConnectClient _sim;
    private readonly Action<string>? _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Light> _lights = new();
    private Random _rng;

    private LightPattern _pattern;
    private double _durationSec;
    private double _sessionStart;   // continuous clock for wander/bank (never resets mid-session)
    private double _phaseStart;     // resets per pattern (progress-based patterns, Tic-Tac rhythm)
    private double _blendStart, _blendDur;
    private double _lastFrame = -1;
    private double _departAccel = 230;
    private double _speedScale = 0.6, _minDistance;
    private int _session;
    private uint _nextSlot;

    public bool IsActive { get; private set; }
    /// <summary>Distance (m) of the nearest spawned object, or null if none.</summary>
    public double? NearestMeters { get; private set; }
    /// <summary>Height (metres) of the Beam cluster above the aircraft.</summary>
    public double BeamHeight { get; set; } = 30;
    /// <summary>Switch on nav/strobe/beacon/logo lights of spawned aircraft.</summary>
    public bool ForceAircraftLights { get; set; } = true;

    /// <summary>Raised when the active pattern changes (for the status display).</summary>
    public event Action<LightPattern>? PatternChanged;

    public LightChoreographer(SimConnectClient sim, Action<string>? log = null, Random? rng = null)
    {
        _sim = sim;
        _log = log;
        _rng = rng ?? new Random();
        _sim.ObjectAssigned += OnObjectAssigned;
        _sim.StateUpdated += OnSimFrame;
        _sim.Disconnected += OnDisconnected;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    public void SetRandom(Random rng) => _rng = rng;

    /// <summary>Objects from the generated light package are plain SimObjects;
    /// every other title (captured from a loaded aircraft) is an aircraft.</summary>
    public static bool IsAircraftTitle(string title) =>
        !title.StartsWith(LightPackageGenerator.TitlePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Spawn objects for a new session. <paramref name="titles"/> may be a
    /// comma-separated list; each object picks one at random (mixed colours).
    /// Speed and minimum distance are fixed for the session. Returns the session
    /// id, or -1 if nothing was started.</summary>
    public int Start(LightPattern pattern, int count, double durationSec, string titles,
        double speedScale, double minDistance)
    {
        if (!_sim.IsConnected) { _log?.Invoke("Objekte: keine Verbindung zum Sim."); return -1; }
        var list = (titles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length == 0)
        {
            _log?.Invoke("Objekte: kein SimObject-Titel gesetzt (Tab „Objekte & Lichter“).");
            return -1;
        }
        var p = _sim.State;
        if (p.OnGround > 0.5) { _log?.Invoke("Objekte: übersprungen (Flugzeug steht am Boden)."); return -1; }

        Stop();
        _session++;
        _pattern = pattern;
        _durationSec = Math.Max(1, durationSec);
        _speedScale = Math.Clamp(speedScale, 0.1, 2.0);
        _minDistance = Math.Max(0, minDistance);
        double now = Now;
        _sessionStart = _phaseStart = now;
        _blendDur = 0;
        _lastFrame = -1;
        IsActive = true;

        count = pattern == LightPattern.Mothership ? 1 : Math.Clamp(count, 1, 8);
        for (uint i = 0; i < count; i++)
        {
            string title = list[_rng.Next(list.Length)];
            var l = new Light
            {
                Index = i,
                Slot = _nextSlot++ % SimConnectClient.SpawnSlots,
                Title = title,
                AsAircraft = IsAircraftTitle(title),
                SpawnedAt = now,
                V = RollVars(),
            };
            Seed(l);
            // Spawn exactly where the pattern starts — no teleport on the first frame.
            var off = OffsetFor(l, 0, 0);
            Array.Copy(off, l.Smoothed, 3);
            l.MinR = IgnoresMinDistance(pattern) ? 0 : _minDistance;
            ApplyMinRadius(l, off);
            _lights.Add(l);
            _sim.SpawnObject(title, ComposePose(p, l.LastSent, 0, l.V), l.Slot, l.AsAircraft);
        }
        _log?.Invoke($"Objekte: {count}× erzeugt – {PatternNames.German(pattern)}.");
        PatternChanged?.Invoke(pattern);
        return _session;
    }

    /// <summary>Switch the motion pattern live, keeping the spawned objects. Each
    /// object cross-fades from where it really is; Tic-Tac keeps its hard jump.
    /// <paramref name="durationSec"/> drives progress-based patterns (fly-by etc.).</summary>
    public void SetPattern(LightPattern pattern, double durationSec)
    {
        if (!IsActive) return;
        _durationSec = Math.Max(1, durationSec);
        if (pattern == _pattern) return;   // e.g. Mothership → Mothership: keep drifting

        double now = Now;
        foreach (var l in _lights)
        {
            Array.Copy(l.LastSent, l.From, 3);
            l.V = RollVars();
            if (pattern == LightPattern.TicTac) JumpTicTac(l, 0);
            else if (pattern == LightPattern.Mothership) JumpMothership(l);
        }
        _blendStart = now;
        _blendDur = pattern == LightPattern.TicTac ? 0 : BlendSeconds;
        _pattern = pattern;
        _phaseStart = now;
        PatternChanged?.Invoke(pattern);
    }

    /// <summary>Send every object off along its current bearing — fast, or slow and
    /// majestic for the mothership — before it is despawned. Ignored when
    /// <paramref name="session"/> is given and no longer current.</summary>
    public void BeginDepart(bool majestic, int session = -1)
    {
        if (!IsActive || (session >= 0 && session != _session)) return;
        _departAccel = majestic ? 55 : 230;
        foreach (var l in _lights)
            Array.Copy(l.LastSent, l.DepartFrom, 3);
        _pattern = LightPattern.Depart;
        _phaseStart = Now;
        _blendDur = 0;
        PatternChanged?.Invoke(LightPattern.Depart);
    }

    /// <summary>Remove all objects. Ignored when <paramref name="session"/> is given
    /// and no longer current (a stale timer must not kill a newer session).</summary>
    public void Stop(int session = -1)
    {
        if (session >= 0 && session != _session) return;
        foreach (var l in _lights)
            if (l.ObjectId is uint id) _sim.RemoveObject(id, l.Slot);
        // Spawns still in flight are removed when their assignment arrives (no match).
        _lights.Clear();
        IsActive = false;
        NearestMeters = null;
        _lastFrame = -1;
    }

    private void OnDisconnected()
    {
        // The sim dropped all objects; don't keep writing to dead ids.
        _lights.Clear();
        IsActive = false;
        NearestMeters = null;
    }

    private Vars RollVars() => new()
    {
        Phase = _rng.NextDouble() * Math.PI * 2,
        Speed = 0.7 + _rng.NextDouble() * 0.8,
        Amp = 0.6 + _rng.NextDouble() * 1.0,
        Side = _rng.Next(2) == 0 ? -1 : 1,
        Dist = 0.7 + _rng.NextDouble() * 0.7,
        DartChance = 0.12 + _rng.NextDouble() * 0.25,
        JumpMin = 0.3 + _rng.NextDouble() * 0.3,
        JumpMax = 0.7 + _rng.NextDouble() * 0.8,
    };

    /// <summary>Seed anchors for patterns that need one.</summary>
    private void Seed(Light l)
    {
        if (_pattern == LightPattern.Mothership) JumpMothership(l);
        else if (_pattern == LightPattern.Beam) { l.Current[0] = 0; l.Current[1] = 0; l.Current[2] = BeamHeight; }
        else if (_pattern == LightPattern.TicTac) JumpTicTac(l, 0);
    }

    private void OnObjectAssigned(uint slot, uint objectId)
    {
        var l = _lights.Find(x => x.Slot == slot && x.ObjectId is null);
        if (l == null)
        {
            // Stale: its session already ended or was replaced. Don't leave a ghost.
            _sim.RemoveObject(objectId, slot);
            return;
        }
        l.ObjectId = objectId;
        l.AssignedAt = Now;
        _sim.ReleaseAndFreeze(objectId, slot);
    }

    private static bool IgnoresMinDistance(LightPattern p) =>
        p is LightPattern.Beam or LightPattern.BrakeHover or LightPattern.Overtake or LightPattern.Depart;

    /// <summary>Patterns whose path is already smooth/analytic (or intentionally
    /// instant) — smoothing them would only add lag.</summary>
    private static bool IsAnalytic(LightPattern p) =>
        p is LightPattern.TicTac or LightPattern.Depart or LightPattern.FlyBy or LightPattern.Overtake
          or LightPattern.Approach or LightPattern.BrakeHover or LightPattern.Streak;

    private void OnSimFrame(PlaneState p)
    {
        if (!IsActive || _lights.Count == 0) return;

        double now = Now;
        double dt = _lastFrame < 0 ? 1 / 30.0 : Math.Clamp(now - _lastFrame, 0.0, 0.1);
        _lastFrame = now;
        double tSession = now - _sessionStart;
        double tPhase = now - _phaseStart;
        double blendU = _blendDur > 0 ? Math.Clamp((now - _blendStart) / _blendDur, 0, 1) : 1;
        double blendS = blendU * blendU * (3 - 2 * blendU);
        double smoothA = 1 - Math.Exp(-dt / SmoothTau);
        double radiusA = 1 - Math.Exp(-dt / 0.8);
        bool analytic = IsAnalytic(_pattern);
        double targetR = IgnoresMinDistance(_pattern) ? 0 : _minDistance;
        double? nearest = null;

        foreach (var l in _lights)
        {
            if (l.ObjectId is not uint id)
            {
                if (!l.Warned && now - l.SpawnedAt > 5)
                {
                    l.Warned = true;
                    _log?.Invoke($"⚠ Objekt „{l.Title}“ ist nach 5 s nicht erschienen. Titel exakt prüfen "
                                 + "(oder per „Aktuelles Flugzeug übernehmen“ holen); bei Paket-Objekten MSFS neu starten.");
                }
                continue;
            }

            double since = now - l.AssignedAt;
            // Re-assert the freeze a few times during the object's first second.
            if (since <= 1.0)
            {
                int step = (int)(since / 0.2);
                if (step > l.FreezeSteps) { l.FreezeSteps = step; _sim.FreezeObject(id); }
            }
            // Switch aircraft lights on once the object has settled (twice, for robustness).
            if (ForceAircraftLights && l.AsAircraft &&
                ((l.LightsAsserted == 0 && since > 0.8) || (l.LightsAsserted == 1 && since > 2.5)))
            {
                _sim.SetObjectLights(id, true);
                l.LightsAsserted++;
            }

            var raw = OffsetFor(l, tPhase, tSession);
            double[] off;
            if (analytic)
            {
                off = raw;
                Array.Copy(raw, l.Smoothed, 3);    // keep the filter in sync for later switches
            }
            else
            {
                for (int i = 0; i < 3; i++) l.Smoothed[i] += (raw[i] - l.Smoothed[i]) * smoothA;
                off = l.Smoothed;
            }

            if (blendU < 1)
            {
                off = new[]
                {
                    Lerp(l.From[0], off[0], blendS),
                    Lerp(l.From[1], off[1], blendS),
                    Lerp(l.From[2], off[2], blendS),
                };
            }

            l.MinR += (targetR - l.MinR) * radiusA;
            ApplyMinRadius(l, off);

            double d = Math.Sqrt(l.LastSent[0] * l.LastSent[0] + l.LastSent[1] * l.LastSent[1] + l.LastSent[2] * l.LastSent[2]);
            if (nearest is null || d < nearest) nearest = d;

            _sim.MoveObject(id, ComposePose(p, l.LastSent, tSession, l.V));
            if (l.AsAircraft) _sim.ZeroVelocity(id);
        }
        NearestMeters = nearest;
    }

    /// <summary>Write <paramref name="off"/> into LastSent, pushed out to the eased
    /// minimum radius if it is closer (avoids close-range stutter).</summary>
    private static void ApplyMinRadius(Light l, double[] off)
    {
        double d = Math.Sqrt(off[0] * off[0] + off[1] * off[1] + off[2] * off[2]);
        if (l.MinR > 1 && d < l.MinR)
        {
            if (d > 1)
            {
                double s = l.MinR / d;
                l.LastSent[0] = off[0] * s; l.LastSent[1] = off[1] * s; l.LastSent[2] = off[2] * s;
            }
            else { l.LastSent[0] = 0; l.LastSent[1] = l.MinR; l.LastSent[2] = 0; }
            return;
        }
        Array.Copy(off, l.LastSent, 3);
    }

    /// <summary>Smooth, organic pseudo-noise (sum of incommensurate sines), ~[-1,1].</summary>
    private static double Wander(double t, double seed) =>
        0.55 * Math.Sin(t * 0.37 + seed)
        + 0.30 * Math.Sin(t * 0.83 + seed * 1.7)
        + 0.15 * Math.Sin(t * 1.49 + seed * 2.6);

    /// <summary>Local offset (right, fwd, up in metres). <paramref name="tp"/> is the
    /// time since the pattern began (progress), <paramref name="tsess"/> the
    /// continuous session time (oscillations, wander).</summary>
    private double[] OffsetFor(Light l, double tp, double tsess)
    {
        var v = l.V;
        double ph = v.Phase;
        int idx = (int)l.Index;
        int n = Math.Max(1, _lights.Count);
        double ts = tsess * v.Speed * _speedScale;

        // Organic wander per axis (different seeds), scaled by amplitude.
        double wr = Wander(ts, ph) * v.Amp;
        double wf = Wander(ts, ph + 2.1) * v.Amp;
        double wu = Wander(ts, ph + 4.7) * v.Amp;

        switch (_pattern)
        {
            case LightPattern.Wingman:
                return new[]
                {
                    v.Side * (40 + 30 * v.Dist) + 18 * wr,
                    -(45 + 25 * v.Dist) + idx * -18.0 + 14 * wf,
                    -2 + 10 * wu,
                };

            case LightPattern.FlyBy:
            {
                double u = Math.Clamp(tp / _durationSec, 0, 1);
                return new[]
                {
                    v.Side * (90 + 60 * v.Dist) + idx * 15 + 25 * wr,
                    Lerp(2200 + 2000 * v.Dist, -1500 - 800 * v.Dist, u),
                    (15 + 25 * v.Amp) * Math.Sin(u * Math.PI) + 18 * wu,
                };
            }

            case LightPattern.Formation:
            {
                double spread = (idx - (n - 1) / 2.0) * (24 + 12 * v.Dist);
                return new[]
                {
                    spread + 16 * wr,
                    120 + 60 * v.Dist + Math.Abs(spread) * 0.6 + 22 * wf,
                    8 + 12 * wu,
                };
            }

            case LightPattern.PopUp:
                return new[]
                {
                    v.Side * (40 + 60 * v.Amp) * Math.Sin(ts * 1.4 + ph) + 30 * wr,
                    260 + 120 * v.Dist + 40 * Math.Sin(ts * 0.8 + ph) + 40 * wf,
                    15 + (20 + 18 * v.Amp) * Math.Sin(ts * 2.0 + ph * 1.3) + 25 * wu,
                };

            case LightPattern.Mothership:
                // A single massive object drifting slowly around a fixed far anchor.
                return new[]
                {
                    l.Current[0] + 350 * Math.Sin(ts * 0.05 + ph) + 130 * wr,
                    l.Current[1] + 420 * Math.Sin(ts * 0.035) + 170 * wf,
                    l.Current[2] + 130 * Math.Sin(ts * 0.045 + ph) + 90 * wu,
                };

            case LightPattern.Beam:
            {
                // Tight cluster directly overhead; a small ring widens the lit area.
                double ang = ph + idx * (2 * Math.PI / n);
                double ringR = 6 + 7 * idx;
                return new[]
                {
                    ringR * Math.Cos(ang) + 3 * wr,
                    ringR * Math.Sin(ang) + 3 * wf,
                    BeamHeight + 2 * Math.Sin(ts * 0.3 + ph),
                };
            }

            case LightPattern.Orbit:
            {
                double radius = 280 + 220 * v.Dist;
                double ang = ts * 0.12 + ph + idx * (2 * Math.PI / n);
                return new[]
                {
                    radius * Math.Cos(ang) + 20 * wr,
                    radius * Math.Sin(ang) + 20 * wf,
                    60 + 40 * Math.Sin(ts * 0.05 + ph) + 15 * wu,
                };
            }

            case LightPattern.Streak:
                return new[] { v.Side * 50 + 12 * wr, 500 + 220 * tp * tp, 40 + 12 * wu };

            case LightPattern.PaceAhead:
                return new[] { 14 * wr, 120 + 80 * v.Dist + 18 * wf, 8 + 12 * wu };

            case LightPattern.Abeam:
                return new[] { v.Side * (70 + 50 * v.Dist) + 16 * wr, 12 * wf, 5 + 10 * wu };

            case LightPattern.Above:
                return new[] { 16 * wr, 22 * wf, 60 + 40 * v.Dist + 12 * wu };

            case LightPattern.Below:
                return new[] { 16 * wr, 22 * wf, -(40 + 30 * v.Dist) + 12 * wu };

            case LightPattern.Inspect:
            {
                double radius = 120 + 80 * v.Dist;
                double ang = ts * 0.08 + ph + idx * (2 * Math.PI / n);
                return new[]
                {
                    radius * Math.Cos(ang) + 15 * wr,
                    radius * Math.Sin(ang) + 15 * wf,
                    20 + 30 * Math.Sin(ts * 0.06 + ph) + 10 * wu,
                };
            }

            case LightPattern.Approach:
            {
                double u = Math.Clamp(tp / _durationSec, 0, 1);
                double dist = Lerp(2600, 130, u);
                return new[] { v.Side * 0.30 * dist + 14 * wr, 0.90 * dist, 0.15 * dist + 10 * wu };
            }

            case LightPattern.BrakeHover:
            {
                const double brakeTime = 2.5;
                double u = Math.Clamp(tp / brakeTime, 0, 1);
                double eased = 1 - (1 - u) * (1 - u); // decelerating ease-out
                return new[] { 5 * wr, Lerp(3000, 12, eased), 4 + 6 * wu };
            }

            case LightPattern.Depart:
            {
                var fr = l.DepartFrom;
                double mag = Math.Sqrt(fr[0] * fr[0] + fr[1] * fr[1] + fr[2] * fr[2]);
                double ux, uy, uz;
                if (mag < 1) { ux = 0; uy = 1; uz = 0.2; mag = 50; }
                else { ux = fr[0] / mag; uy = fr[1] / mag; uz = fr[2] / mag; }
                double dist = Math.Max(mag, 40) + _departAccel * tp * tp;
                return new[] { ux * dist, uy * dist, uz * dist };
            }

            case LightPattern.Overtake:
            {
                // Fixed RELATIVE closing speed on top of the player: ~150 m/s ≈ a
                // transonic fighter overtaking you at cruise.
                const double rel = 150;
                double half = rel * _durationSec / 2.0;
                double u = Math.Clamp(tp / _durationSec, 0, 1);
                return new[] { v.Side * (45 + 20 * v.Dist), -half + rel * tp, 8 + 12 * Math.Sin(u * Math.PI) };
            }

            case LightPattern.TicTac:
            default:
                if (tp >= l.NextJump) JumpTicTac(l, tp);
                return new[] { l.Current[0], l.Current[1], l.Current[2] };
        }
    }

    private void JumpTicTac(Light l, double t)
    {
        var v = l.V;
        bool dart = _rng.NextDouble() < v.DartChance;
        double reach = (dart ? 1800 : 700) * v.Dist;
        l.Current[0] = (_rng.NextDouble() * 2 - 1) * reach;                  // right
        l.Current[1] = 200 + _rng.NextDouble() * (reach + 600);             // fwd
        l.Current[2] = (_rng.NextDouble() * 2 - 1) * 250 + 100;             // up
        double dwell = (v.JumpMin + _rng.NextDouble() * (v.JumpMax - v.JumpMin)) / _speedScale;
        l.NextJump = t + (dart ? dwell * 0.4 : dwell);                       // darts snap quicker
    }

    private void JumpMothership(Light l)
    {
        var v = l.V;
        l.Current[0] = (_rng.NextDouble() * 2 - 1) * 1500 * v.Dist;          // right ±
        l.Current[1] = 2200 + _rng.NextDouble() * 3500 * v.Dist;            // far ahead
        l.Current[2] = -100 + _rng.NextDouble() * 900;                      // can loom above
    }

    /// <summary>Convert a local (right, fwd, up) offset to a world pose.</summary>
    private static ObjectPose ComposePose(PlaneState p, double[] off, double tSession, Vars v)
    {
        double right = off[0], fwd = off[1], up = off[2];
        double hr = p.HeadingTrue * Math.PI / 180.0;
        double cos = Math.Cos(hr), sin = Math.Sin(hr);

        double northM = fwd * cos - right * sin;
        double eastM = fwd * sin + right * cos;

        double latRad = p.Latitude * Math.PI / 180.0;
        double dLat = northM / MetersPerDegLat;
        double dLon = eastM / (MetersPerDegLat * Math.Max(0.01, Math.Cos(latRad)));

        return new ObjectPose
        {
            Latitude = p.Latitude + dLat,
            Longitude = p.Longitude + dLon,
            AltitudeMsl = p.AltitudeMsl + up * FeetPerMeter,
            Pitch = 0,
            Bank = 15 * Math.Sin(tSession * 1.7 * v.Speed + v.Phase),  // continuous across switches
            Heading = p.HeadingTrue,
        };
    }

    private static double Lerp(double a, double b, double u) => a + (b - a) * u;
}
