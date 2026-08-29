using System.IO;
using System.Windows.Media;

namespace UfoEncounter.Audio;

/// <summary>
/// Multi-layer encounter audio. Each layer is a procedurally generated WAV
/// (written to a temp file in the ctor) played through a WPF MediaPlayer, so we
/// get real-time per-layer volume — used for distance-based loudness and for
/// cueing sub-bass / static during phases. No external audio dependencies.
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
        OpenOnce(_jumpscare, "ufo_jumpscare.wav", WavSynth.Jumpscare());

        // Optional override: drop a 'jumpscare.wav' / '.mp3' / '.ogg' next to the
        // exe to replace the synthesized scare with your own sound.
        var custom = FindUserAudio("jumpscare");
        if (custom != null)
        {
            try { _jumpscare.Open(new Uri(custom)); } catch { /* keep synth */ }
        }

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

    /// <summary>Fire the one-shot high-frequency "jumpscare" cluster.</summary>
    public void PlayJumpscare(double v)
    {
        _jumpscare.Volume = Clamp(v);
        _jumpscare.Position = TimeSpan.Zero;
        _jumpscare.Play();
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
