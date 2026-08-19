using System.Text.Json;

namespace MusicTrackGen.Core;

public enum ReferenceState { Added, Analysed, Failed }

/// <summary>One track in the reference library the app learns from.</summary>
public sealed class ReferenceTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Language { get; set; } = nameof(Core.Language.Hindi);
    public string MoodTag { get; set; } = "Romantic";
    /// <summary>Provenance is mandatory in the UI: the app learns from whatever is added here.</summary>
    public string LicenceNote { get; set; } = "";
    public double DurationSeconds { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public ReferenceState State { get; set; } = ReferenceState.Added;
    public string? Error { get; set; }
    public TrackFeatures? Features { get; set; }
}

/// <summary>The reference library, persisted as one JSON file.</summary>
public sealed class ReferenceLibrary
{
    private readonly Workspace _workspace;
    public List<ReferenceTrack> Tracks { get; private set; } = [];

    public ReferenceLibrary(Workspace workspace)
    {
        _workspace = workspace;
        Load();
    }

    public void Load()
    {
        if (!File.Exists(_workspace.LibraryFile)) return;
        try
        {
            Tracks = JsonSerializer.Deserialize<List<ReferenceTrack>>(
                File.ReadAllText(_workspace.LibraryFile), StyleProfile.Json) ?? [];
        }
        catch (Exception)
        {
            // A corrupt library must not stop the app from starting; it is a cache of
            // analysis results and can be rebuilt by re-adding files.
            Tracks = [];
        }
    }

    public void Save() =>
        File.WriteAllText(_workspace.LibraryFile, JsonSerializer.Serialize(Tracks, StyleProfile.Json));

    /// <summary>Adds audio files, skipping ones already present. Returns the new entries.</summary>
    public List<ReferenceTrack> Add(IEnumerable<string> paths, Language language, string mood, string licenceNote)
    {
        var added = new List<ReferenceTrack>();
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            if (Tracks.Any(t => string.Equals(Path.GetFullPath(t.Path), full, StringComparison.OrdinalIgnoreCase)))
                continue;

            var track = new ReferenceTrack
            {
                Path = full,
                Title = Path.GetFileName(full),
                Language = language.ToString(),
                MoodTag = mood,
                LicenceNote = licenceNote,
            };
            Tracks.Add(track);
            added.Add(track);
        }
        if (added.Count > 0) Save();
        return added;
    }

    public void Remove(string id)
    {
        Tracks.RemoveAll(t => t.Id == id);
        Save();
    }

    /// <summary>Analyses everything not yet analysed. Failures are recorded per track, not thrown.</summary>
    public int AnalysePending(Action<ReferenceTrack, int, int>? progress = null, CancellationToken ct = default)
    {
        var pending = Tracks.Where(t => t.State != ReferenceState.Analysed).ToList();
        int done = 0;

        for (int i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var track = pending[i];
            try
            {
                if (!File.Exists(track.Path))
                    throw new FileNotFoundException($"File no longer exists: {track.Path}");

                var audio = WavIo.Read(track.Path);
                if (audio.DurationSeconds < 5)
                    throw new InvalidDataException("Track is under 5 seconds; too short to learn from.");

                track.Features = Analyser.Analyse(audio, track.Path);
                track.DurationSeconds = audio.DurationSeconds;
                track.State = ReferenceState.Analysed;
                track.Error = null;
                done++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                track.State = ReferenceState.Failed;
                track.Error = ex.Message;
            }
            progress?.Invoke(track, i + 1, pending.Count);
        }
        Save();
        return done;
    }
}

/// <summary>Aggregates per-track features into the style profiles that generation samples from.</summary>
public static class StyleProfileBuilder
{
    /// <summary>Below this many tracks a bucket is blended heavily toward the built-in prior.</summary>
    public const int RecommendedMinimum = 8;

