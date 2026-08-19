namespace MusicTrackGen.Core;

/// <summary>
/// Band-limited oscillator helpers. PolyBLEP correction keeps saw and square edges
/// from aliasing, which matters because the whole synth runs at one sample rate with
/// no oversampling.
/// </summary>
public static class Osc
{
    public static double PolyBlep(double t, double dt)
    {
        if (t < dt)
        {
            t /= dt;
            return t + t - t * t - 1.0;
        }
        if (t > 1.0 - dt)
        {
            t = (t - 1.0) / dt;
            return t * t + t + t + 1.0;
        }
        return 0.0;
    }

    /// <summary>Band-limited sawtooth in [-1,1]; advances phase in place.</summary>
    public static double Saw(ref double phase, double dt)
    {
        phase += dt;
        if (phase >= 1.0) phase -= 1.0;
        return 2.0 * phase - 1.0 - PolyBlep(phase, dt);
    }

    public static double Square(ref double phase, double dt, double pulseWidth = 0.5)
    {
        phase += dt;
        if (phase >= 1.0) phase -= 1.0;
        double value = phase < pulseWidth ? 1.0 : -1.0;
        value += PolyBlep(phase, dt);
        double shifted = phase - pulseWidth;
        if (shifted < 0) shifted += 1.0;
        value -= PolyBlep(shifted, dt);
        return value;
    }

    public static double Sine(ref double phase, double dt)
    {
        phase += dt;
        if (phase >= 1.0) phase -= 1.0;
        return Math.Sin(2.0 * Math.PI * phase);
    }
}

/// <summary>Renders symbolic notes into audio for the non-vocal parts.</summary>
public static class Instruments
{
    private static Adsr EnvelopeFor(PartKind kind) => kind switch
    {
        PartKind.Pad => new Adsr(0.35, 0.30, 0.72, 0.90),
        PartKind.Strings => new Adsr(0.18, 0.25, 0.80, 0.65),
        PartKind.Lead => new Adsr(0.05, 0.12, 0.85, 0.28),
        PartKind.Pluck => new Adsr(0.002, 0.05, 0.30, 0.35),
        PartKind.Bass => new Adsr(0.006, 0.10, 0.70, 0.18),
        PartKind.Keys => new Adsr(0.004, 0.09, 0.45, 0.22),
        _ => new Adsr(0.02, 0.1, 0.8, 0.3),
    };

    /// <summary>
    /// Renders one part into a fresh mono buffer. Timing and velocity humanisation come
    /// from the tuning profile, so the same score played with a different tuning profile
    /// grooves differently.
    /// </summary>
    public static float[] RenderPart(Part part, SongScore score, int sampleRate, int totalSamples, Rng rng)
    {
        var buffer = new float[totalSamples];
        var envelope = EnvelopeFor(part.Kind);
        double spb = score.SecondsPerBeat;
        var tuning = score.Tuning;

        foreach (var note in part.Notes)
        {
            double jitter = rng.Range(-tuning.TimingJitterMs, tuning.TimingJitterMs) / 1000.0;
            double start = note.StartBeat * spb + jitter;
            double hold = Math.Max(0.05, note.LengthBeats * spb);
            double velocity = Math.Clamp(
                note.Velocity + rng.Range(-tuning.VelocityJitter, tuning.VelocityJitter) / 127.0,
                0.05, 1.0);
            double freq = tuning.FrequencyOf(note.Midi, part.Index);

            RenderNote(buffer, sampleRate, start, hold, freq, velocity, part.Kind, envelope, tuning, rng);
        }
        return buffer;
    }

