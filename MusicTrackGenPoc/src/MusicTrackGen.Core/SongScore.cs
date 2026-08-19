namespace MusicTrackGen.Core;

public enum Language { English, Hindi, Marathi, Punjabi }

public enum VoiceType { Male, Female, Child }

public enum PartKind { Lead, Pad, Strings, Pluck, Bass, Keys, Vocal }

public enum DrumKind { Kick, Snare, HatClosed, HatOpen, TablaNa, TablaGe, Dholak }

/// <summary>One note in the symbolic score. Times are in beats from song start.</summary>
public sealed class NoteEvent
{
    public double StartBeat { get; init; }
    public double LengthBeats { get; init; }
    public int Midi { get; init; }
    public double Velocity { get; init; } = 0.8;
    /// <summary>Lyric syllable carried by this note, for vocal parts.</summary>
    public Syllable? Syllable { get; init; }
}

public sealed class DrumHit
{
    public double StartBeat { get; init; }
    public DrumKind Kind { get; init; }
    public double Velocity { get; init; } = 0.8;
}

public sealed class Part
{
    public PartKind Kind { get; init; }
    public int Index { get; init; }
    public double Gain { get; init; } = 0.8;
    public double Pan { get; init; }
    public List<NoteEvent> Notes { get; } = [];
}

public sealed class SectionPlan
{
    public required string Name { get; init; }
    public int StartBar { get; init; }
    public int Bars { get; init; }
    public bool HasVocal { get; init; }
    public double Intensity { get; init; } = 1.0;
    public int EndBar => StartBar + Bars;
}

/// <summary>The complete symbolic song, before any audio is rendered.</summary>
public sealed class SongScore
{
    public required ScaleDef Scale { get; init; }
    public int TonicMidi { get; init; }
    public double Bpm { get; init; }
    public int BeatsPerBar { get; init; } = 4;
    public required TuningProfile Tuning { get; init; }
    public List<SectionPlan> Sections { get; } = [];
    public List<Part> Parts { get; } = [];
    public List<DrumHit> Drums { get; } = [];
    /// <summary>Chord scale-degree per bar.</summary>
    public List<int> ChordPerBar { get; } = [];
    public List<LyricLine> Lyrics { get; } = [];

    public int TotalBars => Sections.Count == 0 ? 0 : Sections[^1].EndBar;
    public double TotalBeats => TotalBars * BeatsPerBar;
    public double SecondsPerBeat => 60.0 / Bpm;
    public double DurationSeconds => TotalBeats * SecondsPerBeat;

    public string KeyDescription => $"{Scales.PitchClassNames[((TonicMidi % 12) + 12) % 12]} {Scale.Name}";

    public string ProgressionSummary()
    {
        var labels = ChordPerBar.Take(8).Select(d => Chords.Label(Scale, d));
        return string.Join(" – ", labels);
    }
}

/// <summary>What the user asked for on the Generate screen.</summary>
public sealed class GenerationRequest
{
    public Language Language { get; init; } = Language.Hindi;
    public VoiceType VoiceType { get; init; } = VoiceType.Female;
    public TimeSpan TargetLength { get; init; } = TimeSpan.FromSeconds(60);
    public string MoodTag { get; init; } = "Romantic";
    public string? StyleProfileId { get; init; }
    public string? UserLyrics { get; init; }
    public ulong? Seed { get; init; }
    public bool RenderVocals { get; init; } = true;
    public int SampleRate { get; init; } = 44100;
}

/// <summary>Progress stages reported back to the UI while generating.</summary>
public enum GenerationStage
{
    Composing,
    Arranging,
    WritingLyrics,
    RenderingInstruments,
    SynthesizingVocals,
    Mixing,
    CheckingUniqueness,
    Exporting,
    Done,
}
