using System.Text;

namespace MusicTrackGen.Core;

/// <summary>Decoded PCM audio, mono or stereo, float samples in [-1,1].</summary>
public sealed class PcmAudio
{
    public required float[] Left { get; init; }
    public float[]? Right { get; init; }
    public int SampleRate { get; init; }
    public int Length => Left.Length;
    public double DurationSeconds => Length / (double)SampleRate;

    /// <summary>Mono mixdown used by the analyser.</summary>
    public float[] ToMono()
    {
        if (Right is null) return Left;
        var mono = new float[Left.Length];
        for (int i = 0; i < mono.Length; i++) mono[i] = 0.5f * (Left[i] + Right[i]);
        return mono;
    }
}

/// <summary>
/// Minimal RIFF/WAVE reader and writer. Handles the PCM and IEEE-float encodings that
/// matter here; compressed formats are rejected with a clear message rather than
/// silently producing noise.
/// </summary>
public static class WavIo
{
    public static void WriteStereo16(string path, float[] left, float[] right, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.ASCII);

        int frames = Math.Min(left.Length, right.Length);
        int dataBytes = frames * 2 * 2;

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);              // PCM
        w.Write((short)2);              // channels
        w.Write(sampleRate);
        w.Write(sampleRate * 2 * 2);    // byte rate
        w.Write((short)4);              // block align
        w.Write((short)16);             // bits per sample

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);

        for (int i = 0; i < frames; i++)
        {
            w.Write(ToPcm16(left[i]));
            w.Write(ToPcm16(right[i]));
        }
    }

    private static short ToPcm16(float sample)
    {
        // Round-to-nearest with symmetric clamping avoids the asymmetric wrap you get
        // from a plain cast at full scale.
        double scaled = Math.Clamp(sample, -1.0, 1.0) * 32767.0;
        return (short)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    public static PcmAudio Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var r = new BinaryReader(fs);

        if (new string(r.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a RIFF file. " +
                                           "The PoC analyser reads .wav; convert other formats first.");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a WAVE file.");

        int format = -1, channels = 0, sampleRate = 0, bits = 0;
        byte[]? data = null;

        while (fs.Position + 8 <= fs.Length)
        {
            string id = new(r.ReadChars(4));
            int size = r.ReadInt32();
            if (size < 0 || fs.Position + size > fs.Length) size = (int)(fs.Length - fs.Position);

            if (id == "fmt ")
            {
                long next = fs.Position + size;
                format = r.ReadInt16();
                channels = r.ReadInt16();
                sampleRate = r.ReadInt32();
                r.ReadInt32();              // byte rate
                r.ReadInt16();              // block align
                bits = r.ReadInt16();
                fs.Position = next;
            }
            else if (id == "data")
            {
                data = r.ReadBytes(size);
            }
            else
            {
                fs.Position += size + (size % 2);   // chunks are word-aligned
            }
        }

        if (data is null || channels < 1 || sampleRate < 1)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' has no readable PCM data chunk.");
        if (format != 1 && format != 3 && format != 0xFFFE)
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' uses WAVE format {format}, which is compressed. " +
                "Export it as uncompressed PCM .wav and re-add it.");

        var samples = Decode(data, format, bits, channels, out int frames);
        var left = new float[frames];
        float[]? right = channels > 1 ? new float[frames] : null;
        for (int i = 0; i < frames; i++)
        {
            left[i] = samples[i * channels];
            if (right is not null) right[i] = samples[i * channels + 1];
        }
        return new PcmAudio { Left = left, Right = right, SampleRate = sampleRate };
    }

    private static float[] Decode(byte[] data, int format, int bits, int channels, out int frames)
    {
        int bytesPerSample = Math.Max(1, bits / 8);
        int totalSamples = data.Length / bytesPerSample;
        frames = totalSamples / Math.Max(1, channels);
        totalSamples = frames * channels;

        var output = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            int o = i * bytesPerSample;
            output[i] = (format, bits) switch
            {
                (3, 32) => BitConverter.ToSingle(data, o),
                (_, 8) => (data[o] - 128) / 128f,
                (_, 16) => BitConverter.ToInt16(data, o) / 32768f,
                (_, 24) => ((data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16)) / 8388608f),
                (_, 32) => BitConverter.ToInt32(data, o) / 2147483648f,
                _ => 0f,
            };
        }
        return output;
    }
}