    public static StyleProfile Build(Language language, string mood, IReadOnlyList<TrackFeatures> features)
    {
        var prior = StyleProfile.BuiltIn(language, mood);
        if (features.Count == 0) return prior;

        var profile = new StyleProfile
        {
            ProfileId = $"{language}/{mood}",
            Language = language.ToString(),
            MoodTag = mood,
            IsBuiltIn = false,
            SourceTrackCount = features.Count,
            BuiltUtc = DateTime.UtcNow,
            SourceTracks = features.Select(f => Path.GetFileName(f.SourcePath)).ToList(),
        };

        // ---- tempo, with 2-sigma outlier rejection ----
        var tempos = features.Select(f => f.Bpm).ToList();
        double mean = tempos.Average();
        double sd = StdDev(tempos, mean);
        var kept = tempos.Where(t => sd < 1e-6 || Math.Abs(t - mean) <= 2 * sd).ToList();
        if (kept.Count == 0) kept = tempos;
        profile.TempoMean = kept.Average();
        profile.TempoSd = Math.Max(2.0, StdDev(kept, profile.TempoMean));
        profile.TempoMin = Math.Max(45, kept.Min() - 4);
        profile.TempoMax = Math.Min(210, kept.Max() + 4);

        // ---- modes, weighted by how confident each key estimate was ----
        var modes = new Dictionary<string, double>();
        foreach (var f in features)
        {
            double weight = 0.25 + 0.75 * Math.Clamp(f.KeyConfidence, 0, 1);
            modes[f.ModeName] = modes.GetValueOrDefault(f.ModeName) + weight;
        }
        double modeTotal = modes.Values.Sum();
        profile.ModeHistogram = modeTotal <= 0
            ? prior.ModeHistogram
            : modes.ToDictionary(k => k.Key, k => k.Value / modeTotal);

        // ---- chord transitions, blended with the prior by how much data we have ----
        profile.DegreeTransitions = BlendTransitions(features, prior.DegreeTransitions);

        // ---- rhythm grids, averaged ----
        profile.Rhythm = AverageRhythm(features, prior.Rhythm);

        // ---- instrumentation, inferred from spectral balance ----
        profile.InstrumentWeights = InferInstruments(features);

        profile.LoudnessRms = features.Average(f => f.LoudnessRms);
        profile.BrightnessHz = features.Average(f => f.BrightnessHz);

        // ---- song form ----
        var forms = features.Where(f => f.FormFractions.Length >= 3).ToList();
        profile.FormTemplate = forms.Count == 0
            ? prior.FormTemplate
            : AverageForm(forms.Select(f => f.FormFractions).ToList(), prior.FormTemplate.Length);

        return profile;
    }

    private static double StdDev(IReadOnlyCollection<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sum = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sum / (values.Count - 1));
    }

    private static double[][] BlendTransitions(IReadOnlyList<TrackFeatures> features, double[][] prior)
    {
        var counts = new double[7][];
        for (int i = 0; i < 7; i++) counts[i] = new double[7];
        int observations = 0;

        foreach (var f in features)
        {
            var seq = f.DegreeSequence;
            for (int i = 1; i < seq.Count; i++)
            {
                int from = seq[i - 1], to = seq[i];
                if (from is < 0 or > 6 || to is < 0 or > 6) continue;
                counts[from][to]++;
                observations++;
            }
        }

        // With little data the prior dominates; with a few hundred observations the
        // measured matrix takes over. This is what stops a two-song library producing
        // a degenerate progression that only ever plays one chord.
        double weight = Math.Clamp(observations / 400.0, 0, 0.85);
        var result = new double[7][];
        for (int i = 0; i < 7; i++)
        {
            double rowTotal = counts[i].Sum();
            result[i] = new double[7];
            for (int j = 0; j < 7; j++)
            {
                double measured = rowTotal > 0 ? counts[i][j] / rowTotal : prior[i][j];
                result[i][j] = measured * weight + prior[i][j] * (1 - weight);
            }
        }
        return result;
    }

    private static Dictionary<string, double[]> AverageRhythm(IReadOnlyList<TrackFeatures> features,
                                                             Dictionary<string, double[]> prior)
    {
        var result = new Dictionary<string, double[]>();
        foreach (string role in new[] { "kick", "snare", "hat" })
        {
            var grids = features.Where(f => f.RhythmGrid.ContainsKey(role))
                                .Select(f => f.RhythmGrid[role])
                                .Where(g => g.Length == 16).ToList();
            if (grids.Count == 0)
            {
                if (prior.TryGetValue(role, out double[]? p)) result[role] = p;
                continue;
            }
            var avg = new double[16];
            foreach (var g in grids)
                for (int i = 0; i < 16; i++) avg[i] += g[i];
            for (int i = 0; i < 16; i++) avg[i] = Math.Clamp(avg[i] / grids.Count, 0.02, 0.95);
            result[role] = avg;
        }

        // Hand-drum roles follow the measured mid-band pattern, thinned out so tabla and
        // snare do not fire on identical steps.
        if (prior.ContainsKey("tabla") && result.TryGetValue("snare", out double[]? snare))
        {
            result["tabla"] = snare.Select((v, i) => Math.Clamp(v * (i % 2 == 0 ? 0.85 : 0.55), 0.02, 0.9)).ToArray();
            result["dholak"] = snare.Select((v, i) => Math.Clamp(v * (i % 4 == 0 ? 0.7 : 0.35), 0.02, 0.8)).ToArray();
        }
        return result;
    }

    /// <summary>
    /// Instrument mix inferred from where the reference tracks put their energy. This is a
    /// heuristic, not source separation: bright, high-energy libraries get more plucked and
    /// lead material, bass-heavy ones get more pad.
    /// </summary>
    private static Dictionary<string, double> InferInstruments(IReadOnlyList<TrackFeatures> features)
    {
        double low = features.Average(f => f.LowEnergy);
        double mid = features.Average(f => f.MidEnergy);
        double high = features.Average(f => f.HighEnergy);
        double brightness = features.Average(f => f.BrightnessHz);

        var weights = new Dictionary<string, double>
        {
            ["Pad"] = 0.18 + low * 0.9,
            ["Strings"] = 0.18 + mid * 0.6,
            ["Lead"] = 0.14 + Math.Clamp(brightness / 6000.0, 0, 0.5),
            ["Pluck"] = 0.14 + high * 1.6,
            ["Keys"] = 0.12 + mid * 0.4,
        };
        double total = weights.Values.Sum();
        return weights.ToDictionary(k => k.Key, k => k.Value / total);
    }

    /// <summary>Resamples each track's section proportions onto a fixed number of slots and averages.</summary>
    private static double[] AverageForm(List<double[]> forms, int slots)
    {
        var accumulator = new double[slots];
        foreach (var form in forms)
        {
            var resampled = ResampleFractions(form, slots);
            for (int i = 0; i < slots; i++) accumulator[i] += resampled[i];
        }
        for (int i = 0; i < slots; i++) accumulator[i] /= forms.Count;
        double total = accumulator.Sum();
        return total <= 0 ? accumulator : accumulator.Select(v => v / total).ToArray();
    }

    private static double[] ResampleFractions(double[] fractions, int slots)
    {
        var result = new double[slots];
        if (fractions.Length == 0) return result;

        // Walk the cumulative distribution and split it into `slots` equal spans.
        var cumulative = new double[fractions.Length + 1];
        for (int i = 0; i < fractions.Length; i++) cumulative[i + 1] = cumulative[i] + fractions[i];
        double total = cumulative[^1];
        if (total <= 0) return result;

        for (int s = 0; s < slots; s++)
        {
            double from = total * s / slots;
            double to = total * (s + 1) / slots;
            for (int i = 0; i < fractions.Length; i++)
            {
                double overlap = Math.Min(to, cumulative[i + 1]) - Math.Max(from, cumulative[i]);
                if (overlap > 0) result[s] += overlap;
            }
        }
        double sum = result.Sum();
        return sum <= 0 ? result : result.Select(v => v / sum).ToArray();
    }
}

