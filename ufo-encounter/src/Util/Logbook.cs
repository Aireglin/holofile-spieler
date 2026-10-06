using System.Globalization;
using System.IO;
using UfoEncounter.Sim;

namespace UfoEncounter.Util;

public sealed record SightingEntry(
    DateTime TimeUtc,
    string Scenario,
    double Latitude,
    double Longitude,
    double AltitudeMsl,
    double AltitudeAgl);

/// <summary>
/// Records encounters and appends them to a CSV next to the executable, so a
/// session can be exported as a tongue-in-cheek "sighting report".
/// </summary>
public sealed class Logbook
{
    private readonly string _path;
    private readonly List<SightingEntry> _entries = new();

    public IReadOnlyList<SightingEntry> Entries => _entries;
    public event Action<SightingEntry>? EntryAdded;

    public Logbook()
    {
        var dir = AppContext.BaseDirectory;
        _path = Path.Combine(dir, "sightings.csv");
        if (!File.Exists(_path))
            File.WriteAllText(_path, "time_utc,scenario,lat,lon,alt_msl_ft,alt_agl_ft\n");
    }

    public void Record(string scenario, PlaneState s)
    {
        var e = new SightingEntry(DateTime.UtcNow, scenario,
            s.Latitude, s.Longitude, s.AltitudeMsl, s.AltitudeAgl);
        _entries.Add(e);

        var ci = CultureInfo.InvariantCulture;
        var line = string.Join(',',
            e.TimeUtc.ToString("o", ci), Escape(scenario),
            e.Latitude.ToString("F6", ci), e.Longitude.ToString("F6", ci),
            e.AltitudeMsl.ToString("F0", ci), e.AltitudeAgl.ToString("F0", ci));
        try { File.AppendAllText(_path, line + "\n"); } catch { /* non-fatal */ }

        EntryAdded?.Invoke(e);
    }

    private static string Escape(string s) =>
        s.Contains(',') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
