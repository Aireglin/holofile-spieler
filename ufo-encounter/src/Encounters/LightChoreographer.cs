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
        public double[] Current = new double[3]; // right, fwd, up (metres)
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
    private long _frame;

    public bool IsActive { get; private set; }
    /// <summary>Distance (m) of the nearest spawned object, or null if none.</summary>
    public double? NearestMeters { get; private set; }

    public LightChoreographer(SimConnectClient sim, Action<string>? log = null, Random? rng = null)
    {
        _sim = sim;
        _log = log;
        _rng = rng ?? new Random();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) }; // ~50 Hz
        _timer.Tick += OnTick;
        _sim.ObjectAssigned += OnObjectAssigned;
    }

    public void Start(LightPattern pattern, int count, double durationSec, string title)
    {
        if (!_sim.IsConnected) { _log?.Invoke("Lights: not connected."); return; }
        if (string.IsNullOrWhiteSpace(title)) { _log?.Invoke("Lights: no SimObject title set."); return; }
        var p = _sim.State;
        if (p.OnGround > 0.5) { _log?.Invoke("Lights: skipped (on ground)."); return; }

        Stop();
        _pattern = pattern;
        _durationSec = durationSec;
        _title = title;
        _start = DateTime.UtcNow;
        IsActive = true;

        // A mothership is a single large object; light patterns can have several.
        count = pattern == LightPattern.Mothership ? 1 : Math.Clamp(count, 1, 8);
        for (uint i = 0; i < count; i++)
        {
            var light = new Light { Index = i, V = RollVars() };
            Seed(light);
            _lights.Add(light);
            _sim.SpawnLight(_title, ComposePose(p, light.Current, 0, light.V), i);
        }
        _log?.Invoke($"Lights: spawning {count}× '{_title}' ({pattern}).");
        _timer.Start();
    }

    /// <summary>Switch the motion pattern live, keeping the spawned objects
    /// (used for multi-phase encounters). Re-rolls variance and re-seeds anchors.</summary>
    public void SetPattern(LightPattern pattern)
    {
        if (!IsActive) return;
        _pattern = pattern;
        _start = DateTime.UtcNow;
        foreach (var l in _lights)
        {
            l.V = RollVars();
            Seed(l);
        }
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
        else JumpTicTac(l, 0);
    }

    private void OnObjectAssigned(uint lightIndex, uint objectId)
    {
        var light = _lights.Find(l => l.Index == lightIndex);
        if (light == null) { _sim.RemoveLight(objectId, lightIndex); return; } // stale
        light.ObjectId = objectId;
        _sim.FreezeLight(objectId);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double t = (DateTime.UtcNow - _start).TotalSeconds;
        var p = _sim.State;
        // Re-assert the freeze a few times a second: AI objects can wake up and
        // drift, which otherwise looks like the light "resetting" to its origin.
        bool reFreeze = (++_frame % 25) == 0;
        double? nearest = null;
        foreach (var l in _lights)
        {
            if (l.ObjectId is not uint id) continue;
            if (reFreeze) _sim.FreezeLight(id);
            var off = OffsetFor(l, t);
            double d = Math.Sqrt(off[0] * off[0] + off[1] * off[1] + off[2] * off[2]);
            if (nearest is null || d < nearest) nearest = d;
            _sim.MoveLight(id, ComposePose(p, off, t, l.V));
        }
        NearestMeters = nearest;
    }

    /// <summary>Local offset (right, fwd, up in metres) for a light at time t.</summary>
    private double[] OffsetFor(Light l, double t)
    {
        var v = l.V;
        double ph = v.Phase;
        int idx = (int)l.Index;
        int n = _lights.Count;

        switch (_pattern)
        {
            case LightPattern.Wingman:
                return new[]
                {
                    v.Side * (40 + 30 * v.Dist) + 6 * v.Amp * Math.Sin(t * 0.7 * v.Speed + ph),
                    -(45 + 25 * v.Dist) + idx * -18.0,
                    -2 + 5 * v.Amp * Math.Sin(t * 0.9 * v.Speed + ph),
                };

            case LightPattern.FlyBy:
            {
                double u = Math.Clamp(t / Math.Max(1.0, _durationSec), 0, 1);
                return new[]
                {
                    v.Side * (90 + 60 * v.Dist) + idx * 15,
                    Lerp(2200 + 2000 * v.Dist, -1500 - 800 * v.Dist, u),
                    (15 + 25 * v.Amp) * Math.Sin(u * Math.PI),
                };
            }

            case LightPattern.Formation:
            {
                double spread = (idx - (n - 1) / 2.0) * (24 + 12 * v.Dist);
                return new[]
                {
                    spread + 8 * v.Amp * Math.Sin(t * 0.3 * v.Speed + ph),
                    120 + 60 * v.Dist + Math.Abs(spread) * 0.6 + 12 * Math.Sin(t * 0.5 * v.Speed + ph),
                    8 + 5 * v.Amp * Math.Sin(t * 0.4 * v.Speed + ph),
                };
            }

            case LightPattern.PopUp:
                return new[]
                {
                    v.Side * (40 + 60 * v.Amp) * Math.Sin(t * 1.4 * v.Speed + ph),
                    260 + 120 * v.Dist + 50 * Math.Sin(t * 0.8 * v.Speed + ph),
                    15 + (25 + 20 * v.Amp) * Math.Sin(t * 2.0 * v.Speed + ph * 1.3),
                };

            case LightPattern.Mothership:
                if (t >= l.NextJump) JumpMothership(l, t);
                // Slow looming sway around the (occasionally repositioned) anchor.
                return new[]
                {
                    l.Current[0] + 180 * v.Amp * Math.Sin(t * 0.15 * v.Speed + ph),
                    l.Current[1] + 300 * Math.Sin(t * 0.10 * v.Speed),
                    l.Current[2] + 70 * Math.Sin(t * 0.12 * v.Speed + ph),
                };

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
        double dwell = v.JumpMin + _rng.NextDouble() * (v.JumpMax - v.JumpMin);
        l.NextJump = t + (dart ? dwell * 0.4 : dwell);                       // darts snap quicker
    }

    private void JumpMothership(Light l, double t)
    {
        var v = l.V;
        l.Current[0] = (_rng.NextDouble() * 2 - 1) * 1500 * v.Dist;          // right ±
        l.Current[1] = 2200 + _rng.NextDouble() * 3500 * v.Dist;            // far ahead
        l.Current[2] = -200 + _rng.NextDouble() * 1000;                     // can loom above
        l.NextJump = t + 6 + _rng.NextDouble() * 8;                         // reposition every 6–14s
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