/// <summary>Loads, saves and builds style profiles.</summary>
public sealed class StyleProfileStore(Workspace workspace)
{
    private readonly Workspace _workspace = workspace;

    public IEnumerable<string> ListProfileIds() =>
        Directory.Exists(_workspace.ProfilesDirectory)
            ? Directory.EnumerateFiles(_workspace.ProfilesDirectory, "*.json")
                .Select(p => Path.GetFileNameWithoutExtension(p)!)
            : [];

    public void Save(StyleProfile profile) =>
        File.WriteAllText(_workspace.ProfilePath(profile.ProfileId), profile.ToJson());

    /// <summary>
    /// Returns the trained profile for this bucket, or the built-in fallback when the
    /// library has nothing for it. Callers surface which one was used.
    /// </summary>
    public StyleProfile Get(Language language, string mood)
    {
        string path = _workspace.ProfilePath($"{language}/{mood}");
        if (File.Exists(path))
        {
            try
            {
                var loaded = StyleProfile.FromJson(File.ReadAllText(path));
                if (loaded is not null) return loaded;
            }
            catch (Exception) { /* fall through to the built-in profile */ }
        }
        return StyleProfile.BuiltIn(language, mood);
    }

    /// <summary>Rebuilds every profile the library has data for. Returns the profiles written.</summary>
    public List<StyleProfile> RebuildAll(ReferenceLibrary library, Action<string>? log = null)
    {
        var built = new List<StyleProfile>();
        var groups = library.Tracks
            .Where(t => t is { State: ReferenceState.Analysed, Features: not null })
            .GroupBy(t => (t.Language, t.MoodTag));

        foreach (var group in groups)
        {
            if (!Enum.TryParse<Language>(group.Key.Language, out var language)) continue;
            var features = group.Select(t => t.Features!).ToList();
            var profile = StyleProfileBuilder.Build(language, group.Key.MoodTag, features);
            Save(profile);
            built.Add(profile);

            string warning = features.Count < StyleProfileBuilder.RecommendedMinimum
                ? $"  (below the recommended {StyleProfileBuilder.RecommendedMinimum} tracks — blended with the built-in prior)"
                : "";
            log?.Invoke($"Built {profile.ProfileId} from {features.Count} track(s){warning}");
        }
        return built;
    }
}
