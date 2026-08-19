namespace MusicTrackGen.Core;

/// <summary>A scale or raga: the semitone offsets available above the tonic.</summary>
public sealed record ScaleDef(string Name, int[] Semitones, bool IsRaga)
{
    public int Length => Semitones.Length;

    /// <summary>Midi note for a scale degree, wrapping into higher octaves.</summary>
    public int NoteAt(int tonicMidi, int degree)
    {
        int len = Semitones.Length;
        int octave = (int)Math.Floor(degree / (double)len);
        int idx = degree - octave * len;
        return tonicMidi + Semitones[idx] + 12 * octave;
    }
}

public static class Scales
{
    public static readonly ScaleDef Major = new("Major", [0, 2, 4, 5, 7, 9, 11], false);
    public static readonly ScaleDef Minor = new("Minor", [0, 2, 3, 5, 7, 8, 10], false);
    public static readonly ScaleDef HarmonicMinor = new("Harmonic minor", [0, 2, 3, 5, 7, 8, 11], false);

    // Hindustani ragas, expressed as the ascending scale (aroha) in semitones.
    public static readonly ScaleDef Bhairav = new("Bhairav", [0, 1, 4, 5, 7, 8, 11], true);
    public static readonly ScaleDef Yaman = new("Yaman", [0, 2, 4, 6, 7, 9, 11], true);
    public static readonly ScaleDef Bhupali = new("Bhupali", [0, 2, 4, 7, 9], true);
    public static readonly ScaleDef Kafi = new("Kafi", [0, 2, 3, 5, 7, 9, 10], true);
    public static readonly ScaleDef Asavari = new("Asavari", [0, 2, 3, 5, 7, 8, 10], true);
    public static readonly ScaleDef Khamaj = new("Khamaj", [0, 2, 4, 5, 7, 9, 10], true);

    public static readonly ScaleDef[] All =
        [Major, Minor, HarmonicMinor, Bhairav, Yaman, Bhupali, Kafi, Asavari, Khamaj];

    public static ScaleDef ByName(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Major;

    public static readonly string[] PitchClassNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static string NoteName(int midi) => $"{PitchClassNames[((midi % 12) + 12) % 12]}{midi / 12 - 1}";
}

public enum Temperament
{
    EqualTemperament,
    JustIntonation,
    Pythagorean,
    Shruti22,
}

/// <summary>
/// Per-track tuning and performance character. Sampled from the track seed, which
/// is what makes every generated track tuned differently (requirement R7).
/// </summary>
public sealed class TuningProfile
{
    public double ReferencePitchHz { get; init; } = 440.0;
    public Temperament Temperament { get; init; } = Temperament.EqualTemperament;
    /// <summary>Cents of detune applied per instrument slot, keyed by part index.</summary>
    public double[] InstrumentDetuneCents { get; init; } = [0, 0, 0, 0, 0, 0];
    public double TimingJitterMs { get; init; } = 12;
    public double VelocityJitter { get; init; } = 8;
    public double VibratoDepthCents { get; init; } = 35;
    public double VibratoRateHz { get; init; } = 5.2;
    public double VibratoOnsetMs { get; init; } = 180;
    public double StretchCentsPerOctave { get; init; }
    public int TonicMidi { get; init; } = 60;

    private static readonly double[] JustRatiosCents =
        [0, 111.73, 203.91, 315.64, 386.31, 498.04, 590.22, 701.96, 813.69, 884.36, 1017.60, 1088.27];

    private static readonly double[] PythagoreanCents =
        [0, 113.69, 203.91, 294.13, 407.82, 498.04, 611.73, 701.96, 792.18, 905.87, 996.09, 1109.78];

    // 22-shruti approximation: the 12 positions a fixed-fret instrument would use,
    // drawn from the shruti set rather than equal temperament.
    private static readonly double[] ShrutiCents =
        [0, 90.22, 203.91, 294.13, 386.31, 498.04, 588.27, 701.96, 792.18, 884.36, 996.09, 1088.27];

    /// <summary>Frequency in Hz for a midi note under this tuning.</summary>
    public double FrequencyOf(double midi, int partIndex = 0)
    {
        double cents = TemperamentCents(midi);
        if (partIndex >= 0 && partIndex < InstrumentDetuneCents.Length)
            cents += InstrumentDetuneCents[partIndex];

        // Stretch tuning: octaves widen slightly away from the reference note.
        double octavesFromA4 = (midi - 69.0) / 12.0;
        cents += StretchCentsPerOctave * octavesFromA4;

        return ReferencePitchHz * Math.Pow(2.0, (midi - 69.0) / 12.0) * Math.Pow(2.0, cents / 1200.0);
    }

