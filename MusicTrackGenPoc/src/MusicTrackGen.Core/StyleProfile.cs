using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicTrackGen.Core;

/// <summary>
/// What the analyser learned from a set of reference tracks. Generation samples from
/// these distributions, never from a single source song.
/// </summary>
public sealed class StyleProfile
{
    public string ProfileId { get; set; } = "default";
    public string Language { get; set; } = nameof(Core.Language.Hindi);
    public string MoodTag { get; set; } = "Romantic";
    public int SourceTrackCount { get; set; }
    public DateTime BuiltUtc { get; set; } = DateTime.UtcNow;
    public bool IsBuiltIn { get; set; }

    public double TempoMean { get; set; } = 92;
    public double TempoSd { get; set; } = 7;
    public double TempoMin { get; set; } = 70;
    public double TempoMax { get; set; } = 120;

    /// <summary>Scale/raga name to relative frequency.</summary>
    public Dictionary<string, double> ModeHistogram { get; set; } = new();

    /// <summary>7x7 P(next degree | current degree), transposition-invariant.</summary>
    public double[][] DegreeTransitions { get; set; } = [];

    /// <summary>Drum role to 16 onset probabilities, one per 1/16 grid position.</summary>
    public Dictionary<string, double[]> Rhythm { get; set; } = new();

    public Dictionary<string, double> InstrumentWeights { get; set; } = new();

    public double LoudnessRms { get; set; } = 0.16;
    public double BrightnessHz { get; set; } = 2200;

    /// <summary>Relative section lengths measured from reference song form.</summary>
    public double[] FormTemplate { get; set; } = [0.08, 0.22, 0.25, 0.20, 0.19, 0.06];

    /// <summary>Ids of the reference tracks this profile was aggregated from.</summary>
    public List<string> SourceTracks { get; set; } = [];

    [JsonIgnore]
    public bool UsesRaga => ModeHistogram.Keys.Any(k => Scales.ByName(k).IsRaga);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Built-in fallback used when the reference library is too small to train on.
    /// The UI states plainly when a generated track used one of these.
    /// </summary>
    // The Language property shadows the Language enum inside this type, so the enum is
    // spelled out fully here.
    public static StyleProfile BuiltIn(Core.Language language, string mood)
    {
        bool indic = language != Core.Language.English;
        var modes = indic
            ? new Dictionary<string, double>
            {
                ["Kafi"] = 0.26, ["Khamaj"] = 0.20, ["Yaman"] = 0.14,
                ["Bhupali"] = 0.12, ["Minor"] = 0.16, ["Major"] = 0.12,
            }
            : new Dictionary<string, double>
            {
                ["Major"] = 0.44, ["Minor"] = 0.34, ["HarmonicMinor"] = 0.08, ["Khamaj"] = 0.14,
            };

        (double tempo, double sd) = mood.ToLowerInvariant() switch
        {
            "upbeat" => (124, 8.0),
            "sad" => (74, 6.0),
            "devotional" => (84, 6.0),
            "folk" => (108, 9.0),
            _ => (92, 7.0),
        };

        var instruments = indic
            ? new Dictionary<string, double> { ["Strings"] = 0.30, ["Pad"] = 0.26, ["Lead"] = 0.24, ["Pluck"] = 0.20 }
            : new Dictionary<string, double> { ["Pad"] = 0.30, ["Keys"] = 0.28, ["Strings"] = 0.24, ["Lead"] = 0.18 };

        return new StyleProfile
        {
            ProfileId = $"builtin/{language}/{mood}",
            Language = language.ToString(),
            MoodTag = mood,
            IsBuiltIn = true,
            SourceTrackCount = 0,
            TempoMean = tempo,
            TempoSd = sd,
            TempoMin = tempo - 18,
            TempoMax = tempo + 18,
            ModeHistogram = modes,
            DegreeTransitions = DefaultTransitions(),
            Rhythm = DefaultRhythm(indic, mood),
            InstrumentWeights = instruments,
            LoudnessRms = 0.16,
            BrightnessHz = indic ? 2000 : 2400,
        };
    }

