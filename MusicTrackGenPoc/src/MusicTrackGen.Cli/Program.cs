using System.Diagnostics;
using MusicTrackGen.Core;

namespace MusicTrackGen.Cli;

/// <summary>
/// Cross-platform harness for the same engine the Windows UI drives. It exists so the
/// generator can be exercised and verified without a desktop session — the WPF layer adds
/// no music logic of its own.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            var opts = ParseOptions(args.Skip(1));

            return command switch
            {
                "generate" or "gen" => Generate(opts),
                "train" => Train(opts),
                "list" => List(opts),
                "regenerate" or "regen" => Regenerate(opts),
                "selftest" => SelfTest.Run(opts),
                "help" or "--help" or "-h" => Help(),
                _ => Fail($"Unknown command '{command}'. Run 'mtgen help'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // ---------------- options ----------------

    public static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (!a.StartsWith("--")) continue;
            string key = a[2..];
            string value = i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : "true";
            // Repeatable options accumulate rather than overwrite.
            opts[key] = opts.TryGetValue(key, out string? existing) ? existing + "\n" + value : value;
        }
        return opts;
    }

    private static string Get(Dictionary<string, string> o, string key, string fallback) =>
        o.TryGetValue(key, out string? v) ? v : fallback;

    public static Workspace WorkspaceFrom(Dictionary<string, string> o) =>
        new(o.TryGetValue("root", out string? r) ? r : null);

    /// <summary>Accepts 90, 1:30 or 1m30s.</summary>
    public static TimeSpan ParseLength(string text)
    {
        text = text.Trim();
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            return TimeSpan.FromSeconds(int.Parse(parts[0]) * 60 + double.Parse(parts[1]));
        }
        if (text.EndsWith('s') && text.Contains('m'))
        {
            int mIdx = text.IndexOf('m');
            return TimeSpan.FromSeconds(int.Parse(text[..mIdx]) * 60 +
                                        double.Parse(text[(mIdx + 1)..^1]));
        }
        if (text.EndsWith('m')) return TimeSpan.FromMinutes(double.Parse(text[..^1]));
        return TimeSpan.FromSeconds(double.Parse(text.TrimEnd('s')));
    }

    // ---------------- commands ----------------

    private static int Generate(Dictionary<string, string> o)
    {
        var workspace = WorkspaceFrom(o);
        var profiles = new StyleProfileStore(workspace);
        var catalog = new Catalog(workspace);
        var generator = new TrackGenerator(workspace, profiles, catalog);

        var request = new GenerationRequest
        {
            Language = Enum.Parse<Language>(Get(o, "language", "Hindi"), true),
            VoiceType = Enum.Parse<VoiceType>(Get(o, "voice", "Female"), true),
            MoodTag = Get(o, "mood", "Romantic"),
            TargetLength = ParseLength(Get(o, "length", "60")),
            RenderVocals = !o.ContainsKey("no-vocals"),
            Seed = o.TryGetValue("seed", out string? s)
                ? Convert.ToUInt64(s, s.StartsWith("0x") ? 16 : 10)
                : null,
        };

        Console.WriteLine($"Generating {request.TargetLength:mm\\:ss} · {request.Language} · " +
                          $"{request.VoiceType} · {request.MoodTag}");

        var stopwatch = Stopwatch.StartNew();
        GenerationStage lastStage = GenerationStage.Composing;
        var track = generator.Generate(request, (stage, _) =>
        {
            if (stage == lastStage) return;
            lastStage = stage;
            Console.WriteLine($"  {stage}...");
        });
        stopwatch.Stop();

        PrintTrack(track, stopwatch.Elapsed);

        if (o.TryGetValue("out", out string? outPath))
        {
            File.Copy(track.Record.AudioPath, outPath, overwrite: true);
            Console.WriteLine($"  copied to    {outPath}");
        }
        return 0;
    }

    public static void PrintTrack(GeneratedTrack track, TimeSpan elapsed)
    {
        var r = track.Record;
        Console.WriteLine();
        Console.WriteLine($"  seed         {r.SeedHex}" + (r.SeedAttempts > 1 ? $"  ({r.SeedAttempts} attempts)" : ""));
        Console.WriteLine($"  fingerprint  {r.ShortFingerprint}");
        Console.WriteLine($"  key / tempo  {r.KeyDescription} · {r.Bpm:0.0} BPM");
        Console.WriteLine($"  tuning       {r.TuningDescription}");
        Console.WriteLine($"  tuning hash  {r.TuningHash}");
        Console.WriteLine($"  profile      {r.StyleProfileId}{(r.UsedBuiltInProfile ? "  (built-in fallback)" : "")}");
        Console.WriteLine($"  progression  {track.Score.ProgressionSummary()}");
        Console.WriteLine($"  form         {string.Join(" ", track.Score.Sections.Select(s => $"{s.Name}:{s.Bars}"))}");
        Console.WriteLine($"  lyrics       {track.Score.Lyrics.Count} line(s), " +
                          $"first: \"{track.Score.Lyrics.FirstOrDefault()?.Text}\"");
        Console.WriteLine($"  length       {r.ActualLengthSeconds:0.000} s " +
                          $"(requested {r.RequestedLengthSeconds:0.###} s, " +
                          $"delta {Math.Abs(r.ActualLengthSeconds - r.RequestedLengthSeconds) * 1000:0} ms)");
        Console.WriteLine($"  peak         {track.Audio.PeakDbfs:0.0} dBFS · RMS {track.Audio.Rms:0.000}");
        Console.WriteLine($"  wav          {r.AudioPath}");
        Console.WriteLine($"  sidecar      {r.SidecarPath}");
        Console.WriteLine($"  generated in {elapsed.TotalSeconds:0.00} s");
    }

    private static int Train(Dictionary<string, string> o)
    {
        var workspace = WorkspaceFrom(o);
        var library = new ReferenceLibrary(workspace);
        var profiles = new StyleProfileStore(workspace);

        if (o.TryGetValue("add", out string? add))
        {
            var files = new List<string>();
            foreach (string entry in add.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Directory.Exists(entry))
                    files.AddRange(Directory.EnumerateFiles(entry, "*.wav", SearchOption.AllDirectories));
                else if (File.Exists(entry)) files.Add(entry);
                else Console.Error.WriteLine($"  skipped (not found): {entry}");
            }

            var added = library.Add(files,
                Enum.Parse<Language>(Get(o, "language", "Hindi"), true),
                Get(o, "mood", "Romantic"),
                Get(o, "licence", "unspecified — set this before using the profile"));
            Console.WriteLine($"Added {added.Count} track(s) to the reference library " +
                              $"({library.Tracks.Count} total).");
        }

        if (!o.ContainsKey("no-analyse"))
        {
            Console.WriteLine("Analysing...");
            int done = library.AnalysePending((track, i, total) =>
            {
                string state = track.State == ReferenceState.Analysed
                    ? track.Features!.Summary()
                    : $"FAILED — {track.Error}";
                Console.WriteLine($"  [{i}/{total}] {track.Title}: {state}");
            });
            Console.WriteLine($"Analysed {done} track(s).");
        }

        var built = profiles.RebuildAll(library, Console.WriteLine);
        foreach (var profile in built)
        {
            Console.WriteLine();
            Console.WriteLine(profile.Describe());
        }
        if (built.Count == 0)
            Console.WriteLine("No profiles built — the library has no successfully analysed tracks yet.");
        return 0;
    }

    private static int List(Dictionary<string, string> o)
    {
        var workspace = WorkspaceFrom(o);
        var catalog = new Catalog(workspace);
        var (tracks, fingerprints, tunings) = catalog.Stats();

        Console.WriteLine($"Workspace: {workspace.Root}");
        Console.WriteLine($"{tracks} track(s) · {fingerprints} distinct fingerprint(s) · " +
                          $"{tunings} distinct tuning(s)");
        Console.WriteLine();
        Console.WriteLine($"{"created",-20} {"lang",-8} {"voice",-7} {"len",6} {"seed",-17} fingerprint");
        foreach (var r in catalog.Records.OrderByDescending(r => r.CreatedUtc).Take(40))
            Console.WriteLine($"{r.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} {r.Language,-8} " +
                              $"{r.VoiceType,-7} {r.ActualLengthSeconds,6:0.0} {r.SeedHex,-17} {r.ShortFingerprint}");
        return 0;
    }

    private static int Regenerate(Dictionary<string, string> o)
    {
        var workspace = WorkspaceFrom(o);
        var catalog = new Catalog(workspace);
        var generator = new TrackGenerator(workspace, new StyleProfileStore(workspace), catalog);

        string id = Get(o, "id", "");
        var record = catalog.Records.FirstOrDefault(r => r.Id == id || r.SeedHex.Equals(id, StringComparison.OrdinalIgnoreCase))
                     ?? catalog.Records.OrderByDescending(r => r.CreatedUtc).FirstOrDefault();
        if (record is null) return Fail("Nothing in the catalogue to regenerate.");

        Console.WriteLine($"Regenerating seed {record.SeedHex} from {record.CreatedUtc.ToLocalTime():g}");
        var stopwatch = Stopwatch.StartNew();
        var track = generator.RegenerateFromSeed(record, o.TryGetValue("out", out string? p) ? p : null);
        stopwatch.Stop();

        bool identical = FilesEqual(record.AudioPath, track.Record.AudioPath);
        PrintTrack(track, stopwatch.Elapsed);
        Console.WriteLine();
        Console.WriteLine(identical
            ? "  byte-identical to the original render."
            : "  WARNING: output differs from the original render.");
        return identical ? 0 : 2;
    }

    public static bool FilesEqual(string a, string b)
    {
        if (!File.Exists(a) || !File.Exists(b)) return false;
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        var ba = new byte[65536];
        var bb = new byte[65536];
        int read;
        while ((read = sa.Read(ba)) > 0)
        {
            int readB = sb.Read(bb, 0, read);
            if (readB != read) return false;
            if (!ba.AsSpan(0, read).SequenceEqual(bb.AsSpan(0, read))) return false;
        }
        return true;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Help()
    {
        Console.WriteLine("""
            mtgen — Music Track Generator PoC (console harness)

            The Windows desktop app (MusicTrackGen.App.exe) drives the same engine.
            This harness exists for scripted use and verification.

            COMMANDS
              generate            Generate one track
                --language        English | Hindi | Marathi | Punjabi   (default Hindi)
                --voice           Male | Female | Child                 (default Female)
                --mood            Romantic | Upbeat | Sad | Devotional | Folk
                --length          seconds, m:ss or 1m30s                (default 60)
                --seed            decimal or 0x-prefixed hex; omit for a new track
                --no-vocals       instrumental only
                --out <file>      also copy the wav here
                --root <dir>      workspace directory

              train               Add reference tracks, analyse them, rebuild style profiles
                --add <path>      .wav file or folder (repeatable)
                --language        language tag for the added files
                --mood            mood tag for the added files
                --licence <text>  provenance note recorded per file
                --no-analyse      add without analysing

              list                Show the generated-track catalogue
              regenerate          Re-render a catalogued track from its seed
                --id <id|seed>    catalogue id or seed hex (default: most recent)
                --out <file>      write the re-render here

              selftest            Verify determinism, uniqueness, length accuracy,
                                  voice separation and all four languages
                --quick           smaller sample sizes

            NOTE  The analyser reads uncompressed PCM .wav only. Convert mp3/m4a first.
            """);
        return 0;
    }
}
