namespace Iris.Core.Overlay;

public enum UiSound
{
    InteractiveOverlayOpen,

    InteractiveOverlayClose,

    DossierOpen,
}

public static class UiSounds
{
    public const int SampleRate = 48000;

    public const double FullVolumePeak = 0.25;

    public const double CloseShare = 0.7;

    public static double[] Samples(UiSound sound) => sound switch
    {
        UiSound.InteractiveOverlayOpen => OverlayOpen(),
        UiSound.InteractiveOverlayClose => OverlayClose(),
        UiSound.DossierOpen => DossierOpen(),
        _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, null),
    };

    public static byte[] Wav(UiSound sound, int volumePercent)
    {
        var gain = Math.Clamp(volumePercent, 0, 100) / 100.0;
        var samples = Samples(sound);
        var dataBytes = samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        using var stream = new MemoryStream(wav);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in samples)
            writer.Write((short)Math.Round(Math.Clamp(sample * gain, -1.0, 1.0) * short.MaxValue));
        return wav;
    }

    private static double[] OverlayOpen()
    {
        const double Whistle = 0.6;
        var whistle = new double[Count(0.55)];
        var rest = new double[whistle.Length];
        AddTo(whistle, 0, Times(Sweep(1500, 4200, 0.09), Envelope(Count(0.09), 0.003, 0.05)), 1);
        AddTo(whistle, Count(0.025), Times(Sweep(2250, 6300, 0.07), Envelope(Count(0.07), 0.003, 0.04)), 0.45);
        AddTo(rest, 0, Times(Tone(3700, rest.Length), Envelope(rest.Length, 0.02, 0.12)), 0.18);
        AddTo(rest, Count(0.04), Times(Shimmer(0.5, seed: 1, rate: 90), Envelope(Count(0.5), 0.05, 0.15)), 0.25);

        var full = new double[whistle.Length];
        for (var i = 0; i < full.Length; i++)
            full[i] = whistle[i] + rest[i];
        var gain = FullVolumePeak / Peak(full);
        var result = new double[whistle.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = (whistle[i] * Whistle + rest[i]) * gain;
        return result;
    }

    private static double[] OverlayClose()
    {
        var sound = new double[Count(0.42)];
        AddTo(sound, 0, Times(Sweep(1950, 1750, 0.08), Envelope(Count(0.08), 0.003, 0.03)), 1);
        AddTo(sound, 0, Times(Sweep(3900, 3500, 0.08), Envelope(Count(0.08), 0.003, 0.02)), 0.25);
        AddTo(sound, Count(0.075), Times(Sweep(1450, 1250, 0.16), Envelope(Count(0.16), 0.003, 0.06)), 0.85);
        AddTo(sound, Count(0.02), Times(Shimmer(0.3, seed: 3, rate: 50), Envelope(Count(0.3), 0.02, 0.08)), 0.06);
        var gain = CloseShare * Peak(OverlayOpen()) / Peak(sound);
        for (var i = 0; i < sound.Length; i++)
            sound[i] *= gain;
        return sound;
    }

    private static double[] DossierOpen()
    {
        var sound = new double[Count(0.3)];
        AddTo(sound, 0, Times(Tone(2600, Count(0.06)), Envelope(Count(0.06), 0.003, 0.025)), 1);
        AddTo(sound, Count(0.06), Times(Tone(3900, Count(0.09)), Envelope(Count(0.09), 0.003, 0.025)), 0.8);
        AddTo(sound, Count(0.07), Times(Shimmer(0.13, seed: 2, rate: 60), Envelope(Count(0.13), 0.01, 0.04)), 0.08);
        var gain = FullVolumePeak / Peak(sound);
        for (var i = 0; i < sound.Length; i++)
            sound[i] *= gain;
        return sound;
    }

    private static int Count(double seconds) => (int)(SampleRate * seconds);

    private static double[] Envelope(int count, double attack, double decay)
    {
        var envelope = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;
            envelope[i] = Math.Min(1, t / attack) * Math.Exp(-t / decay);
        }
        return envelope;
    }

    private static double[] Sweep(double f0, double f1, double seconds)
    {
        var sweep = new double[Count(seconds)];
        var phase = 0.0;
        for (var i = 0; i < sweep.Length; i++)
        {
            phase += f0 * Math.Pow(f1 / f0, i / (double)SampleRate / seconds) / SampleRate;
            sweep[i] = Math.Sin(2 * Math.PI * phase);
        }
        return sweep;
    }

    private static double[] Tone(double frequency, int count)
    {
        var tone = new double[count];
        for (var i = 0; i < count; i++)
            tone[i] = Math.Sin(2 * Math.PI * frequency * i / SampleRate);
        return tone;
    }

    private static double[] Shimmer(double seconds, ulong seed, int rate)
    {
        var random = new SplitMix(seed);
        var shimmer = new double[Count(seconds)];
        var blip = Count(0.012);
        for (var k = 0; k < (int)(seconds * rate); k++)
        {
            var start = (int)random.Between(0, shimmer.Length - 2000);
            var frequency = random.Between(6000, 11000);
            var strength = random.Between(0.3, 1);
            for (var i = 0; i < blip && start + i < shimmer.Length; i++)
            {
                var t = i / (double)SampleRate;
                shimmer[start + i] += Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t / 0.004) * strength;
            }
        }
        return shimmer;
    }

    private static double[] Times(double[] a, double[] b)
    {
        var product = new double[Math.Min(a.Length, b.Length)];
        for (var i = 0; i < product.Length; i++)
            product[i] = a[i] * b[i];
        return product;
    }

    private static void AddTo(double[] target, int offset, double[] part, double scale)
    {
        for (var i = 0; i < part.Length && offset + i < target.Length; i++)
            target[offset + i] += part[i] * scale;
    }

    private static double Peak(double[] samples)
    {
        var peak = 0.0;
        foreach (var sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    private struct SplitMix(ulong seed)
    {
        private ulong _state = seed;

        public double Between(double low, double high)
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return low + (z >> 11) * (1.0 / (1UL << 53)) * (high - low);
        }
    }
}
