using System.Text.Json;
using System.Text.Json.Nodes;

namespace MusicTrackGen.Core;

public sealed class GeneratedTrack
{
    public required GeneratedTrackRecord Record { get; init; }
    public required SongScore Score { get; init; }
    public required RenderResult Audio { get; init; }
    public required StyleProfile Profile { get; init; }
}

/// <summary>
/// The full generate pipeline: profile -> score -> uniqueness check -> audio -> files.
/// One call per track; deterministic for a fixed seed.
/// </summary>
public sealed class TrackGenerator
{
    /// <summary>How many times to re-roll the seed when a fingerprint collides.</summary>
    public const int MaxSeedAttempts = 8;

    private readonly Workspace _workspace;
    private readonly StyleProfileStore _profiles;
    private readonly Catalog _catalog;
    private readonly IVocalSynthesizer _vocals;

    public TrackGenerator(Workspace workspace, StyleProfileStore profiles, Catalog catalog,
                          IVocalSynthesizer? vocals = null)
    {
        _workspace = workspace;
        _profiles = profiles;
        _catalog = catalog;
        _vocals = vocals ?? new FormantVocalSynthesizer();
    }

    public Catalog Catalog => _catalog;

    public IVocalSynthesizer VocalSynthesizer => _vocals;

    /// <summary>
    /// Generates one track and writes the WAV plus its sidecar JSON.
    /// </summary>
    /// <param name="allowDuplicate">
    /// Set when deliberately reproducing a known seed ("regenerate from seed"), where an
    /// identical fingerprint is the expected outcome rather than a collision.
    /// </param>
    public GeneratedTrack Generate(GenerationRequest request,
                                   Action<GenerationStage, double>? progress = null,
                                   bool allowDuplicate = false,
                                   CancellationToken ct = default)
    {
        var profile = _profiles.Get(request.Language, request.MoodTag);
        ulong seed = request.Seed ?? Rng.NewSeed();

        progress?.Invoke(GenerationStage.Composing, 0.0);

        SongScore score;
        string fingerprint;
        int attempt = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            score = Composer.Compose(profile, request, seed);
            fingerprint = Fingerprint.Compute(score);

            progress?.Invoke(GenerationStage.CheckingUniqueness, attempt / (double)MaxSeedAttempts);

            if (allowDuplicate || !_catalog.Contains(fingerprint)) break;

            if (attempt++ >= MaxSeedAttempts)
                throw new InvalidOperationException(
                    $"Could not produce a new song after {MaxSeedAttempts} attempts. " +
                    "The reference library is probably too small to support more variety — " +
                    "add more tracks on the Train screen and rebuild the style profiles.");

            // Re-roll deterministically from the colliding seed.
            seed = Rng.Hash64($"{seed}:retry{attempt}");
        }

        progress?.Invoke(GenerationStage.RenderingInstruments, 0.0);
        var audio = Renderer.Render(score, request, _vocals, seed, progress);

        progress?.Invoke(GenerationStage.Exporting, 0.0);

        string stem = $"{DateTime.Now:yyyyMMdd-HHmmss}_{request.Language}_{request.VoiceType}_{seed:X8}";
        string wavPath = Path.Combine(_workspace.GeneratedDirectory, stem + ".wav");
        string sidecarPath = Path.Combine(_workspace.GeneratedDirectory, stem + ".json");

        WavIo.WriteStereo16(wavPath, audio.Left, audio.Right, audio.SampleRate);

        var record = new GeneratedTrackRecord
        {
            Seed = seed,
            StyleProfileId = profile.ProfileId,
            UsedBuiltInProfile = profile.IsBuiltIn,
            Language = request.Language.ToString(),
            VoiceType = request.VoiceType.ToString(),
            MoodTag = request.MoodTag,
            RequestedLengthSeconds = request.TargetLength.TotalSeconds,
            ActualLengthSeconds = audio.DurationSeconds,
            Bpm = score.Bpm,
            KeyDescription = score.KeyDescription,
            TuningDescription = score.Tuning.Describe(),
            TuningHash = score.Tuning.Hash(),
            Fingerprint = fingerprint,
            AudioPath = wavPath,
            SidecarPath = sidecarPath,
            SeedAttempts = attempt,
            HadVocals = audio.HadVocals,
        };

        File.WriteAllText(sidecarPath, BuildSidecar(record, score, profile, audio));

        if (!allowDuplicate) _catalog.Add(record);

