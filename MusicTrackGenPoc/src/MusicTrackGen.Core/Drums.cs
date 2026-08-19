namespace MusicTrackGen.Core;

/// <summary>
/// Percussion by physical caricature rather than samples: a pitch-swept sine for the
/// kick, noise plus body tones for the snare, and decaying membrane modes for tabla
/// and dholak. No sample library to license or ship.
/// </summary>
public static class Drums
{
    public static float[] Render(SongScore score, int sampleRate, int totalSamples, Rng rng)
    {
        var buffer = new float[totalSamples];
        double spb = score.SecondsPerBeat;
        double jitterMs = score.Tuning.TimingJitterMs;

        foreach (var hit in score.Drums)
        {
            double jitter = rng.Range(-jitterMs, jitterMs) / 1000.0;
            double start = hit.StartBeat * spb + jitter;
            int at = (int)(start * sampleRate);
            if (at >= totalSamples) continue;
            if (at < 0) at = 0;

            switch (hit.Kind)
            {
                case DrumKind.Kick: Kick(buffer, at, sampleRate, hit.Velocity, rng); break;
                case DrumKind.Snare: Snare(buffer, at, sampleRate, hit.Velocity, rng); break;
                case DrumKind.HatClosed: Hat(buffer, at, sampleRate, hit.Velocity, 0.045, rng); break;
                case DrumKind.HatOpen: Hat(buffer, at, sampleRate, hit.Velocity, 0.26, rng); break;
                case DrumKind.TablaNa: Membrane(buffer, at, sampleRate, hit.Velocity, 620, 0.22, 0.35, rng); break;
                case DrumKind.TablaGe: Membrane(buffer, at, sampleRate, hit.Velocity, 130, 0.42, 0.15, rng); break;
                case DrumKind.Dholak: Membrane(buffer, at, sampleRate, hit.Velocity, 210, 0.30, 0.25, rng); break;
            }
        }
        return buffer;
    }

    private static void Add(float[] buffer, int at, int i, double value)
    {
        int idx = at + i;
        if (idx >= 0 && idx < buffer.Length) buffer[idx] += (float)value;
    }

    private static void Kick(float[] buffer, int at, int sampleRate, double velocity, Rng rng)
    {
        int n = (int)(0.30 * sampleRate);
        double phase = 0;
        var click = Biquad.HighPass(sampleRate, 1200, 0.9);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            // Pitch sweeps from a slap down to the body frequency.
            double freq = 46 + 105 * Math.Exp(-t * 42);
            phase += freq / sampleRate;
            double body = Math.Sin(2 * Math.PI * phase) * Math.Exp(-t * 11);
            double transient = click.Process(rng.NextDouble() * 2 - 1) * Math.Exp(-t * 320) * 0.35;
            Add(buffer, at, i, (body + transient) * velocity * 0.9);
        }
    }

    private static void Snare(float[] buffer, int at, int sampleRate, double velocity, Rng rng)
    {
        int n = (int)(0.24 * sampleRate);
        var band = Biquad.BandPass(sampleRate, 1900, 0.7);
        var hp = Biquad.HighPass(sampleRate, 320, 0.8);
        double p1 = 0, p2 = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double noise = band.Process(rng.NextDouble() * 2 - 1) * Math.Exp(-t * 26);
            double tone = (Math.Sin(2 * Math.PI * (p1 += 182.0 / sampleRate)) * 0.6 +
                           Math.Sin(2 * Math.PI * (p2 += 331.0 / sampleRate)) * 0.4) * Math.Exp(-t * 34);
            Add(buffer, at, i, hp.Process(noise * 0.85 + tone * 0.4) * velocity * 0.7);
        }
    }

    private static void Hat(float[] buffer, int at, int sampleRate, double velocity, double decay, Rng rng)
    {
        int n = (int)(decay * 3 * sampleRate);
        var hp = Biquad.HighPass(sampleRate, 7200, 0.8);
        var peak = Biquad.BandPass(sampleRate, 9800, 1.4);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double noise = rng.NextDouble() * 2 - 1;
            double shaped = hp.Process(noise) * 0.7 + peak.Process(noise) * 0.5;
            Add(buffer, at, i, shaped * Math.Exp(-t / decay) * velocity * 0.35);
        }
    }

    /// <summary>
    /// Struck membrane: a few inharmonic modes decaying at different rates, plus a
    /// strike transient. Tuning the base frequency gives na / ge / dholak strokes.
    /// </summary>
    private static void Membrane(float[] buffer, int at, int sampleRate, double velocity,
                                double baseFreq, double decay, double bendAmount, Rng rng)
    {
        int n = (int)(decay * 3.5 * sampleRate);
        // Circular-membrane mode ratios (Bessel zeros), which is what makes a drum
        // read as a drum rather than a pitched tone.
        double[] modes = [1.0, 1.593, 2.135, 2.295, 2.917];
        double[] gains = [1.0, 0.42, 0.26, 0.18, 0.10];
        var phases = new double[modes.Length];
        var hp = Biquad.HighPass(sampleRate, 60, 0.7);

        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            // Slight downward pitch bend as the strike energy dissipates.
            double bend = 1.0 + bendAmount * Math.Exp(-t * 30);
            double sum = 0;
            for (int m = 0; m < modes.Length; m++)
            {
                phases[m] += baseFreq * modes[m] * bend / sampleRate;
                sum += Math.Sin(2 * Math.PI * phases[m]) * gains[m] * Math.Exp(-t / (decay / (1 + m * 0.7)));
            }
            double strike = (rng.NextDouble() * 2 - 1) * Math.Exp(-t * 260) * 0.25;
            Add(buffer, at, i, hp.Process(sum * 0.35 + strike) * velocity * 0.8);
        }
    }
}
