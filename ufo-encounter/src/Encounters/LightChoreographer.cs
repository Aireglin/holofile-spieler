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
}

/// <summary>
/// Spawns light SimObjects and animates them relative to the player aircraft.
/// Offsets are computed in an aircraft-local frame (right / forward / up, in
/// metres) and converted to lat/lon/alt each frame.
///
/// EXPERIMENTAL: needs a SimObject title that exists in the user's install. If
/// nothing appears, SimConnect logs an exception with the bad title and the
/// user can try another via the UI.
/// </summary>
public sealed class LightChoreographer
{
    private const double MetersPerDegLat = 111320.0;
    private const double FeetPerMeter = 3.28084;

    private sealed class Light
    {
        public uint Index;
        public uint? ObjectId;
        public double Phase;
        // Tic-Tac state:
        public double[] Current = new double[3]; // right, fwd, up (metres)
        public double NextJump;
    }

    private readonly SimConnectClient _sim;
    private readonly Action<string>? _log;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly List<Light> _lights = new();

    private LightPattern _pattern;
    private double _durationSec;
    private DateTime _start;
    private string _title = "";

    public bool IsActive { get; private set; }

    public LightChoreographer(SimConnectClient sim, Action<string>? log = null)
    {
        _sim = sim;
        _log = log;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30 Hz
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

        count = Math.Clamp(count, 1, 8);
        for (uint i = 0; i < count; i++)
        {
            var light = new Light { Index = i, Phase = _rng.NextDouble() * Math.PI * 2, NextJump = 0 };
            JumpTicTac(light, 0); // seed an initial offset
            _lights.Add(light);
            var pose = ComposePose(p, light.Current, 0);
            _sim.SpawnLight(_title, pose, i);
        }
        _log?.Invoke($"Lights: spawning {count}× '{_title}' ({pattern}).");
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        foreach (var l in _lights)
            if (l.ObjectId is uint id) _sim.RemoveLight(id, l.Index);
        _lights.Clear();
        IsActive = false;
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
        foreach (var l in _lights)
        {
            if (l.ObjectId is not uint id) continue;
            var offset = OffsetFor(l, t);
            _sim.MoveLight(id, ComposePose(p, offset, t));
        }
    }

    /// <summary>Local offset (right, fwd, up in metres) for a light at time t.</summary>
    private double[] OffsetFor(Light l, double t)
    {
        double ph = l.Phase;
        int idx = (int)l.Index;
        int n = _lights.Count;

        switch (_pattern)
        {
            case LightPattern.Wingman:
                // Hold aft-right, gently bobbing.
                return new[]
                {
                    45 + 5 * Math.Sin(t * 0.7 + ph),
                    -55 + idx * -18.0,
                    -2 + 4 * Math.Sin(t * 0.9 + ph),
                };

            case LightPattern.FlyBy:
            {
                double u = Math.Clamp(t / Math.Max(1.0, _durationSec), 0, 1);
                return new[]
                {
                    120.0 + idx * 15,
                    Lerp(3000, -1800, u),
                    25 * Math.Sin(u * Math.PI),
                };
            }

            case LightPattern.Formation:
            {
                double spread = (idx - (n - 1) / 2.0) * 28.0;       // V across
                return new[]
                {
                    spread,
                    140 + Math.Abs(spread) * 0.6 + 12 * Math.Sin(t * 0.5 + ph),
                    8 + 4 * Math.Sin(t * 0.4 + ph),
                };
            }

            case LightPattern.PopUp:
                return new[]
                {
                    70 * Math.Sin(t * 1.4 + ph),
                    320 + 50 * Math.Sin(t * 0.8 + ph),
                    15 + 35 * Math.Sin(t * 2.0 + ph * 1.3),
                };

            case LightPattern.TicTac:
            default:
                if (t >= l.NextJump) JumpTicTac(l, t);
                return l.Current;
        }
    }

    private void JumpTicTac(Light l, double t)
    {
        l.Current[0] = (_rng.NextDouble() * 2 - 1) * 600;   // right ±600 m
        l.Current[1] = 200 + _rng.NextDouble() * 1300;       // fwd 200..1500 m
        l.Current[2] = (_rng.NextDouble() * 2 - 1) * 200 + 100; // up -100..300 m
        l.NextJump = t + 0.4 + _rng.NextDouble() * 0.5;      // every ~0.4–0.9 s
    }

    /// <summary>Convert a local (right, fwd, up) offset to a world pose.</summary>
    private static ObjectPose ComposePose(PlaneState p, double[] off, double t)
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
            Bank = 20 * Math.Sin(t * 1.7),  // a little visual life
            Heading = p.HeadingTrue,
        };
    }

    private static double Lerp(double a, double b, double u) => a + (b - a) * u;
}
