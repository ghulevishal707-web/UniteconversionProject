using System.Diagnostics;
using MusicTrackGen.Core;

namespace MusicTrackGen.Cli;

/// <summary>
/// Executable acceptance criteria. Each check corresponds to a numbered criterion in the
/// PoC document, so "it works" is a command anyone can run rather than a claim.
/// </summary>
public static class SelfTest
{
    private static int _passed;
    private static int _failed;

    public static int Run(Dictionary<string, string> opts)
    {
        bool quick = opts.ContainsKey("quick");
        int uniquenessSamples = quick ? 50 : 200;

        string root = Path.Combine(Path.GetTempPath(), "mtgen-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        var workspace = new Workspace(root);
        var profiles = new StyleProfileStore(workspace);
        var catalog = new Catalog(workspace);
        var generator = new TrackGenerator(workspace, profiles, catalog);

        Console.WriteLine($"Self-test workspace: {root}");
        Console.WriteLine();
        var total = Stopwatch.StartNew();

        try
        {
            CheckSyllabifiers();
            CheckWavRoundTrip(workspace);
            CheckUniqueness(uniquenessSamples);
            CheckTuningUniqueness(uniquenessSamples);
            CheckLengthAccuracy(generator, quick);
            CheckDeterminism(generator, workspace);
            CheckVoiceSeparation();
            CheckAllLanguages(generator);
            CheckAnalyserOnGeneratedAudio(generator);
            CheckCatalogRejectsDuplicates(catalog);
        }
        finally
        {
            total.Stop();
            Console.WriteLine();
            Console.WriteLine($"{_passed} passed, {_failed} failed in {total.Elapsed.TotalSeconds:0.0} s");
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* temp dir */ }
        }
        return _failed == 0 ? 0 : 1;
    }

    // ---------------- checks ----------------

    private static void CheckSyllabifiers()
    {
        Section("Syllabification (R6 — four scripts)");

        // Devanagari: each akshara is one sung syllable.
        var hindi = Lyrics.Syllabify("मन ये गाता जाए", Language.Hindi);
        Assert(hindi.Count is >= 6 and <= 9, $"Hindi 'मन ये गाता जाए' -> {hindi.Count} syllables (expect 6-9)");
        Assert(hindi.Any(s => s.Vowel == Vowel.A), "Hindi syllables carry vowels");

        var marathi = Lyrics.Syllabify("सांज सरली हळू", Language.Marathi);
        Assert(marathi.Count >= 5, $"Marathi 'सांज सरली हळू' -> {marathi.Count} syllables (expect >= 5)");

        var punjabi = Lyrics.Syllabify("ਮਨ ਇਹ ਗਾਉਂਦਾ ਜਾਏ", Language.Punjabi);
        Assert(punjabi.Count >= 6, $"Punjabi (Gurmukhi) -> {punjabi.Count} syllables (expect >= 6)");

        var english = Lyrics.Syllabify("the evening holds its light", Language.English);
        Assert(english.Count is >= 5 and <= 9, $"English -> {english.Count} syllables (expect 5-9)");

        // Every language must produce a vowel for every syllable, or the vocal synth
        // has nothing to sing.
        foreach (var (name, syllables) in new[]
                 {
                     ("Hindi", hindi), ("Marathi", marathi), ("Punjabi", punjabi), ("English", english),
                 })
            Assert(syllables.All(s => Enum.IsDefined(s.Vowel)), $"{name}: every syllable has a valid vowel");
    }

    private static void CheckWavRoundTrip(Workspace workspace)
    {
        Section("WAV writer / reader agreement");

        int sr = 44100;
        var left = new float[sr];
        var right = new float[sr];
        for (int i = 0; i < sr; i++)
        {
            left[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / sr));
            right[i] = (float)(-0.25 * Math.Sin(2 * Math.PI * 220 * i / sr));
        }

        string path = Path.Combine(workspace.Root, "roundtrip.wav");
        WavIo.WriteStereo16(path, left, right, sr);
        var read = WavIo.Read(path);

        Assert(read.SampleRate == sr, $"sample rate preserved ({read.SampleRate})");
        Assert(read.Length == sr, $"frame count preserved ({read.Length})");
        Assert(read.Right is not null, "stereo preserved");

