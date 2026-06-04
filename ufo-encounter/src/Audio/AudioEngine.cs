using System.IO;
using System.Media;

namespace UfoEncounter.Audio;

/// <summary>
/// Plays a dull, low "hum" through the PC's default output. The waveform is
/// synthesized in-memory (no audio files to ship) and looped with SoundPlayer.
/// SoundPlayer has no volume control, so intensity is baked into the buffer.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int SampleRate = 44100;

    private SoundPlayer? _player;

    /// <summary>
    /// Start (or restart) the hum. <paramref name="intensity"/> 0..1 scales
    /// loudness; <paramref name="baseHz"/> sets the fundamental (≈ 55–90 Hz
    /// reads as a deep drone).
    /// </summary>
    public void StartHum(double intensity = 0.6, double baseHz = 70.0)
    {
        Stop();
        intensity = Math.Clamp(intensity, 0.0, 1.0);
        var wav = BuildHumWav(intensity, baseHz);
        _player = new SoundPlayer(new MemoryStream(wav));
        _player.PlayLooping();
    }

    public void Stop()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
    }

    /// <summary>One seamless ~2s loop: fundamental + sub-octave + a fifth, with
    /// a slow tremolo and a touch of "beating" to keep it unsettling.</summary>
    private static byte[] BuildHumWav(double intensity, double baseHz)
    {
        const double seconds = 2.0;
        int n = (int)(SampleRate * seconds);
        var pcm = new short[n];

        double peak = 0.85 * intensity * short.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            // Loop-safe tremolo: whole number of cycles across the buffer.
            double tremolo = 0.75 + 0.25 * Math.Sin(2 * Math.PI * (2.0 / seconds) * t);
            double s =
                  1.00 * Math.Sin(2 * Math.PI * baseHz * t)
                + 0.55 * Math.Sin(2 * Math.PI * (baseHz / 2) * t)
                + 0.30 * Math.Sin(2 * Math.PI * (baseHz * 1.5) * t)
                + 0.12 * Math.Sin(2 * Math.PI * (baseHz + 1.0) * t); // beating
            s /= 1.97; // normalize sum of weights
            pcm[i] = (short)(s * tremolo * peak);
        }

        return WrapWav(pcm);
    }

    private static byte[] WrapWav(short[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = pcm.Length * sizeof(short);
        const short channels = 1, bitsPerSample = 16;
        int byteRate = SampleRate * channels * bitsPerSample / 8;

        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);                          // PCM chunk size
        w.Write((short)1);                    // PCM
        w.Write(channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bitsPerSample / 8)); // block align
        w.Write(bitsPerSample);
        w.Write("data".ToCharArray());
        w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);

        w.Flush();
        return ms.ToArray();
    }

    public void Dispose() => Stop();
}
