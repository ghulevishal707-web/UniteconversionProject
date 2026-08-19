using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using MusicTrackGen.Core;

namespace MusicTrackGen.App;

/// <summary>
/// All four screens are driven from here. The view model owns no music logic — it calls the
/// same Core engine the console harness does, so the two cannot drift apart.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Workspace _workspace;
    private readonly StyleProfileStore _profiles;
    private readonly Catalog _catalog;
    private readonly ReferenceLibrary _library;
    private readonly TrackGenerator _generator;
    private CancellationTokenSource? _jobCancellation;

    public MainViewModel()
    {
        _workspace = new Workspace();
        _profiles = new StyleProfileStore(_workspace);
        _catalog = new Catalog(_workspace);
        _library = new ReferenceLibrary(_workspace);
        _generator = new TrackGenerator(_workspace, _profiles, _catalog);

        GenerateCommand = new AsyncRelayCommand(GenerateAsync);
        PlayCommand = new RelayCommand(Play, () => File.Exists(CurrentAudioPath));
        StopCommand = new RelayCommand(Stop);
        SaveAsCommand = new RelayCommand(SaveAs, () => File.Exists(CurrentAudioPath));
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        NewSeedCommand = new RelayCommand(() => SeedText = "");

        PlaySelectedCommand = new RelayCommand(PlaySelected, () => SelectedTrack is not null);
        RegenerateCommand = new AsyncRelayCommand(RegenerateAsync, () => SelectedTrack is not null);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => SelectedTrack is not null);

        AddSongsCommand = new RelayCommand(AddSongs);
        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveReferenceCommand = new RelayCommand(RemoveReference, () => SelectedReference is not null);
        AnalyseCommand = new AsyncRelayCommand(AnalyseAsync, () => _library.Tracks.Count > 0);
        BuildProfilesCommand = new AsyncRelayCommand(BuildProfilesAsync, () => _library.Tracks.Count > 0);
        CancelJobCommand = new RelayCommand(() => _jobCancellation?.Cancel());

        RefreshCatalog();
        RefreshLibrary();
        UpdateProfilePreview();
        VocalEngineName = _generator.VocalSynthesizer.Name;
        VocalEngineNotes = _generator.VocalSynthesizer.Capabilities.Notes;
    }

    // ---------------- choices ----------------

    public IReadOnlyList<Language> Languages { get; } = Enum.GetValues<Language>();
    public IReadOnlyList<VoiceType> VoiceTypes { get; } = Enum.GetValues<VoiceType>();
    public IReadOnlyList<string> Moods { get; } = ["Romantic", "Upbeat", "Sad", "Devotional", "Folk"];
    public IReadOnlyList<string> LengthPresets { get; } = ["0:30", "1:00", "2:00", "3:00", "Custom"];

    private Language _selectedLanguage = Language.Hindi;
    public Language SelectedLanguage
    {
        get => _selectedLanguage;
        set { if (Set(ref _selectedLanguage, value)) { OnPropertyChanged(nameof(ScriptName)); UpdateProfilePreview(); } }
    }

    private VoiceType _selectedVoice = VoiceType.Female;
    public VoiceType SelectedVoice
    {
        get => _selectedVoice;
        set => Set(ref _selectedVoice, value);
    }

    private string _selectedMood = "Romantic";
    public string SelectedMood
    {
        get => _selectedMood;
        set { if (Set(ref _selectedMood, value)) UpdateProfilePreview(); }
    }

    private string _selectedLengthPreset = "1:00";
    public string SelectedLengthPreset
    {
        get => _selectedLengthPreset;
        set { if (Set(ref _selectedLengthPreset, value)) OnPropertyChanged(nameof(IsCustomLength)); }
    }

    public bool IsCustomLength => SelectedLengthPreset == "Custom";

    private double _customLengthSeconds = 90;
    public double CustomLengthSeconds
    {
        get => _customLengthSeconds;
        set => Set(ref _customLengthSeconds, Math.Clamp(value, 15, 300));
    }

    private bool _useOwnLyrics;
    public bool UseOwnLyrics
    {
        get => _useOwnLyrics;
        set => Set(ref _useOwnLyrics, value);
    }

    private string _userLyrics = "";
    public string UserLyrics
    {
        get => _userLyrics;
        set => Set(ref _userLyrics, value);
    }

    private bool _renderVocals = true;
    public bool RenderVocals
    {
        get => _renderVocals;
        set => Set(ref _renderVocals, value);
    }

    private string _seedText = "";
    public string SeedText
    {
        get => _seedText;
        set => Set(ref _seedText, value);
    }

    public string ScriptName => Lyrics.ScriptName(SelectedLanguage);

    // ---------------- generate state ----------------

    private string _status = "Ready.";
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !IsBusy;

    private string _resultDetails = "No track generated yet.";
    public string ResultDetails
    {
        get => _resultDetails;
        private set => Set(ref _resultDetails, value);
    }

    private string _currentAudioPath = "";
    public string CurrentAudioPath
    {
        get => _currentAudioPath;
        private set
        {
            if (Set(ref _currentAudioPath, value))
            {
                (PlayCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SaveAsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private string _profilePreview = "";
    public string ProfilePreview
    {
        get => _profilePreview;
        private set => Set(ref _profilePreview, value);
    }

    // ---------------- library / catalogue ----------------

    public ObservableCollection<GeneratedTrackRecord> Tracks { get; } = [];

    private GeneratedTrackRecord? _selectedTrack;
    public GeneratedTrackRecord? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (!Set(ref _selectedTrack, value)) return;
            (PlaySelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RegenerateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DeleteSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _catalogSummary = "";
    public string CatalogSummary
    {
        get => _catalogSummary;
        private set => Set(ref _catalogSummary, value);
    }

    public ObservableCollection<ReferenceTrack> References { get; } = [];

    private ReferenceTrack? _selectedReference;
    public ReferenceTrack? SelectedReference
    {
        get => _selectedReference;
        set
        {
            if (!Set(ref _selectedReference, value)) return;
            (RemoveReferenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private Language _trainLanguage = Language.Hindi;
    public Language TrainLanguage
    {
        get => _trainLanguage;
        set => Set(ref _trainLanguage, value);
    }

    private string _trainMood = "Romantic";
    public string TrainMood
    {
        get => _trainMood;
        set => Set(ref _trainMood, value);
    }

    private string _licenceNote = "I own or have rights to this audio";
    public string LicenceNote
    {
        get => _licenceNote;
        set => Set(ref _licenceNote, value);
    }

    private string _trainLog = "";
    public string TrainLog
    {
        get => _trainLog;
        private set => Set(ref _trainLog, value);
    }

    private string _librarySummary = "";
    public string LibrarySummary
    {
        get => _librarySummary;
        private set => Set(ref _librarySummary, value);
    }

    // ---------------- settings ----------------

    public string WorkspaceRoot => _workspace.Root;
    public string GeneratedFolder => _workspace.GeneratedDirectory;
    public string VocalEngineName { get; }
    public string VocalEngineNotes { get; }
    public string SupportedLanguages => string.Join(", ", _generator.VocalSynthesizer.Capabilities.Languages);

    // ---------------- commands ----------------

    public System.Windows.Input.ICommand GenerateCommand { get; }
    public System.Windows.Input.ICommand PlayCommand { get; }
    public System.Windows.Input.ICommand StopCommand { get; }
    public System.Windows.Input.ICommand SaveAsCommand { get; }
    public System.Windows.Input.ICommand OpenOutputFolderCommand { get; }
    public System.Windows.Input.ICommand NewSeedCommand { get; }
    public System.Windows.Input.ICommand PlaySelectedCommand { get; }
    public System.Windows.Input.ICommand RegenerateCommand { get; }
    public System.Windows.Input.ICommand DeleteSelectedCommand { get; }
    public System.Windows.Input.ICommand AddSongsCommand { get; }
    public System.Windows.Input.ICommand AddFolderCommand { get; }
    public System.Windows.Input.ICommand RemoveReferenceCommand { get; }
    public System.Windows.Input.ICommand AnalyseCommand { get; }
    public System.Windows.Input.ICommand BuildProfilesCommand { get; }
    public System.Windows.Input.ICommand CancelJobCommand { get; }

    // ---------------- generate ----------------

    private TimeSpan ResolveLength() => SelectedLengthPreset switch
    {
        "0:30" => TimeSpan.FromSeconds(30),
        "1:00" => TimeSpan.FromSeconds(60),
        "2:00" => TimeSpan.FromSeconds(120),
        "3:00" => TimeSpan.FromSeconds(180),
        _ => TimeSpan.FromSeconds(CustomLengthSeconds),
    };

    private async Task GenerateAsync()
    {
        IsBusy = true;
        Progress = 0;
        Status = "Composing...";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ulong? seed = null;
            if (!string.IsNullOrWhiteSpace(SeedText))
            {
                string text = SeedText.Trim();
                seed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt64(text[2..], 16)
                    : ulong.TryParse(text, out ulong parsed) ? parsed : Rng.Hash64(text);
            }

            var request = new GenerationRequest
            {
                Language = SelectedLanguage,
                VoiceType = SelectedVoice,
                MoodTag = SelectedMood,
                TargetLength = ResolveLength(),
                RenderVocals = RenderVocals,
                UserLyrics = UseOwnLyrics && !string.IsNullOrWhiteSpace(UserLyrics) ? UserLyrics : null,
                Seed = seed,
            };

            var track = await Task.Run(() => _generator.Generate(request, (stage, fraction) =>
            {
                // Marshal progress back to the UI thread.
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    Status = stage switch
                    {
                        GenerationStage.Composing => "Composing...",
                        GenerationStage.CheckingUniqueness => "Checking this song is new...",
                        GenerationStage.RenderingInstruments => "Rendering instruments...",
                        GenerationStage.SynthesizingVocals => "Synthesising vocals...",
                        GenerationStage.Mixing => "Mixing and mastering...",
                        GenerationStage.Exporting => "Writing files...",
                        _ => Status,
                    };
                    Progress = Math.Clamp(StageBase(stage) + fraction * 0.2, 0, 1) * 100;
                });
            }));

            stopwatch.Stop();
            CurrentAudioPath = track.Record.AudioPath;
            ResultDetails = Describe(track, stopwatch.Elapsed);
            SeedText = track.Record.SeedHex;
            Status = $"Done in {stopwatch.Elapsed.TotalSeconds:0.0} s.";
            Progress = 100;
            RefreshCatalog();
        }
        catch (Exception ex)
        {
            Status = "Generation failed.";
            ResultDetails = ex.Message;
            MessageBox.Show(ex.Message, "Could not generate a track",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static double StageBase(GenerationStage stage) => stage switch
    {
        GenerationStage.Composing => 0.00,
        GenerationStage.CheckingUniqueness => 0.05,
        GenerationStage.RenderingInstruments => 0.15,
        GenerationStage.SynthesizingVocals => 0.55,
        GenerationStage.Mixing => 0.75,
        GenerationStage.Exporting => 0.92,
        _ => 0.98,
    };

    private static string Describe(GeneratedTrack track, TimeSpan elapsed)
    {
        var r = track.Record;
        double deltaMs = Math.Abs(r.ActualLengthSeconds - r.RequestedLengthSeconds) * 1000;
        return string.Join(Environment.NewLine,
            $"{r.KeyDescription} · {r.Bpm:0.0} BPM · {track.Score.TotalBars} bars",
            $"Tuning      {r.TuningDescription}",
            $"Seed        {r.SeedHex}   (tuning {r.TuningHash})",
            $"Fingerprint {r.ShortFingerprint}" + (r.SeedAttempts > 1
                ? $"   — {r.SeedAttempts} seeds tried before a new song was found"
                : ""),
            $"Profile     {r.StyleProfileId}" + (r.UsedBuiltInProfile ? "  (built-in fallback — nothing trained yet)" : ""),
            $"Progression {track.Score.ProgressionSummary()}",
            $"Form        {string.Join("  ", track.Score.Sections.Select(s => $"{s.Name}:{s.Bars}"))}",
            $"Lyrics      {track.Score.Lyrics.Count} line(s) — \"{track.Score.Lyrics.FirstOrDefault()?.Text}\"",
            $"Length      {r.ActualLengthSeconds:0.000} s (asked {r.RequestedLengthSeconds:0.#} s, off by {deltaMs:0} ms)",
            $"Level       peak {track.Audio.PeakDbfs:0.0} dBFS · RMS {track.Audio.Rms:0.000}",
            $"Rendered in {elapsed.TotalSeconds:0.0} s");
    }

    // ---------------- playback ----------------

    private readonly System.Windows.Media.MediaPlayer _player = new();

    private void Play() => PlayFile(CurrentAudioPath);

    private void PlaySelected() => PlayFile(SelectedTrack?.AudioPath ?? "");

    private void PlayFile(string path)
    {
        if (!File.Exists(path))
        {
            Status = "That audio file is missing from disk.";
            return;
        }
        _player.Open(new Uri(path));
        _player.Play();
        Status = $"Playing {Path.GetFileName(path)}";
    }

    private void Stop()
    {
        _player.Stop();
        Status = "Stopped.";
    }

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WAV audio (*.wav)|*.wav",
            FileName = Path.GetFileName(CurrentAudioPath),
        };
        if (dialog.ShowDialog() != true) return;
        File.Copy(CurrentAudioPath, dialog.FileName, overwrite: true);

        // The sidecar travels with the audio: it is what makes the track reproducible.
        string sidecar = Path.ChangeExtension(CurrentAudioPath, ".json");
        if (File.Exists(sidecar))
            File.Copy(sidecar, Path.ChangeExtension(dialog.FileName, ".json"), overwrite: true);
        Status = $"Saved to {dialog.FileName}";
    }

    private void OpenOutputFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_workspace.GeneratedDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Could not open the folder: {ex.Message}";
        }
    }

    // ---------------- catalogue ----------------

    private void RefreshCatalog()
    {
        _catalog.Load();
        Tracks.Clear();
        foreach (var record in _catalog.Records.OrderByDescending(r => r.CreatedUtc))
            Tracks.Add(record);

        var (tracks, fingerprints, tunings) = _catalog.Stats();
        CatalogSummary = $"{tracks} track(s) · {fingerprints} distinct fingerprint(s) · " +
                         $"{tunings} distinct tuning(s)" +
                         (tracks > 0 && fingerprints == tracks ? "  — all unique" : "");
    }

    private async Task RegenerateAsync()
    {
        if (SelectedTrack is null) return;
        var record = SelectedTrack;
        IsBusy = true;
        Status = $"Regenerating seed {record.SeedHex}...";

        try
        {
            string originalPath = record.AudioPath;
            var track = await Task.Run(() => _generator.RegenerateFromSeed(record));
            bool identical = FilesEqual(originalPath, track.Record.AudioPath);

            CurrentAudioPath = track.Record.AudioPath;
            ResultDetails = Describe(track, TimeSpan.Zero) + Environment.NewLine +
                            (identical
                                ? "Byte-identical to the original render — reproducibility confirmed."
                                : "WARNING: output differs from the original render.");
            Status = identical ? "Regenerated: byte-identical." : "Regenerated, but output differs.";
        }
        catch (Exception ex)
        {
            Status = $"Regenerate failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool FilesEqual(string a, string b)
    {
        if (!File.Exists(a) || !File.Exists(b)) return false;
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        var ba = new byte[65536];
        var bb = new byte[65536];
        int read;
        while ((read = sa.Read(ba)) > 0)
        {
            if (sb.Read(bb, 0, read) != read) return false;
            if (!ba.AsSpan(0, read).SequenceEqual(bb.AsSpan(0, read))) return false;
        }
        return true;
    }

    private void DeleteSelected()
    {
        if (SelectedTrack is null) return;
        var record = SelectedTrack;
        if (MessageBox.Show($"Delete {Path.GetFileName(record.AudioPath)} and its catalogue entry?",
                "Delete track", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        foreach (string path in new[] { record.AudioPath, record.SidecarPath })
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* keep going */ }

        _catalog.Remove(record.Id);
        RefreshCatalog();
        Status = "Track deleted.";
    }

    // ---------------- train ----------------

    private void AddSongs()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add reference tracks (uncompressed PCM .wav)",
            Filter = "WAV audio (*.wav)|*.wav",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        AddReferences(dialog.FileNames);
    }

    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add every .wav in a folder" };
        if (dialog.ShowDialog() != true) return;
        AddReferences(Directory.EnumerateFiles(dialog.FolderName, "*.wav", SearchOption.AllDirectories));
    }

    private void AddReferences(IEnumerable<string> paths)
    {
        var added = _library.Add(paths, TrainLanguage, TrainMood, LicenceNote);
        RefreshLibrary();
        AppendLog($"Added {added.Count} track(s) as {TrainLanguage}/{TrainMood}.");
        if (added.Count == 0)
            AppendLog("  (nothing new — those files are already in the library)");
        (AnalyseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (BuildProfilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RemoveReference()
    {
        if (SelectedReference is null) return;
        _library.Remove(SelectedReference.Id);
        RefreshLibrary();
        AppendLog("Removed one reference track.");
    }

    private async Task AnalyseAsync()
    {
        IsBusy = true;
        Progress = 0;
        _jobCancellation = new CancellationTokenSource();
        var token = _jobCancellation.Token;
        AppendLog("Analysing reference tracks...");

        try
        {
            int done = await Task.Run(() => _library.AnalysePending((track, index, total) =>
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    Progress = index / (double)Math.Max(1, total) * 100;
                    Status = $"Analysing {index}/{total}: {track.Title}";
                    AppendLog(track.State == ReferenceState.Analysed
                        ? $"  {track.Title}: {track.Features!.Summary()}"
                        : $"  {track.Title}: FAILED — {track.Error}");
                }), token), token);

            AppendLog($"Analysed {done} track(s).");
            Status = $"Analysis complete ({done} track(s)).";
        }
        catch (OperationCanceledException)
        {
            AppendLog("Analysis cancelled.");
            Status = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Analysis error: {ex.Message}");
        }
        finally
        {
            RefreshLibrary();
            IsBusy = false;
            _jobCancellation = null;
        }
    }

    private async Task BuildProfilesAsync()
    {
        IsBusy = true;
        AppendLog("Building style profiles...");
        try
        {
            var built = await Task.Run(() => _profiles.RebuildAll(_library,
                message => Application.Current?.Dispatcher.Invoke(() => AppendLog("  " + message))));

            if (built.Count == 0)
                AppendLog("  No profiles built — analyse some tracks first.");
            foreach (var profile in built)
                AppendLog(Environment.NewLine + profile.Describe());

            Status = $"Built {built.Count} style profile(s).";
            UpdateProfilePreview();
        }
        catch (Exception ex)
        {
            AppendLog($"Profile build error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshLibrary()
    {
        References.Clear();
        foreach (var track in _library.Tracks.OrderBy(t => t.Title)) References.Add(track);

        int analysed = _library.Tracks.Count(t => t.State == ReferenceState.Analysed);
        int failed = _library.Tracks.Count(t => t.State == ReferenceState.Failed);
        double totalMinutes = _library.Tracks.Sum(t => t.DurationSeconds) / 60.0;
        LibrarySummary = $"{_library.Tracks.Count} track(s) · {analysed} analysed" +
                         (failed > 0 ? $" · {failed} failed" : "") +
                         $" · {totalMinutes:0.0} min total";
    }

    private void UpdateProfilePreview()
    {
        var profile = _profiles.Get(SelectedLanguage, SelectedMood);
        ProfilePreview = profile.Describe();
    }

    private void AppendLog(string message)
    {
        string stamp = DateTime.Now.ToString("HH:mm:ss");
        TrainLog = TrainLog.Length == 0
            ? $"[{stamp}] {message}"
            : $"{TrainLog}{Environment.NewLine}[{stamp}] {message}";
    }

    // ---------------- INotifyPropertyChanged ----------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
