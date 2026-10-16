using System.IO;
using System.Windows.Media;

namespace UfoEncounter.Audio;

/// <summary>
/// Multi-layer encounter audio. Each layer is a procedurally generated WAV
/// (written to a temp file in the ctor) played through a WPF MediaPlayer, so we
/// get real-time per-layer volume — used for distance-based loudness and for
/// cueing sub-bass / static during phases. The jumpscare may instead be a user
/// file or the clip embedded at build time. No external audio dependencies.
///
/// MUST be constructed and driven on the UI (Dispatcher) thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly MediaPlayer _drone = new();
    private readonly MediaPlayer _subBass = new();
    private readonly MediaPlayer _static = new();
    private readonly MediaPlayer _whoosh = new();
    private readonly MediaPlayer _jumpscare = new();
    private readonly List<string> _tempFiles = new();
    private bool _looping;

    public AudioEngine()
    {
        OpenLoop(_drone, "ufo_drone.wav", WavSynth.Drone());
        OpenLoop(_subBass, "ufo_subbass.wav", WavSynth.SubBass());
        OpenLoop(_static, "ufo_static.wav", WavSynth.Static());
        OpenOnce(_whoosh, "ufo_whoosh.wav", WavSynth.Whoosh());

        // Jumpscare, first one that opens wins: a 'jumpscare.wav' / '.mp3' / '.ogg'
        // next to the exe (user override), the clip embedded at build time
        // (MediaPlayer needs a file, so it is extracted), the synthesized cluster.
        if (!TryOpen(_jumpscare, FindUserAudio("jumpscare"))
            && !TryOpen(_jumpscare, ExtractEmbedded("jumpscare.mp3", "ufo_jumpscare.mp3")))
            OpenOnce(_jumpscare, "ufo_jumpscare.wav", WavSynth.Jumpscare());

        foreach (var p in new[] { _drone, _subBass, _static, _whoosh, _jumpscare }) p.Volume = 0;
    }

    private static string? FindUserAudio(string baseName)
    {
        foreach (var ext in new[] { ".wav", ".mp3", ".ogg", ".wma" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, baseName + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Copies an embedded asset to the temp folder, kept between runs and
    /// rewritten only if missing or of a different size. Null if the asset was not
    /// built in (git-ignored file absent at build time) or the copy failed.</summary>
    private static string? ExtractEmbedded(string asset, string name)
    {
        try
        {
            using var src = typeof(AudioEngine).Assembly
                .GetManifestResourceStream("UfoEncounter.Assets." + asset);
            if (src == null) return null;
            var path = Path.Combine(Path.GetTempPath(), name);
            var existing = new FileInfo(path);
            if (!existing.Exists || existing.Length != src.Length)
            {
                using var dst = new FileStream(path, FileMode.Create, FileAccess.Write);
                src.CopyTo(dst);
            }
            return path;
        }
        catch { /* fall back to the next source */ return null; }
    }

    private static bool TryOpen(MediaPlayer p, string? path)
    {
        if (path == null) return false;
        try { p.Open(new Uri(path)); return true; } catch { return false; }
    }

    private void OpenLoop(MediaPlayer p, string name, short[] pcm)
    {
        var path = WriteTemp(name, pcm);
        p.MediaEnded += (_, _) => { p.Position = TimeSpan.Zero; p.Play(); };
        p.Open(new Uri(path));
    }

    private void OpenOnce(MediaPlayer p, string name, short[] pcm) =>
        p.Open(new Uri(WriteTemp(name, pcm)));

    private string WriteTemp(string name, short[] pcm)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        try { WavSynth.Write(path, pcm); } catch { /* fall through; player just stays silent */ }
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Begin (silent) playback of the looping layers.</summary>
    public void StartLayers()
    {
        if (_looping) return;
        _looping = true;
        _drone.Position = TimeSpan.Zero; _drone.Play();
        _subBass.Position = TimeSpan.Zero; _subBass.Play();
        _static.Position = TimeSpan.Zero; _static.Play();
    }

    public void SetDroneVolume(double v) => _drone.Volume = Clamp(v);
    public void SetSubBassVolume(double v) => _subBass.Volume = Clamp(v);
    public void SetStaticVolume(double v) => _static.Volume = Clamp(v);

    /// <summary>Fire the one-shot approach whoosh.</summary>
    public void PlayWhoosh(double v)
    {
        _whoosh.Volume = Clamp(v);
        _whoosh.Position = TimeSpan.Zero;
        _whoosh.Play();
    }

    /// <summary>Fire the one-shot jumpscare (restarts from the beginning).</summary>
    public void PlayJumpscare(double v)
    {
        _jumpscare.Volume = Clamp(v);
        _jumpscare.Position = TimeSpan.Zero;
        _jumpscare.Play();
    }

    /// <summary>Cut a running jumpscare. <see cref="StopAll"/> leaves it alone so a
    /// long clip can ring out past the end of a scene; a panic stop should not.</summary>
    public void StopJumpscare()
    {
        _jumpscare.Volume = 0;
        _jumpscare.Stop();
    }

    /// <summary>Snap every layer to silence immediately (the "sudden quiet").</summary>
    public void HardSilence()
    {
        _drone.Volume = 0; _subBass.Volume = 0; _static.Volume = 0;
    }

    public void StopAll()
    {
        _looping = false;
        foreach (var p in new[] { _drone, _subBass, _static, _whoosh })
        {
            p.Volume = 0;
            p.Stop();
        }
    }

    // ---- Backwards-compatible simple hum (used by the Hum test button) ----
    public void StartHum(double volume = 0.6, double baseHz = 68.0)
    {
        StartLayers();
        SetDroneVolume(volume);
    }

    public void Stop() => StopAll();

    private static double Clamp(double v) => Math.Clamp(v, 0.0, 1.0);

    public void Dispose()
    {
        StopAll();
        foreach (var p in new[] { _drone, _subBass, _static, _whoosh, _jumpscare }) p.Close();
        foreach (var f in _tempFiles)
            try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
    }
}
