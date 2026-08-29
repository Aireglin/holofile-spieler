using System.IO;

namespace UfoEncounter.Audio;

/// <summary>
/// Procedurally generates the encounter's sound layers as PCM WAV files (no
/// audio assets to ship). Each layer is written once to a temp file and then
/// played/looped by <see cref="AudioEngine"/> via MediaPlayer.
/// </summary>
internal static class WavSynth
{
    private const int SampleRate = 44100;

    /// <summary>Deep continuous drone (the classic "hum").</summary>
    public static short[] Drone(double baseHz = 68.0)
    {
        const double seconds = 2.0;
        int n = (int)(SampleRate * seconds);
        var pcm = new short[n];
        double peak = 0.8 * short.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            double trem = 0.8 + 0.2 * Math.Sin(2 * Math.PI * (2.0 / seconds) * t);
            double s = Math.Sin(2 * Math.PI * baseHz * t)
                     + 0.55 * Math.Sin(2 * Math.PI * (baseHz / 2) * t)
                     + 0.30 * Math.Sin(2 * Math.PI * (baseHz * 1.5) * t)
                     + 0.12 * Math.Sin(2 * Math.PI * (baseHz + 1.0) * t);
            pcm[i] = (short)(s / 1.97 * trem * peak);
        }
        return pcm;
    }

    /// <summary>Very low pulsing sub-bass — felt more than heard; "approach".</summary>
    public static short[] SubBass(double hz = 31.0)
    {
        const double seconds = 3.2;
        int n = (int)(SampleRate * seconds);
        var pcm = new short[n];
        double peak = 0.95 * short.MaxValue;
        double pulses = 4; // integer pulses across the loop -> seamless
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            double env = 0.45 + 0.55 * Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * (pulses / seconds) * t)), 2);
            double s = Math.Sin(2 * Math.PI * hz * t) + 0.4 * Math.Sin(2 * Math.PI * (hz / 2) * t);
            pcm[i] = (short)(s / 1.4 * env * peak);
        }
        return pcm;
    }

    /// <summary>Radio static / electrical interference, for blackouts.</summary>
    public static short[] Static()
    {
        const double seconds = 2.0;
        int n = (int)(SampleRate * seconds);
        var pcm = new short[n];
        var rng = new Random(1234);
        double peak = 0.5 * short.MaxValue;
        double prev = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            double white = rng.NextDouble() * 2 - 1;
            prev = 0.6 * prev + 0.4 * white;            // crude low-pass -> "crackle"
            double flick = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 7 * t + 3 * Math.Sin(t * 11));
            pcm[i] = (short)(prev * flick * peak);
        }
        return pcm;
    }

    /// <summary>One-shot approach swell: a noisy whoosh with a falling tone.</summary>
    public static short[] Whoosh()
    {
        const double seconds = 1.8;
        int n = (int)(SampleRate * seconds);
        var pcm = new short[n];
        var rng = new Random(7);
        double peak = 0.85 * short.MaxValue;
        double prev = 0;
        for (int i = 0; i < n; i++)
        {
            double u = (double)i / n;            // 0..1
            double t = (double)i / SampleRate;
            double env = Math.Sin(Math.PI * u);   // rise then fall
            double sweepHz = 420 * Math.Pow(0.18, u); // 420 -> ~75 Hz
            double white = rng.NextDouble() * 2 - 1;
            prev = 0.5 * prev + 0.5 * white;
            double s = 0.5 * prev + 0.5 * Math.Sin(2 * Math.PI * sweepHz * t);
            pcm[i] = (short)(s * env * peak);
        }
        return pcm;
    }

    /// <summary>One-shot "jumpscare": a short, bright high cluster like striking
    /// the top piano keys (capped ~3.1 kHz so it startles without hurting).</summary>
    public static short[] Jumpscare()
    {
        const double seconds = 0.9;
        int n = (int)(SampleRate * seconds);
        var pcm = new short[n];
        double[] freqs = { 2093, 2349, 2637, 3136 }; // C7, D7, E7, G7
        double peak = 0.55 * short.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            double s = 0;
            for (int k = 0; k < freqs.Length; k++)
            {
                double onset = k * 0.045;          // quick arpeggio "strike"
                if (t < onset) continue;
                double dt = t - onset;
                double env = Math.Exp(-dt * 6.5);  // fast decay
                s += env * Math.Sin(2 * Math.PI * freqs[k] * dt);
            }
            pcm[i] = (short)(s / freqs.Length * peak);
        }
        return pcm;
    }

    /// <summary>Write a mono 16-bit PCM WAV to <paramref name="path"/>.</summary>
    public static void Write(string path, short[] pcm)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataBytes = pcm.Length * sizeof(short);
        const short channels = 1, bits = 16;
        int byteRate = SampleRate * channels * bits / 8;

        w.Write("RIFF".ToCharArray()); w.Write(36 + dataBytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write(channels);
        w.Write(SampleRate); w.Write(byteRate); w.Write((short)(channels * bits / 8)); w.Write(bits);
        w.Write("data".ToCharArray()); w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);
    }
}