        progress?.Invoke(GenerationStage.Done, 1.0);
        return new GeneratedTrack { Record = record, Score = score, Audio = audio, Profile = profile };
    }

    /// <summary>
    /// Reproduces a catalogued track from its seed. Byte-identical output is the point:
    /// it is how the determinism claim is checked rather than asserted.
    /// </summary>
    public GeneratedTrack RegenerateFromSeed(GeneratedTrackRecord record, string? outputPath = null)
    {
        var request = new GenerationRequest
        {
            Language = Enum.Parse<Language>(record.Language),
            VoiceType = Enum.Parse<VoiceType>(record.VoiceType),
            MoodTag = record.MoodTag,
            TargetLength = TimeSpan.FromSeconds(record.RequestedLengthSeconds),
            Seed = record.Seed,
            RenderVocals = record.HadVocals,
        };

        var result = Generate(request, allowDuplicate: true);
        if (outputPath is not null)
            File.Copy(result.Record.AudioPath, outputPath, overwrite: true);
        return result;
    }

    private static string BuildSidecar(GeneratedTrackRecord record, SongScore score,
                                       StyleProfile profile, RenderResult audio)
    {
        var sections = new JsonArray();
        foreach (var s in score.Sections)
            sections.Add(new JsonObject
            {
                ["name"] = s.Name,
                ["startBar"] = s.StartBar,
                ["bars"] = s.Bars,
                ["hasVocal"] = s.HasVocal,
            });

        var chords = new JsonArray();
        foreach (int degree in score.ChordPerBar) chords.Add(Chords.Label(score.Scale, degree));

        var lyrics = new JsonArray();
        foreach (var line in score.Lyrics)
            lyrics.Add(new JsonObject
            {
                ["role"] = line.Role,
                ["text"] = line.Text,
                ["syllables"] = line.Count,
            });

        var root = new JsonObject
        {
            ["generator"] = "MusicTrackGen PoC",
            ["generatorVersion"] = typeof(TrackGenerator).Assembly.GetName().Version?.ToString() ?? "1.0",
            ["createdUtc"] = record.CreatedUtc.ToString("O"),
            ["seed"] = record.SeedHex,
            ["seedAttempts"] = record.SeedAttempts,
            ["fingerprint"] = record.Fingerprint,
            ["request"] = new JsonObject
            {
                ["language"] = record.Language,
                ["voiceType"] = record.VoiceType,
                ["mood"] = record.MoodTag,
                ["requestedLengthSeconds"] = record.RequestedLengthSeconds,
            },
            ["styleProfile"] = new JsonObject
            {
                ["id"] = profile.ProfileId,
                ["builtInFallback"] = profile.IsBuiltIn,
                ["sourceTrackCount"] = profile.SourceTrackCount,
                ["tempoMean"] = profile.TempoMean,
            },
            ["music"] = new JsonObject
            {
                ["bpm"] = Math.Round(score.Bpm, 3),
                ["key"] = score.KeyDescription,
                ["scale"] = score.Scale.Name,
                ["isRaga"] = score.Scale.IsRaga,
                ["bars"] = score.TotalBars,
                ["beatsPerBar"] = score.BeatsPerBar,
                ["sections"] = sections,
                ["chordsPerBar"] = chords,
            },
            ["tuning"] = new JsonObject
            {
                ["description"] = record.TuningDescription,
                ["hash"] = record.TuningHash,
                ["referencePitchHz"] = score.Tuning.ReferencePitchHz,
                ["temperament"] = score.Tuning.Temperament.ToString(),
                ["detuneCents"] = new JsonArray(score.Tuning.InstrumentDetuneCents
                    .Select(c => (JsonNode)JsonValue.Create(Math.Round(c, 2))!).ToArray()),
                ["timingJitterMs"] = Math.Round(score.Tuning.TimingJitterMs, 2),
                ["vibratoDepthCents"] = Math.Round(score.Tuning.VibratoDepthCents, 2),
                ["vibratoRateHz"] = Math.Round(score.Tuning.VibratoRateHz, 3),
                ["stretchCentsPerOctave"] = Math.Round(score.Tuning.StretchCentsPerOctave, 3),
            },
            ["lyrics"] = lyrics,
            ["audio"] = new JsonObject
            {
                ["path"] = Path.GetFileName(record.AudioPath),
                ["sampleRate"] = audio.SampleRate,
                ["durationSeconds"] = Math.Round(audio.DurationSeconds, 4),
                ["rms"] = Math.Round(audio.Rms, 5),
                ["peakDbfs"] = Math.Round(audio.PeakDbfs, 2),
                ["hadVocals"] = audio.HadVocals,
            },
            ["notice"] = "Machine-generated audio. Not for commercial release without review.",
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
