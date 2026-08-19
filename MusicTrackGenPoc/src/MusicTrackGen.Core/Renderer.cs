namespace MusicTrackGen.Core;

public sealed class RenderResult
{
    public required float[] Left { get; init; }
    public required float[] Right { get; init; }
    public int SampleRate { get; init; }
    public double DurationSeconds => Left.Length / (double)SampleRate;
    public double Rms { get; init; }
    public double PeakDbfs { get; init; }
    public bool HadVocals { get; init; }
}

/// <summary>
/// Renders a score to stereo audio: per-part synthesis, panning, buses, reverb, a
/// vocal-driven duck on the music bus, then master limiting and normalisation.
/// </summary>
public static class Renderer
{
    /// <summary>Extra time rendered past the last note so reverb and releases are not clipped.</summary>
    private const double TailSeconds = 2.5;

    public static RenderResult Render(SongScore score, GenerationRequest request,
                                      IVocalSynthesizer vocalSynth, ulong seed,
                                      Action<GenerationStage, double>? progress = null)
    {
        int sr = request.SampleRate;
        int musicalSamples = (int)Math.Round(score.DurationSeconds * sr);
        int renderSamples = musicalSamples + (int)(TailSeconds * sr);

        var musicL = new float[renderSamples];
        var musicR = new float[renderSamples];
        var vocalL = new float[renderSamples];
        var vocalR = new float[renderSamples];

        // ---- instruments ----
        var instrumentParts = score.Parts.Where(p => p.Kind != PartKind.Vocal).ToList();
        for (int i = 0; i < instrumentParts.Count; i++)
        {
            var part = instrumentParts[i];
            progress?.Invoke(GenerationStage.RenderingInstruments, (i + 1) / (double)(instrumentParts.Count + 2));
            var mono = Instruments.RenderPart(part, score, sr, renderSamples,
                                             new Rng(seed ^ (ulong)(0x9E37 + part.Index * 7919)));
            MixInto(musicL, musicR, mono, part.Gain, part.Pan);
        }

        // ---- percussion ----
        progress?.Invoke(GenerationStage.RenderingInstruments, 0.9);
        var drums = Drums.Render(score, sr, renderSamples, new Rng(seed ^ 0xD1234));
        MixInto(musicL, musicR, drums, 0.62, 0.0);

        // ---- vocals ----
        var vocalPart = score.Parts.FirstOrDefault(p => p.Kind == PartKind.Vocal);
        bool hadVocals = false;
        if (vocalPart is not null && request.RenderVocals && vocalPart.Notes.Count > 0)
        {
            progress?.Invoke(GenerationStage.SynthesizingVocals, 0.0);
            var mono = vocalSynth.Synthesize(vocalPart, score, request.VoiceType, sr, renderSamples,
                                             new Rng(seed ^ 0x5EED1));
            MixInto(vocalL, vocalR, mono, vocalPart.Gain, 0.0);
            hadVocals = true;
            progress?.Invoke(GenerationStage.SynthesizingVocals, 1.0);
        }

        progress?.Invoke(GenerationStage.Mixing, 0.2);

        // ---- reverb: slightly different room per channel so the image is wide ----
        ApplyReverb(musicL, musicR, sr, wet: 0.17, room: 1.7);
        if (hadVocals) ApplyReverb(vocalL, vocalR, sr, wet: 0.26, room: 2.2, predelayMs: 22);

        // ---- sidechain duck so vocals sit in front of the music ----
        if (hadVocals) DuckByVocal(musicL, musicR, vocalL, vocalR, sr, depthDb: -2.2);

        progress?.Invoke(GenerationStage.Mixing, 0.6);

        // ---- sum, master, trim to the exact requested length ----
        var left = new float[musicalSamples];
        var right = new float[musicalSamples];
        for (int i = 0; i < musicalSamples; i++)
        {
            left[i] = musicL[i] + vocalL[i];
            right[i] = musicR[i] + vocalR[i];
        }

        // Fold the reverb tail back over the final moments instead of hard-cutting it.
        FoldTail(left, right, musicL, musicR, vocalL, vocalR, musicalSamples, sr);

        Dynamics.Compress(left, sr, -12, 1.8, 25, 220, 0.8);
        Dynamics.Compress(right, sr, -12, 1.8, 25, 220, 0.8);
        double targetRms = Math.Clamp(0.16, 0.06, 0.28);
        Dynamics.NormaliseRms(left, right, targetRms);
        Dynamics.Limit(left);
        Dynamics.Limit(right);
        FadeEdges(left, right, sr);

        progress?.Invoke(GenerationStage.Mixing, 1.0);

        double peak = 0;
        for (int i = 0; i < left.Length; i++)
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));

        return new RenderResult
        {
            Left = left,
            Right = right,
            SampleRate = sr,
            Rms = Dynamics.Rms(left, right),
            PeakDbfs = Dynamics.LinToDb(peak),
            HadVocals = hadVocals,
        };
    }

    private static void MixInto(float[] left, float[] right, float[] mono, double gain, double pan)
    {
        // Constant-power panning keeps perceived loudness steady across the image.
        double angle = (Math.Clamp(pan, -1, 1) + 1) * 0.25 * Math.PI;
        double gl = Math.Cos(angle) * gain;
        double gr = Math.Sin(angle) * gain;
        for (int i = 0; i < mono.Length; i++)
        {
            left[i] += (float)(mono[i] * gl);
            right[i] += (float)(mono[i] * gr);
        }
    }

    private static void ApplyReverb(float[] left, float[] right, int sampleRate, double wet,
                                    double room, double predelayMs = 0)
    {
        var rl = new Reverb(sampleRate, room, 1.0);
        var rr = new Reverb(sampleRate, room, 1.037);
        int predelay = (int)(predelayMs * sampleRate / 1000.0);
        var dl = predelay > 0 ? new float[predelay] : null;
        var dr = predelay > 0 ? new float[predelay] : null;
        int di = 0;
        double dry = 1.0 - wet * 0.4;

        for (int i = 0; i < left.Length; i++)
        {
            float inL = left[i], inR = right[i];
            if (dl is not null && dr is not null)
            {
                float heldL = dl[di], heldR = dr[di];
                dl[di] = inL; dr[di] = inR;
                di = (di + 1) % dl.Length;
                inL = heldL; inR = heldR;
            }
            left[i] = (float)(left[i] * dry + rl.Process(inL) * wet);
            right[i] = (float)(right[i] * dry + rr.Process(inR) * wet);
        }
    }

    private static void DuckByVocal(float[] musicL, float[] musicR, float[] vocalL, float[] vocalR,
                                    int sampleRate, double depthDb)
    {
        double attack = Math.Exp(-1.0 / (0.008 * sampleRate));
        double release = Math.Exp(-1.0 / (0.180 * sampleRate));
        double depth = 1.0 - Dynamics.DbToLin(depthDb);   // e.g. -2.2 dB -> 0.224
        double env = 0;
        double peak = 1e-6;
        for (int i = 0; i < vocalL.Length; i++)
            peak = Math.Max(peak, Math.Abs(vocalL[i]) + Math.Abs(vocalR[i]));

        for (int i = 0; i < musicL.Length; i++)
        {
            double x = (Math.Abs(vocalL[i]) + Math.Abs(vocalR[i])) / peak;
            env = x > env ? attack * env + (1 - attack) * x : release * env + (1 - release) * x;
            double gain = 1.0 - depth * Math.Clamp(env * 2.2, 0, 1);
            musicL[i] = (float)(musicL[i] * gain);
            musicR[i] = (float)(musicR[i] * gain);
        }
    }

    /// <summary>
    /// The reverb tail that would fall past the end is mixed back over the closing bars,
    /// so trimming to an exact duration does not amputate the decay.
    /// </summary>
    private static void FoldTail(float[] left, float[] right, float[] musicL, float[] musicR,
                                 float[] vocalL, float[] vocalR, int musicalSamples, int sampleRate)
    {
        int foldLength = Math.Min(musicalSamples, (int)(1.2 * sampleRate));
        if (foldLength <= 0 || musicalSamples >= musicL.Length) return;
        int offset = musicalSamples - foldLength;

        for (int i = 0; i < foldLength; i++)
        {
            int src = musicalSamples + i;
            if (src >= musicL.Length) break;
            // Ramp the folded tail in so it never adds a transient.
            double w = 0.55 * (i / (double)foldLength);
            left[offset + i] += (float)((musicL[src] + vocalL[src]) * w);
            right[offset + i] += (float)((musicR[src] + vocalR[src]) * w);
        }
    }

    private static void FadeEdges(float[] left, float[] right, int sampleRate)
    {
        int fadeIn = Math.Min(left.Length, (int)(0.020 * sampleRate));
        int fadeOut = Math.Min(left.Length, (int)(0.400 * sampleRate));

        for (int i = 0; i < fadeIn; i++)
        {
            double g = i / (double)fadeIn;
            left[i] = (float)(left[i] * g);
            right[i] = (float)(right[i] * g);
        }
        for (int i = 0; i < fadeOut; i++)
        {
            double g = i / (double)fadeOut;
            int idx = left.Length - 1 - i;
            left[idx] = (float)(left[idx] * g);
            right[idx] = (float)(right[idx] * g);
        }
    }
}