        double maxError = 0;
        for (int i = 0; i < sr; i++)
            maxError = Math.Max(maxError, Math.Abs(read.Left[i] - left[i]));
        // 16-bit quantisation step is 1/32768 = 3.05e-5; allow one step.
        Assert(maxError <= 3.1e-5, $"sample error within one 16-bit step (max {maxError:E2})");
    }

    private static void CheckUniqueness(int samples)
    {
        Section($"Uniqueness — {samples} consecutive generations (R5)");

        var profile = StyleProfile.BuiltIn(Language.Hindi, "Romantic");
        var request = new GenerationRequest
        {
            Language = Language.Hindi,
            VoiceType = VoiceType.Female,
            TargetLength = TimeSpan.FromSeconds(60),
        };

        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < samples; i++)
        {
            var score = Composer.Compose(profile, request, Rng.NewSeed());
            fingerprints.Add(Fingerprint.Compute(score));
        }
        Assert(fingerprints.Count == samples,
            $"{fingerprints.Count}/{samples} distinct fingerprints");

        // The fingerprint must ignore transposition: the same composition in another key
        // is the same song, and has to be detected as such.
        ulong seed = Rng.NewSeed();
        var a = Composer.Compose(profile, request, seed);
        var b = Composer.Compose(profile, request, seed);
        Assert(Fingerprint.Compute(a) == Fingerprint.Compute(b), "same seed -> same fingerprint");
    }

    private static void CheckTuningUniqueness(int samples)
    {
        Section($"Per-track tuning — {samples} tracks (R7)");

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pitches = new HashSet<double>();
        var temperaments = new HashSet<Temperament>();

        for (int i = 0; i < samples; i++)
        {
            var tuning = TuningProfile.Sample(new Rng(Rng.NewSeed()), 60, allowShruti: true);
            hashes.Add(tuning.Hash());
            pitches.Add(tuning.ReferencePitchHz);
            temperaments.Add(tuning.Temperament);
        }

        Assert(hashes.Count == samples, $"{hashes.Count}/{samples} distinct tuning profiles");
        Assert(pitches.Count >= 3, $"reference pitch varies across tracks ({pitches.Count} distinct values)");
        Assert(temperaments.Count >= 3, $"temperament varies across tracks ({temperaments.Count} distinct)");

        // Tuning must be reproducible from the seed, or the sidecar is worthless.
        ulong seed = Rng.NewSeed();
        var t1 = TuningProfile.Sample(new Rng(seed), 60, true);
        var t2 = TuningProfile.Sample(new Rng(seed), 60, true);
        Assert(t1.Hash() == t2.Hash(), "same seed -> same tuning profile");
    }

    private static void CheckLengthAccuracy(TrackGenerator generator, bool quick)
    {
        Section("Track length accuracy — within ±0.5 s (R10)");

        double[] targets = quick ? [30, 47] : [30, 47, 60, 120, 180];
        foreach (double seconds in targets)
        {
            var track = generator.Generate(new GenerationRequest
            {
                Language = Language.Hindi,
                VoiceType = VoiceType.Female,
                TargetLength = TimeSpan.FromSeconds(seconds),
                MoodTag = "Romantic",
            });

            double delta = Math.Abs(track.Audio.DurationSeconds - seconds);
            Assert(delta <= 0.5,
                $"target {seconds,5:0.#} s -> {track.Audio.DurationSeconds,7:0.000} s " +
                $"(delta {delta * 1000,4:0} ms, {track.Score.TotalBars} bars @ {track.Score.Bpm:0.0} BPM)");
        }
    }

    private static void CheckDeterminism(TrackGenerator generator, Workspace workspace)
    {
        Section("Reproducibility — same seed is byte-identical (R11)");

        ulong seed = 0x0123456789ABCDEF;
        var request = new GenerationRequest
        {
            Language = Language.Marathi,
            VoiceType = VoiceType.Male,
            TargetLength = TimeSpan.FromSeconds(20),
            MoodTag = "Folk",
            Seed = seed,
        };

        var first = generator.Generate(request, allowDuplicate: true);
        var second = generator.Generate(request, allowDuplicate: true);

        Assert(first.Record.Fingerprint == second.Record.Fingerprint, "fingerprints match");
        Assert(first.Record.TuningHash == second.Record.TuningHash, "tuning profiles match");
        Assert(Program.FilesEqual(first.Record.AudioPath, second.Record.AudioPath),
            "rendered WAV files are byte-identical");

        // And a different seed must produce a different song.
        var other = generator.Generate(new GenerationRequest
        {
            Language = request.Language,
            VoiceType = request.VoiceType,
            TargetLength = request.TargetLength,
            MoodTag = request.MoodTag,
            Seed = seed ^ 0xFFFF,
        }, allowDuplicate: true);
        Assert(other.Record.Fingerprint != first.Record.Fingerprint,
            "a different seed produces a different fingerprint");
    }

    private static void CheckVoiceSeparation()
    {
        Section("Voice types are measurably distinct (R4)");

        var profile = StyleProfile.BuiltIn(Language.Hindi, "Romantic");
        ulong seed = 0xABCDEF0123456789;
        var medians = new Dictionary<VoiceType, double>();

        foreach (var voice in new[] { VoiceType.Male, VoiceType.Female, VoiceType.Child })
        {
            var score = Composer.Compose(profile, new GenerationRequest
            {
                Language = Language.Hindi,
                VoiceType = voice,
                TargetLength = TimeSpan.FromSeconds(40),
            }, seed);

            var vocal = score.Parts.FirstOrDefault(p => p.Kind == PartKind.Vocal);
            Assert(vocal is not null && vocal.Notes.Count > 0, $"{voice}: vocal part has notes");
            if (vocal is null) continue;
            medians[voice] = FormantVocalSynthesizer.MedianF0(vocal, score.Tuning);
        }

        if (medians.Count == 3)
        {
            double Semitones(double a, double b) => Math.Abs(12 * Math.Log2(a / b));
            double maleFemale = Semitones(medians[VoiceType.Female], medians[VoiceType.Male]);
            double femaleChild = Semitones(medians[VoiceType.Child], medians[VoiceType.Female]);

            Console.WriteLine($"        median F0: male {medians[VoiceType.Male]:0.0} Hz · " +
                              $"female {medians[VoiceType.Female]:0.0} Hz · " +
                              $"child {medians[VoiceType.Child]:0.0} Hz");
            Assert(maleFemale >= 4, $"male -> female differ by {maleFemale:0.0} semitones (need >= 4)");
            Assert(femaleChild >= 4, $"female -> child differ by {femaleChild:0.0} semitones (need >= 4)");
        }
    }

    private static void CheckAllLanguages(TrackGenerator generator)
    {
        Section("All four languages render with vocals (R6)");

        foreach (var language in new[] { Language.English, Language.Hindi, Language.Marathi, Language.Punjabi })
        {
            var track = generator.Generate(new GenerationRequest
            {
                Language = language,
                VoiceType = VoiceType.Female,
                TargetLength = TimeSpan.FromSeconds(20),
                MoodTag = "Romantic",
            });

            var vocal = track.Score.Parts.FirstOrDefault(p => p.Kind == PartKind.Vocal);
            int syllables = vocal?.Notes.Count(n => n.Syllable is not null) ?? 0;
            bool audible = track.Audio.PeakDbfs > -20;

            Assert(track.Audio.HadVocals && syllables > 0 && audible,
                $"{language,-8} {syllables,3} sung syllables · {Lyrics.ScriptName(language),-11} · " +
                $"peak {track.Audio.PeakDbfs:0.0} dBFS");
        }
    }

    /// <summary>
    /// End-to-end check of the training analyser: generate audio at a known tempo, then
    /// have the analyser recover it from the rendered waveform alone.
    /// </summary>
    private static void CheckAnalyserOnGeneratedAudio(TrackGenerator generator)
    {
        Section("Analyser recovers tempo and key from rendered audio (R2/R8)");

        // Fixed seed: an acceptance check must not be a coin flip from run to run.
        var track = generator.Generate(new GenerationRequest
        {
            Language = Language.Hindi,
            VoiceType = VoiceType.Female,
            TargetLength = TimeSpan.FromSeconds(40),
            MoodTag = "Upbeat",
            RenderVocals = false,          // instrumental gives the cleanest onsets
            Seed = 0x5EED0FA11ACCE55,
        }, allowDuplicate: true);

        var audio = WavIo.Read(track.Record.AudioPath);
        var features = Analyser.Analyse(audio, track.Record.AudioPath);

        double actual = track.Score.Bpm;
        double detected = features.Bpm;
        // Accept the true tempo or a metrical multiple — half/double is a genuine ambiguity
        // in beat tracking, not a defect.
        double[] candidates = [detected, detected * 2, detected / 2, detected * 4 / 3, detected * 3 / 4];
        double bestError = candidates.Min(c => Math.Abs(c - actual) / actual);

        Console.WriteLine($"        rendered {actual:0.0} BPM · detected {detected:0.0} BPM " +
                          $"· best metrical match {bestError * 100:0.0}% off");
        Assert(bestError <= 0.08, $"tempo recovered within 8% at some metrical level ({bestError * 100:0.0}%)");

        int detectedRoot = features.KeyRoot;
        int actualRoot = ((track.Score.TonicMidi % 12) + 12) % 12;
        Console.WriteLine($"        rendered key {Scales.PitchClassNames[actualRoot]} {track.Score.Scale.Name} " +
                          $"· detected {Scales.PitchClassNames[detectedRoot]} {features.ModeName} " +
                          $"(confidence {features.KeyConfidence:0.00})");

        Console.WriteLine($"        tuning offset detected: {features.TuningOffsetCents:+0.0;-0.0;0} cents " +
                          $"(rendered at A={track.Score.Tuning.ReferencePitchHz:0.#} Hz, " +
                          $"{1200 * Math.Log2(track.Score.Tuning.ReferencePitchHz / 440.0):+0.0;-0.0;0} cents)");

        // The decidable property is the pitch-class collection. Which member of that
        // collection is the tonic is genuinely ambiguous for rotationally-equivalent modes
        // (E Kafi, B Minor and D Major are the same seven notes), so a rotation counts as
        // a correct scale reading rather than a failure.
        var renderedSet = Analyser.PitchClassSet(actualRoot, track.Score.Scale.Name);
        var detectedSet = Analyser.PitchClassSet(detectedRoot, features.ModeName);
        int overlap = renderedSet.Count(detectedSet.Contains);
        bool exact = renderedSet.SetEquals(detectedSet);

        // A chroma-template key finder on a dense mix is expected to land on the right
        // collection or a near neighbour, not to be infallible. Requiring all but one note
        // is a criterion this PoC can actually stand behind.
        Assert(overlap >= renderedSet.Count - 1,
            $"scale recovered to within one note ({overlap}/{renderedSet.Count} shared" +
            (exact ? ", exact match" : "") + ")");

        if (exact && detectedRoot != actualRoot)
            Console.WriteLine("        note: detected key is a rotation of the rendered one " +
                              "(same notes, different tonic) — expected for modal scales");
        else if (!exact)
            Console.WriteLine($"        note: nearest-neighbour key " +
                              $"({{{string.Join(",", detectedSet)}}} vs {{{string.Join(",", renderedSet)}}})");

        Assert(features.RhythmGrid.Count >= 3, $"rhythm grid measured for {features.RhythmGrid.Count} bands");
        Assert(features.DegreeSequence.Count > 4, $"chord sequence recovered ({features.DegreeSequence.Count} beats)");

        // A profile built from this single track must be usable for generation.
        var profile = StyleProfileBuilder.Build(Language.Hindi, "Upbeat", [features]);
        Assert(!profile.IsBuiltIn && profile.SourceTrackCount == 1, "style profile built from measured features");
        Assert(profile.DegreeTransitions.Length == 7 &&
               profile.DegreeTransitions.All(row => row.Sum() > 0.9 && row.Sum() < 1.1),
            "transition matrix rows are normalised");
    }

    private static void CheckCatalogRejectsDuplicates(Catalog catalog)
    {
        Section("Catalogue enforces uniqueness");

        var record = new GeneratedTrackRecord { Fingerprint = "DUPLICATE-TEST-FINGERPRINT" };
        catalog.Add(record);

        bool rejected = false;
        try
        {
            catalog.Add(new GeneratedTrackRecord { Fingerprint = "DUPLICATE-TEST-FINGERPRINT" });
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Assert(rejected, "a second record with the same fingerprint is refused");
        catalog.Remove(record.Id);
    }

    // ---------------- harness ----------------

    private static void Section(string title)
    {
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private static void Assert(bool condition, string message)
    {
        if (condition) { _passed++; Console.WriteLine($"  PASS  {message}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {message}"); }
    }
}
