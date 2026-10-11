using System.IO;

namespace UfoEncounter.Util;

/// <summary>
/// Where the app keeps its files (settings.json, sightings.csv). Next to the exe
/// when that folder is writable (as before); otherwise — e.g. Program Files or a
/// folder protected by Windows "Controlled Folder Access" — under
/// %LOCALAPPDATA%\UfoEncounter, instead of crashing at startup.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Resolve();

    private static string Resolve()
    {
        string exeDir = AppContext.BaseDirectory;
        if (CanWrite(exeDir)) return exeDir;
        string alt = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UfoEncounter");
        try { Directory.CreateDirectory(alt); } catch { /* best effort */ }
        return alt;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".ufoencounter_write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
