using System.IO;
using System.Text.Json;

namespace UfoEncounter.Util;

/// <summary>Persisted UI/tuning settings (mirrors the controls in MainWindow).</summary>
public sealed class AppSettings
{
    public double Frequency { get; set; } = 1.0;
    public double MinDuration { get; set; } = 4;
    public double MaxDuration { get; set; } = 12;
    public double Intensity { get; set; } = 0.6;
    public double MinAgl { get; set; } = 1500;
    public double BlackoutMin { get; set; } = 8;
    public double BlackoutMax { get; set; } = 25;
    public double Dread { get; set; } = 0.4;
    public double WhooshVolume { get; set; } = 0.35;
    public double DisruptionChance { get; set; } = 0.75;

    public bool MultiPhase { get; set; } = true;
    public bool DayNight { get; set; } = true;
    public bool Whoosh { get; set; } = false;
    public bool EngineCut { get; set; } = true;
    public bool Realism { get; set; } = true;
    public bool LightsEnabled { get; set; } = true;
    public bool SpawnAsAircraft { get; set; }

    public string LightTitle { get; set; } = "";
    public string MothershipTitle { get; set; } = "";
    public int LightCount { get; set; } = 2;
    public double MinDistance { get; set; }
    public double SpeedScale { get; set; } = 0.6;
    public string BeamTitle { get; set; } = "";
    public int BeamCount { get; set; } = 3;
    public double BeamHeight { get; set; } = 30;
    public string JetTitle { get; set; } = "";
}

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON next to the executable.</summary>
public static class SettingsStore
{
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