    private static void RenderNote(float[] buffer, int sampleRate, double startSeconds, double holdSeconds,
                                   double freq, double velocity, PartKind kind, Adsr envelope,
                                   TuningProfile tuning, Rng rng)
    {
        int start = (int)(startSeconds * sampleRate);
        if (start >= buffer.Length) return;
        int count = (int)(envelope.TotalSeconds(holdSeconds) * sampleRate);
        if (start < 0) { count += start; start = 0; }
        count = Math.Min(count, buffer.Length - start);
        if (count <= 0) return;

        double dtBase = freq / sampleRate;
        double phase1 = rng.NextDouble(), phase2 = rng.NextDouble(), phase3 = rng.NextDouble();
        double vibPhase = rng.NextDouble();
        double vibRate = tuning.VibratoRateHz / sampleRate;
        double vibDepth = tuning.VibratoDepthCents / 1200.0;
        double vibOnset = tuning.VibratoOnsetMs / 1000.0;

        var tone = kind switch
        {
            PartKind.Pad => Biquad.LowPass(sampleRate, Math.Min(4200, freq * 12), 0.8),
            PartKind.Strings => Biquad.LowPass(sampleRate, Math.Min(5200, freq * 14), 0.7),
            PartKind.Bass => Biquad.LowPass(sampleRate, Math.Min(1400, freq * 6), 0.9),
            PartKind.Keys => Biquad.LowPass(sampleRate, Math.Min(4800, freq * 10), 0.7),
            PartKind.Lead => Biquad.LowPass(sampleRate, Math.Min(6000, freq * 16), 0.6),
            _ => Biquad.LowPass(sampleRate, 8000, 0.7),
        };

        PluckedString? pluck = kind == PartKind.Pluck
            ? new PluckedString(sampleRate, freq, 0.985, rng)
            : null;

        double noiseState = 0;

        for (int i = 0; i < count; i++)
        {
            double t = i / (double)sampleRate;
            double env = envelope.At(t, holdSeconds);
            if (env <= 0 && t > holdSeconds) break;

            // Vibrato fades in, as a player would.
            double vibAmount = vibDepth * Math.Clamp((t - vibOnset) / 0.25, 0, 1);
            double vib = kind is PartKind.Lead or PartKind.Strings
                ? Math.Sin(2 * Math.PI * (vibPhase += vibRate)) * vibAmount
                : 0;
            double dt = dtBase * Math.Pow(2.0, vib);

            double sample = kind switch
            {
                PartKind.Pad =>
                    0.34 * Osc.Saw(ref phase1, dt) +
                    0.30 * Osc.Saw(ref phase2, dt * 1.0035) +
                    0.24 * Osc.Saw(ref phase3, dt * 0.9968),

                PartKind.Strings =>
                    0.40 * Osc.Saw(ref phase1, dt) +
                    0.28 * Osc.Saw(ref phase2, dt * 1.0021) +
                    0.16 * Osc.Sine(ref phase3, dt * 2),

                PartKind.Lead =>
                    // Bansuri/flute-like: strong fundamental, soft octave, breath noise.
                    0.72 * Osc.Sine(ref phase1, dt) +
                    0.18 * Osc.Sine(ref phase2, dt * 2) +
                    0.06 * Osc.Sine(ref phase3, dt * 3) +
                    0.05 * (noiseState = 0.7 * noiseState + 0.3 * (rng.NextDouble() * 2 - 1)),

                PartKind.Bass =>
                    0.70 * Osc.Sine(ref phase1, dt) +
                    0.30 * Osc.Saw(ref phase2, dt),

                PartKind.Keys =>
                    // Harmonium-like: odd harmonics with a slow tremolo.
                    (0.50 * Osc.Sine(ref phase1, dt) +
                     0.26 * Osc.Sine(ref phase2, dt * 3) +
                     0.14 * Osc.Sine(ref phase3, dt * 5)) *
                    (1.0 + 0.06 * Math.Sin(2 * Math.PI * 5.5 * t)),

                PartKind.Pluck => pluck!.Next() * 1.4,

                _ => Osc.Sine(ref phase1, dt),
            };

            buffer[start + i] += (float)(tone.Process(sample) * env * velocity * 0.5);
        }
    }
}
