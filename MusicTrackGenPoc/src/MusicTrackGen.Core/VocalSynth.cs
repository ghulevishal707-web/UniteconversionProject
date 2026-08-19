namespace MusicTrackGen.Core;

public sealed record VocalCapabilities(string[] Languages, string[] VoiceTypes, string Notes);

/// <summary>
/// The seam that lets the vocal quality tier be swapped without touching anything else.
/// Tier 0 (formant synthesis) ships here; a neural tier implements this same interface.
/// </summary>
public interface IVocalSynthesizer
{
    string Name { get; }
    VocalCapabilities Capabilities { get; }
    float[] Synthesize(Part vocalPart, SongScore score, VoiceType voiceType, int sampleRate,
                       int totalSamples, Rng rng);
}

/// <summary>
/// Tier 0: source-filter singing synthesis. A band-limited glottal source is driven at
/// the melody pitch and shaped by parallel formant resonators for the syllable's vowel.
///
/// This is deliberately not a text-to-speech engine. It needs no Windows language packs,
/// so Marathi and Punjabi sing exactly as well as Hindi and English — the trade is that
/// it sings vowels with consonant articulation rather than fully intelligible words.
/// It sounds like a synthetic voice, and it is on pitch and in time.
/// </summary>
public sealed class FormantVocalSynthesizer : IVocalSynthesizer
{
    public string Name => "Tier 0 — formant synthesis (CPU, offline)";

    public VocalCapabilities Capabilities => new(
        ["English", "Hindi", "Marathi", "Punjabi"],
        ["Male", "Female", "Child"],
        "Vowel-accurate singing with consonant onsets; no external voice packs required.");

    // Formant centres for a male vocal tract (Hz): F1..F4 per vowel.
    private static readonly Dictionary<Vowel, double[]> Formants = new()
    {
        [Vowel.A] = [730, 1090, 2440, 3400],
        [Vowel.E] = [530, 1840, 2480, 3400],
        [Vowel.I] = [270, 2290, 3010, 3500],
        [Vowel.O] = [570, 840, 2410, 3300],
        [Vowel.U] = [300, 870, 2240, 3300],
    };

    private static readonly double[] FormantBandwidths = [70, 100, 140, 190];
    private static readonly double[] FormantGains = [1.0, 0.62, 0.34, 0.18];

    /// <summary>Vocal-tract length scaling. A child tract is shorter, so formants rise.</summary>
    private static double TractScale(VoiceType voice) => voice switch
    {
        VoiceType.Male => 1.00,
        VoiceType.Female => 1.16,
        _ => 1.32,
    };

    private static double BreathAmount(VoiceType voice) => voice switch
    {
        VoiceType.Male => 0.030,
        VoiceType.Female => 0.045,
        _ => 0.028,
    };

    public float[] Synthesize(Part vocalPart, SongScore score, VoiceType voiceType, int sampleRate,
                              int totalSamples, Rng rng)
    {
        var buffer = new float[totalSamples];
        double spb = score.SecondsPerBeat;
        var tuning = score.Tuning;
        double scale = TractScale(voiceType);
        double breath = BreathAmount(voiceType);

        var notes = vocalPart.Notes.OrderBy(n => n.StartBeat).ToList();
        for (int index = 0; index < notes.Count; index++)
        {
            var note = notes[index];
            var syllable = note.Syllable ?? new Syllable("aa", Vowel.A, OnsetClass.None, true);

            // Vocals are humanised less than instruments — a singer lands closer to the beat.
            double jitter = rng.Range(-tuning.TimingJitterMs, tuning.TimingJitterMs) * 0.4 / 1000.0;
            double start = note.StartBeat * spb + jitter;
            double hold = Math.Max(0.09, note.LengthBeats * spb);

            // Legato: if the next syllable follows immediately, don't fully release.
            bool legato = index + 1 < notes.Count &&
                          notes[index + 1].StartBeat - (note.StartBeat + note.LengthBeats) < 0.06;

            double freq = tuning.FrequencyOf(note.Midi, partIndex: 0);
            SingSyllable(buffer, sampleRate, start, hold, freq, note.Velocity, syllable,
                         scale, breath, legato, tuning, rng);
        }

        // Vocal bus: gentle compression, de-ess, then a touch of air.
        Dynamics.Compress(buffer, sampleRate, thresholdDb: -19, ratio: 3.2,
                          attackMs: 12, releaseMs: 140, makeupDb: 3.5);
        var deEss = Biquad.HighShelf(sampleRate, 6200, -3.5);
        deEss.ProcessInPlace(buffer);
        return buffer;
    }