    /// <summary>Cents offset from equal temperament for this note's scale position.</summary>
    private double TemperamentCents(double midi)
    {
        if (Temperament == Temperament.EqualTemperament) return 0;

        double[] table = Temperament switch
        {
            Temperament.JustIntonation => JustRatiosCents,
            Temperament.Pythagorean => PythagoreanCents,
            _ => ShrutiCents,
        };

        // Interval above the track tonic decides which tuned step applies.
        int semisFromTonic = ((int)Math.Round(midi) - TonicMidi) % 12;
        if (semisFromTonic < 0) semisFromTonic += 12;
        return table[semisFromTonic] - semisFromTonic * 100.0;
    }

    public string Describe()
    {
        string temperament = Temperament switch
        {
            Temperament.EqualTemperament => "12-TET",
            Temperament.JustIntonation => "just intonation",
            Temperament.Pythagorean => "Pythagorean",
            _ => "22-shruti",
        };
        return $"A={ReferencePitchHz:0.#} Hz · {temperament} · detune {InstrumentDetuneCents.Max():0.#}¢ · " +
               $"vibrato {VibratoDepthCents:0}¢ @ {VibratoRateHz:0.0} Hz";
    }

    /// <summary>Stable hash of the audible tuning parameters, used to prove tracks differ.</summary>
    public string Hash()
    {
        string s = string.Join('|',
            ReferencePitchHz.ToString("0.###"), Temperament,
            string.Join(',', InstrumentDetuneCents.Select(c => c.ToString("0.##"))),
            TimingJitterMs.ToString("0.##"), VelocityJitter.ToString("0.##"),
            VibratoDepthCents.ToString("0.##"), VibratoRateHz.ToString("0.###"),
            StretchCentsPerOctave.ToString("0.###"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(s)))[..12];
    }

    /// <summary>Draws a fresh tuning profile from the track seed.</summary>
    public static TuningProfile Sample(Rng rng, int tonicMidi, bool allowShruti)
    {
        double[] pitches = [432, 436, 440, 441, 442, 444];
        Temperament[] choices = allowShruti
            ? [Temperament.EqualTemperament, Temperament.JustIntonation, Temperament.Pythagorean, Temperament.Shruti22]
            : [Temperament.EqualTemperament, Temperament.JustIntonation, Temperament.Pythagorean];

        var detune = new double[8];
        for (int i = 0; i < detune.Length; i++)
        {
            double magnitude = rng.Range(3, 18);
            detune[i] = rng.Chance(0.5) ? magnitude : -magnitude;
        }

        return new TuningProfile
        {
            ReferencePitchHz = pitches[rng.PickWeighted([0.12, 0.08, 0.45, 0.05, 0.2, 0.1])],
            Temperament = choices[rng.PickWeighted(allowShruti ? [0.45, 0.2, 0.15, 0.2] : [0.6, 0.25, 0.15])],
            InstrumentDetuneCents = detune,
            TimingJitterMs = rng.Range(5, 25),
            VelocityJitter = rng.Range(4, 12),
            VibratoDepthCents = rng.Range(15, 60),
            VibratoRateHz = rng.Range(4.5, 6.5),
            VibratoOnsetMs = rng.Range(80, 400),
            StretchCentsPerOctave = rng.Range(0, 4),
            TonicMidi = tonicMidi,
        };
    }
}

public static class Chords
{
    /// <summary>
    /// Triad built on a scale degree, voiced inside the scale so it always fits the
    /// mode (this is what keeps raga-based progressions in character).
    /// </summary>
    public static int[] Triad(ScaleDef scale, int tonicMidi, int degree)
    {
        int len = scale.Length;
        int third = len >= 7 ? 2 : 1;
        int fifth = len >= 7 ? 4 : 2;
        return
        [
            scale.NoteAt(tonicMidi, degree),
            scale.NoteAt(tonicMidi, degree + third),
            scale.NoteAt(tonicMidi, degree + fifth),
        ];
    }

    /// <summary>Roman-ish label for display, derived from the actual third interval.</summary>
    public static string Label(ScaleDef scale, int degree)
    {
        string[] numerals = ["I", "II", "III", "IV", "V", "VI", "VII"];
        int len = scale.Length;
        int idx = ((degree % len) + len) % len;
        string numeral = numerals[Math.Min(idx, numerals.Length - 1)];

        int root = scale.Semitones[idx];
        int thirdStep = len >= 7 ? 2 : 1;
        int thirdIdx = (idx + thirdStep) % len;
        int thirdSemis = scale.Semitones[thirdIdx] - root;
        if (thirdSemis < 0) thirdSemis += 12;
        return thirdSemis <= 3 ? numeral.ToLowerInvariant() : numeral;
    }
}