    /// <summary>
    /// Plausible tonal motion: strong pull to I, IV, V and vi. Used before any
    /// training has happened, and as the prior that measured data is blended into.
    /// </summary>
    public static double[][] DefaultTransitions() =>
    [
        //  I     II    III   IV    V     VI    VII
        [0.04, 0.12, 0.10, 0.26, 0.22, 0.20, 0.06],   // from I
        [0.16, 0.04, 0.10, 0.16, 0.30, 0.16, 0.08],   // from II
        [0.18, 0.10, 0.04, 0.26, 0.16, 0.20, 0.06],   // from III
        [0.24, 0.14, 0.08, 0.04, 0.30, 0.14, 0.06],   // from IV
        [0.32, 0.10, 0.10, 0.16, 0.04, 0.22, 0.06],   // from V
        [0.16, 0.18, 0.10, 0.28, 0.22, 0.02, 0.04],   // from VI
        [0.30, 0.08, 0.12, 0.16, 0.24, 0.06, 0.04],   // from VII
    ];

    private static Dictionary<string, double[]> DefaultRhythm(bool indic, string mood)
    {
        bool busy = mood.Equals("Upbeat", StringComparison.OrdinalIgnoreCase) ||
                    mood.Equals("Folk", StringComparison.OrdinalIgnoreCase);

        var rhythm = new Dictionary<string, double[]>
        {
            ["kick"] = [0.98, 0, 0, 0.10, 0.30, 0, busy ? 0.35 : 0.10, 0,
                        0.85, 0, 0.10, 0, 0.25, 0, 0.12, 0.20],
            ["snare"] = [0, 0, 0.08, 0, 0.92, 0, 0.06, 0.12,
                         0, 0.05, 0.10, 0, 0.90, 0, 0.18, 0.25],
            ["hat"] = [0.45, 0.25, busy ? 0.75 : 0.50, 0.25, 0.45, 0.25, busy ? 0.75 : 0.50, 0.30,
                       0.45, 0.25, busy ? 0.75 : 0.50, 0.25, 0.45, 0.30, busy ? 0.70 : 0.45, 0.35],
        };

        if (indic)
        {
            rhythm["tabla"] = [0.70, 0.20, 0.45, 0.15, 0.60, 0.20, 0.40, 0.25,
                               0.65, 0.18, 0.42, 0.15, 0.58, 0.22, 0.48, 0.30];
            rhythm["dholak"] = [0.55, 0, 0.20, 0.10, 0.40, 0, 0.22, 0.12,
                                0.50, 0, 0.18, 0.10, 0.38, 0, 0.25, 0.18];
        }
        return rhythm;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static StyleProfile? FromJson(string json) =>
        JsonSerializer.Deserialize<StyleProfile>(json, Json);

    /// <summary>Human-readable summary shown in the Train tab so training is visibly real.</summary>
    public string Describe()
    {
        var modes = ModeHistogram.OrderByDescending(k => k.Value).Take(4)
            .Select(k => $"{k.Key} {k.Value * 100:0}%");
        var instruments = InstrumentWeights.OrderByDescending(k => k.Value).Take(4)
            .Select(k => $"{k.Key} {k.Value * 100:0}%");
        return string.Join(Environment.NewLine,
            $"Profile      {ProfileId}{(IsBuiltIn ? "  (built-in fallback)" : "")}",
            $"Sources      {SourceTrackCount} track(s)",
            $"Tempo        {TempoMean:0.0} ± {TempoSd:0.0} BPM   (range {TempoMin:0}–{TempoMax:0})",
            $"Modes        {string.Join(" · ", modes)}",
            $"Instruments  {string.Join(" · ", instruments)}",
            $"Loudness     RMS {LoudnessRms:0.000}   Brightness {BrightnessHz:0} Hz",
            $"Form         {string.Join(" / ", FormTemplate.Select(f => $"{f * 100:0}%"))}");
    }
}