    private static void SingSyllable(float[] buffer, int sampleRate, double startSeconds, double holdSeconds,
                                     double freq, double velocity, Syllable syllable, double tractScale,
                                     double breathAmount, bool legato, TuningProfile tuning, Rng rng)
    {
        double consonantSeconds = ConsonantLength(syllable.Onset);
        double release = legato ? 0.03 : 0.09;
        double totalSeconds = consonantSeconds + holdSeconds + release;

        int start = (int)(startSeconds * sampleRate);
        int count = (int)(totalSeconds * sampleRate);
        if (start < 0) { count += start; start = 0; }
        if (start >= buffer.Length || count <= 0) return;
        count = Math.Min(count, buffer.Length - start);

        // Formant bank for this vowel, scaled for the voice type.
        double[] centres = Formants[syllable.Vowel];
        var filters = new Biquad[4];
        for (int f = 0; f < 4; f++)
        {
            double centre = centres[f] * tractScale;
            double q = centre / FormantBandwidths[f];
            filters[f] = Biquad.BandPass(sampleRate, centre, q);
        }

        // A nasal murmur uses a low extra resonance instead of the open first formant.
        var nasal = syllable.Onset == OnsetClass.Nasal
            ? Biquad.BandPass(sampleRate, 260 * tractScale, 6.0)
            : null;
        var consonantFilter = syllable.Onset switch
        {
            OnsetClass.Fricative => Biquad.BandPass(sampleRate, 5200, 1.1),
            OnsetClass.Plosive => Biquad.BandPass(sampleRate, 2100, 0.9),
            OnsetClass.Liquid => Biquad.LowPass(sampleRate, 1400, 0.8),
            _ => null,
        };

        double sourcePhase = rng.NextDouble();
        double vibPhase = rng.NextDouble();
        double vibRate = tuning.VibratoRateHz / sampleRate;
        double vibDepth = tuning.VibratoDepthCents / 1200.0;
        double vibOnset = tuning.VibratoOnsetMs / 1000.0;
        double noiseState = 0;
        double jitterState = 0;

        // Slight upward pitch approach into the note, like a real attack.
        double approachSeconds = Math.Min(0.05, holdSeconds * 0.2);

        for (int i = 0; i < count; i++)
        {
            double t = i / (double)sampleRate;
            double sample;

            if (t < consonantSeconds)
            {
                // Consonant burst before the vowel.
                double u = t / Math.Max(1e-6, consonantSeconds);
                double noise = rng.NextDouble() * 2 - 1;
                double shaped = consonantFilter?.Process(noise) ?? noise * 0.2;
                double shape = syllable.Onset == OnsetClass.Plosive
                    ? Math.Exp(-u * 9) * (u < 0.35 ? 0.25 : 1.0)     // brief silence then burst
                    : Math.Sin(Math.PI * u);
                sample = shaped * shape * 0.30;
            }
            else
            {
                double vt = t - consonantSeconds;
                double env = VowelEnvelope(vt, holdSeconds, release, legato);
                if (env <= 0) break;

                // Pitch: vibrato that fades in, plus micro-jitter so it is not a machine tone.
                double vibAmount = vibDepth * Math.Clamp((vt - vibOnset) / 0.3, 0, 1);
                double vib = Math.Sin(2 * Math.PI * (vibPhase += vibRate)) * vibAmount;
                jitterState = 0.995 * jitterState + 0.005 * (rng.NextDouble() * 2 - 1);
                double approach = vt < approachSeconds
                    ? -0.02 * (1 - vt / Math.Max(1e-6, approachSeconds))
                    : 0;
                double f0 = freq * Math.Pow(2.0, vib + jitterState * 0.004 + approach);

                // Glottal source: a band-limited saw approximates the harmonic-rich
                // glottal pulse train; breath noise fills the upper spectrum.
                double dt = f0 / sampleRate;
                double source = Osc.Saw(ref sourcePhase, dt) * 0.7;
                noiseState = 0.6 * noiseState + 0.4 * (rng.NextDouble() * 2 - 1);
                source += noiseState * breathAmount * 3.0;

                double sum = 0;
                for (int f = 0; f < 4; f++) sum += filters[f].Process(source) * FormantGains[f];
                if (nasal is not null && vt < 0.06)
                    sum = sum * 0.4 + nasal.Process(source) * 1.6;

                sample = sum * env;
            }

            buffer[start + i] += (float)(sample * velocity * 0.42);
        }
    }

    private static double ConsonantLength(OnsetClass onset) => onset switch
    {
        OnsetClass.Plosive => 0.045,
        OnsetClass.Fricative => 0.055,
        OnsetClass.Nasal => 0.040,
        OnsetClass.Liquid => 0.030,
        _ => 0.0,
    };

    private static double VowelEnvelope(double t, double hold, double release, bool legato)
    {
        double attack = legato ? 0.012 : 0.028;
        if (t < attack) return t / attack;
        if (t < hold)
        {
            // Very slight decay across the held vowel keeps it from sounding static.
            double k = (t - attack) / Math.Max(1e-6, hold - attack);
            return 1.0 - 0.18 * k;
        }
        double r = (t - hold) / Math.Max(1e-6, release);
        return r >= 1 ? 0 : 0.82 * (1 - r) * (1 - r);
    }

    /// <summary>Median fundamental frequency actually rendered, used to verify voice types differ.</summary>
    public static double MedianF0(Part vocalPart, TuningProfile tuning)
    {
        var freqs = vocalPart.Notes.Select(n => tuning.FrequencyOf(n.Midi)).OrderBy(f => f).ToList();
        return freqs.Count == 0 ? 0 : freqs[freqs.Count / 2];
    }
}
