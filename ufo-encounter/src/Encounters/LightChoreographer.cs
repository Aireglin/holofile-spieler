using System.Windows.Threading;
using UfoEncounter.Sim;

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
    /// <summary>A single massive object: slow, looming, with occasional
    /// "impossible" instantaneous repositions. Reads well by day.</summary>
    Mothership,
}

/// <summary>
/// Spawns SimObjects and animates them relative to the player aircraft. Offsets
/// are computed in an aircraft-local frame (right / forward / up, in metres) and
/// converted to lat/lon/alt each frame.
///
/// Every encounter draws fresh per-object randomness (speed, distance, amplitude,
/// side, jump rhythm, dart chance), so the same scenario never plays out twice.
///
/// EXPERIMENTAL: needs a SimObject title that exists in the user's install.
/// </summary>
public sealed class LightChoreographer
{
    private const double MetersPerDegLat = 111320.0;
    private const double FeetPerMeter = 3.28084;

    /// <summary>Per-object randomness, rolled once per encounter.</summary>
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
        public uint? ObjectId;
        public Vars V = new();
        public double[] Current = new double[3];  // right, fwd, up (metres)
        public double[] Smoothed = new double[3];  // low-passed output
        public double[] DepartFrom = new double[3]; // bearing captured when departing
        public bool SmoothInit;
        public double NextJump;
    }

    private readonly SimConnectClient _sim;
    private readonly Action<string>? _log;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng;
    private readonly List<Light> _lights = new();

    private LightPattern _pattern;
    private double _durationSec;
    private DateTime _start;
    private string _title = "";
    private bool _asAircraft;
    private long _frame;
    private double _departAccel = 500;

    public bool IsActive { get; private set; }
    /// <summary>Distance (m) of the nearest spawned object, or null if none.</summary>
    public double? NearestMeters { get; private set; }
    /// <summary>Never let an object come closer than this (metres); 0 = no limit.</summary>
    public double MinDistanceMeters { get; set; }
    /// <summary>Global movement-speed multiplier (lower = slower, so the sim keeps
    /// up and motion stutters less). ~0.6 is a good default.</summary>
    public double SpeedScale { get; set; } = 0.6;
    /// <summary>Height (metres) of the Beam cluster above the aircraft.</summary>
    public double BeamHeight { get; set; } = 30;

    public LightChoreographer(SimConnectClient sim, Action<string>? log = null, Random? rng = null)
    {
        _sim = sim;
        _log = log;
        _rng = rng ?? new Random();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) }; // ~100 Hz
        _timer.Tick += OnTick;
        _sim.ObjectAssigned += OnObjectAssigned;
    }

    public void Start(LightPattern pattern, int count, double durationSec, string title, bool asAircraft = false)
    {
        if (!_sim.IsConnected) { _log?.Invoke("Lights: not connected."); return; }
        if (string.IsNullOrWhiteSpace(title)) { _log?.Invoke("Lights: no SimObject title set."); return; }
        var p = _sim.State;
        if (p.OnGround > 0.5) { _log?.Invoke("Lights: skipped (on ground)."); return; }

        Stop();
        _pattern = pattern;
        _durationSec = durationSec;
        _title = title;
        _asAircraft = asAircraft;
        _start = DateTime.UtcNow;
        IsActive = true;

        // Comma-separated titles → each object randomly picks one (e.g. mixed colours).
        var titles = _title.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (titles.Length == 0) { _log?.Invoke("Lights: no SimObject title set."); IsActive = false; return; }

        // A mothership is a single large object; light patterns can have several.
        count = pattern == LightPattern.Mothership ? 1 : Math.Clamp(count, 1, 8);
        for (uint i = 0; i < count; i++)
        {
            var light = new Light { Index = i, V = RollVars() };
            Seed(light);
            _lights.Add(light);
            var chosen = titles[_rng.Next(titles.Length)];
            _sim.SpawnLight(chosen, ComposePose(p, light.Current, 0, light.V), i, _asAircraft);
        }
        _log?.Invoke($"Lights: spawning {count} object(s) ({pattern}).");
        _timer.Start();
    }

    /// <summary>Switch the motion pattern live, keeping the spawned objects
    /// (used for multi-phase encounters). Re-rolls variance; only Tic-Tac re-seeds
    /// a jump target — the mothership keeps its far anchor so it never teleports.</summary>
    public void SetPattern(LightPattern pattern)
    {
        if (!IsActive) return;
        _pattern = pattern;
        _start = DateTime.UtcNow;
        foreach (var l in _lights)
        {
            l.V = RollVars();
            if (pattern == LightPattern.TicTac) JumpTicTac(l, 0);
        }
    }

    /// <summary>Switch every object into a fast (or majestic) departure along its
    /// current bearing — it shoots away into the distance before being despawned.</summary>
    public void BeginDepart(bool majestic)
    {
        if (!IsActive) return;
        _departAccel = majestic ? 90 : 520;
        foreach (var l in _lights)
        {
            var cur = (_pattern == LightPattern.TicTac || !l.SmoothInit) ? l.Current : l.Smoothed;
            Array.Copy(cur, l.DepartFrom, 3);
        }
        _pattern = LightPattern.Depart;
        _start = DateTime.UtcNow;
    }

    public void Stop()
    {
        _timer.Stop();
        foreach (var l in _lights)
            if (l.ObjectId is uint id) _sim.RemoveLight(id, l.Index);
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

    /// <summary>Seed a sensible starting offset per pattern.</summary>
    private void Seed(Light l)
    {
        if (_pattern == LightPattern.Mothership) JumpMothership(l, 0);
        else if (_pattern == LightPattern.Beam) { l.Current[0] = 0; l.Current[1] = 0; l.Current[2] = BeamHeight; }
        else JumpTicTac(l, 0);
    }

    private void OnObjectAssigned(uint lightIndex, uint objectId)
    {
        var light = _lights.Find(l => l.Index == lightIndex);
        if (light == null) { _sim.RemoveLight(objectId, lightIndex); return; } // stale
        light.ObjectId = objectId;
        _sim.ReleaseAndFreeze(objectId, lightIndex);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double t = (DateTime.UtcNow - _start).TotalSeconds;
        var p = _sim.State;
        // Freeze is asserted only briefly after spawn; zeroing velocity each frame
        // keeps objects put afterwards. Repeated freezing caused a periodic hitch.
        _frame++;
        bool reFreeze = _frame <= 60 && (_frame % 12) == 0;
        double? nearest = null;
        foreach (var l in _lights)
        {
            if (l.ObjectId is not uint id) continue;
            if (reFreeze) _sim.FreezeLight(id);

            var raw = OffsetFor(l, t);
            double[] off;
            // Instant patterns (jumps / shooting motion) are not smoothed.
            if (_pattern is LightPattern.TicTac or LightPattern.Depart)
            {
                off = raw;
            }
            else
            {
                if (!l.SmoothInit) { Array.Copy(raw, l.Smoothed, 3); l.SmoothInit = true; }
                // Gentle easing (~time constant 0.25 s at 50 Hz) for fluid motion.
                for (int i = 0; i < 3; i++) l.Smoothed[i] = Lerp(l.Smoothed[i], raw[i], 0.08);
                off = l.Smoothed;
            }

            // Patterns that are intentionally close ignore the minimum distance.
            bool ignoreMin = _pattern is LightPattern.Beam or LightPattern.BrakeHover
                                       or LightPattern.Overtake or LightPattern.Depart;

            // Optionally keep objects beyond a minimum distance (avoids the
            // close-range stutter; reads as a more distant sighting). Work on a
            // copy so we don't corrupt the stored anchor/smoothed state.
            double[] sent = { off[0], off[1], off[2] };
            double d = Math.Sqrt(sent[0] * sent[0] + sent[1] * sent[1] + sent[2] * sent[2]);
            if (!ignoreMin && MinDistanceMeters > 1 && d < MinDistanceMeters)
            {
                if (d > 1) { double s = MinDistanceMeters / d; sent[0] *= s; sent[1] *= s; sent[2] *= s; }
                else { sent[1] = MinDistanceMeters; } // degenerate: push straight ahead
                d = MinDistanceMeters;
            }

            if (nearest is null || d < nearest) nearest = d;
            _sim.MoveLight(id, ComposePose(p, sent, t, l.V));
        }
        NearestMeters = nearest;
    }

    /// <summary>Smooth, organic pseudo-noise (sum of incommensurate sines), ~[-1,1].</summary>
    private static double Wander(double t, double seed) =>
        0.55 * Math.Sin(t * 0.37 + seed)
        + 0.30 * Math.Sin(t * 0.83 + seed * 1.7)
        + 0.15 * Math.Sin(t * 1.49 + seed * 2.6);

    /// <summary>Local offset (right, fwd, up in metres) for a light at time t.</summary>
    private double[] OffsetFor(Light l, double t)
    {
        var v = l.V;
        double ph = v.Phase;
        int idx = (int)l.Index;
        int n = _lights.Count;
        double ts = t * v.Speed * Math.Clamp(SpeedScale, 0.1, 2.0);

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
                double u = Math.Clamp(t / Math.Max(1.0, _durationSec), 0, 1);
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
                // Looser, less mechanical loops: a base circle plus wander.
                return new[]
                {
                    v.Side * (40 + 60 * v.Amp) * Math.Sin(ts * 1.4 + ph) + 30 * wr,
                    260 + 120 * v.Dist + 40 * Math.Sin(ts * 0.8 + ph) + 40 * wf,
                    15 + (20 + 18 * v.Amp) * Math.Sin(ts * 2.0 + ph * 1.3) + 25 * wu,
                };

            case LightPattern.Mothership:
                // No pop-ups: a single massive object drifting slowly around a
                // fixed far anchor (seeded once). Very low frequencies + wander
                // read as huge mass moving "impossibly" but believably.
                return new[]
                {
                    l.Current[0] + 350 * Math.Sin(ts * 0.05 + ph) + 130 * wr,
                    l.Current[1] + 420 * Math.Sin(ts * 0.035) + 170 * wf,
                    l.Current[2] + 130 * Math.Sin(ts * 0.045 + ph) + 90 * wu,
                };

            case LightPattern.Beam:
            {
                // Tight cluster directly overhead; barely moving. Several objects
                // spread in a small ring widen the lit footprint.
                double ang = ph + idx * (2 * Math.PI / Math.Max(1, n));
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
                // Slow, wide circle around the aircraft (long, hypnotic).
                double radius = 280 + 220 * v.Dist;
                double ang = ts * 0.12 + ph + idx * (2 * Math.PI / Math.Max(1, n));
                return new[]
                {
                    radius * Math.Cos(ang) + 20 * wr,
                    radius * Math.Sin(ang) + 20 * wf,
                    60 + 40 * Math.Sin(ts * 0.05 + ph) + 15 * wu,
                };
            }

            case LightPattern.Streak:
            {
                // Sits ahead, then accelerates forward away (uses raw t for punch).
                double f = 500 + 220 * t * t;
                return new[] { v.Side * 50 + 12 * wr, f, 40 + 12 * wu };
            }

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
                // Slow, close-ish transit around you between positions.
                double radius = 120 + 80 * v.Dist;
                double ang = ts * 0.08 + ph + idx * (2 * Math.PI / Math.Max(1, n));
                return new[]
                {
                    radius * Math.Cos(ang) + 15 * wr,
                    radius * Math.Sin(ang) + 15 * wf,
                    20 + 30 * Math.Sin(ts * 0.06 + ph) + 10 * wu,
                };
            }

            case LightPattern.Approach:
            {
                // Comes from far away and settles into close formation ahead.
                double u = Math.Clamp(t / Math.Max(1.0, _durationSec), 0, 1);
                double dist = Lerp(2600, 130, u);
                return new[] { v.Side * 0.30 * dist + 14 * wr, 0.90 * dist, 0.15 * dist + 10 * wu };
            }

            case LightPattern.BrakeHover:
            {
                // Shoots in from far ahead, decelerates and hovers ~12 m in front.
                const double brakeTime = 2.5;
                double u = Math.Clamp(t / brakeTime, 0, 1);
                double eased = 1 - (1 - u) * (1 - u); // decelerating ease-out
                double dist = Lerp(3000, 12, eased);
                return new[] { 5 * wr, dist, 4 + 6 * wu };
            }

            case LightPattern.Depart:
            {
                // Accelerate away along the bearing captured at departure start.
                var fr = l.DepartFrom;
                double mag = Math.Sqrt(fr[0] * fr[0] + fr[1] * fr[1] + fr[2] * fr[2]);
                double ux, uy, uz;
                if (mag < 1) { ux = 0; uy = 1; uz = 0.2; mag = 50; }
                else { ux = fr[0] / mag; uy = fr[1] / mag; uz = fr[2] / mag; }
                double dist = Math.Max(mag, 40) + _departAccel * t * t;
                return new[] { ux * dist, uy * dist, uz * dist };
            }

            case LightPattern.Overtake:
            {
                // Realistic-ish fast pass: a fixed RELATIVE closing speed on top of
                // the player. ~150 m/s over the player ≈ a transonic fighter when
                // you're at cruise — not the old Mach-3 blink.
                const double rel = 150; // m/s relative (~290 kt over the player)
                double half = rel * _durationSec / 2.0;
                double f = -half + rel * t; // far behind → close pass → far ahead
                double u = Math.Clamp(t / Math.Max(1.0, _durationSec), 0, 1);
                return new[]
                {
                    v.Side * (45 + 20 * v.Dist),
                    f,
                    8 + 12 * Math.Sin(u * Math.PI),
                };
            }

            case LightPattern.TicTac:
            default:
                if (t >= l.NextJump) JumpTicTac(l, t);
                return l.Current;
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
        double dwell = (v.JumpMin + _rng.NextDouble() * (v.JumpMax - v.JumpMin))
                       / Math.Clamp(SpeedScale, 0.1, 2.0);                   // slower speed = longer dwell
        l.NextJump = t + (dart ? dwell * 0.4 : dwell);                       // darts snap quicker
    }

    private void JumpMothership(Light l, double t)
    {
        // Seeds the fixed far anchor once (no further repositioning).
        var v = l.V;
        l.Current[0] = (_rng.NextDouble() * 2 - 1) * 1500 * v.Dist;          // right ±
        l.Current[1] = 2200 + _rng.NextDouble() * 3500 * v.Dist;            // far ahead
        l.Current[2] = -100 + _rng.NextDouble() * 900;                      // can loom above
    }

    /// <summary>Convert a local (right, fwd, up) offset to a world pose.</summary>
    private static ObjectPose ComposePose(PlaneState p, double[] off, double t, Vars v)
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
            Bank = 15 * Math.Sin(t * 1.7 * v.Speed + v.Phase),  // a little visual life
            Heading = p.HeadingTrue,
        };
    }

    private static double Lerp(double a, double b, double u) => a + (b - a) * u;
}
