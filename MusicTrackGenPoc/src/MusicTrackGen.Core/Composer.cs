namespace MusicTrackGen.Core;

/// <summary>
/// Turns a style profile plus a seed into a complete symbolic score. Deterministic:
/// the same (profile, request, seed) always yields the identical SongScore.
/// </summary>
public static class Composer
{
    private const int BeatsPerBar = 4;

    public static SongScore Compose(StyleProfile profile, GenerationRequest request, ulong seed)
    {
        var rng = new Rng(seed);

        // ---- global parameters sampled from the learned distributions ----
        var scale = PickScale(profile, rng);
        int tonicMidi = 48 + rng.Next(0, 12);          // C3..B3 as the track tonic
        double bpm = Math.Clamp(rng.Gaussian(profile.TempoMean, profile.TempoSd),
                                profile.TempoMin, profile.TempoMax);

        // ---- length fitting (requirement R10) ----
        double target = request.TargetLength.TotalSeconds;
        (int bars, bpm) = FitBars(target, bpm, profile);

        var tuning = TuningProfile.Sample(rng.Fork("tuning"), tonicMidi, allowShruti: scale.IsRaga);

        var score = new SongScore
        {
            Scale = scale,
            TonicMidi = tonicMidi,
            Bpm = bpm,
            BeatsPerBar = BeatsPerBar,
            Tuning = tuning,
        };

        foreach (var section in PlanSections(bars, profile, rng.Fork("form")))
            score.Sections.Add(section);

        BuildHarmony(score, profile, rng.Fork("harmony"));
        BuildLyrics(score, request, rng.Fork("lyrics"));
        BuildVocalAndLead(score, profile, request, rng.Fork("melody"));
        BuildAccompaniment(score, profile, rng.Fork("accomp"));
        BuildDrums(score, profile, rng.Fork("drums"));

        return score;
    }

    private static ScaleDef PickScale(StyleProfile profile, Rng rng)
    {
        if (profile.ModeHistogram.Count == 0) return Scales.Major;
        var names = profile.ModeHistogram.Keys.ToList();
        var weights = names.Select(n => profile.ModeHistogram[n]).ToList();
        return Scales.ByName(names[rng.PickWeighted(weights)]);
    }

    /// <summary>
    /// Picks a whole number of bars for the requested duration, then nudges the tempo
    /// so those bars land exactly on the target length. Keeps the music bar-aligned
    /// and the file length exact instead of trading one for the other.
    /// </summary>
    private static (int Bars, double Bpm) FitBars(double targetSeconds, double bpm, StyleProfile profile)
    {
        double lo = Math.Max(40, profile.TempoMin * 0.88);
        double hi = Math.Min(200, profile.TempoMax * 1.12);

        double barSeconds = 60.0 / bpm * BeatsPerBar;
        int bars = Math.Max(4, (int)Math.Round(targetSeconds / barSeconds));

        // Exact fit: bars * BeatsPerBar beats spread over exactly targetSeconds.
        double Exact(int b) => b * BeatsPerBar * 60.0 / targetSeconds;

        // If that tempo strays outside the style's plausible range, try neighbouring
        // bar counts until one lands inside it.
        for (int delta = 0; delta <= 6; delta++)
        {
            foreach (int candidate in delta == 0 ? new[] { bars } : [bars - delta, bars + delta])
            {
                if (candidate < 4) continue;
                double t = Exact(candidate);
                if (t >= lo && t <= hi) return (candidate, t);
            }
        }
        return (bars, Math.Clamp(Exact(bars), lo, hi));
    }

    /// <summary>
    /// Allocates the bar budget across a song form. Residual bars land on intro/outro
    /// and the bridge first, so a chorus is never cut mid-phrase.
    /// </summary>
    private static List<SectionPlan> PlanSections(int bars, StyleProfile profile, Rng rng)
    {
        // (name, hasVocal, intensity, absorbsResidual)
        List<(string Name, bool Vocal, double Intensity, bool Flexible)> slots;
        double[] fractions;

        // Thresholds are in bars, not seconds: a section under about 4 bars reads as a
        // transition rather than a section, so short tracks get fewer, longer sections.
        if (bars < 20)
        {
            slots = [("Intro", false, 0.5, true), ("Verse", true, 0.8, false),
                     ("Chorus", true, 1.0, false), ("Outro", false, 0.5, true)];
            fractions = [0.15, 0.35, 0.35, 0.15];
        }
        else if (bars < 40)
        {
            slots = [("Intro", false, 0.5, true), ("Verse", true, 0.8, false),
                     ("Chorus", true, 1.0, false), ("Verse", true, 0.85, false),
                     ("Chorus", true, 1.0, false), ("Outro", false, 0.45, true)];
            fractions = Normalise(profile.FormTemplate.Length == 6
                ? profile.FormTemplate
                : [0.10, 0.22, 0.25, 0.20, 0.17, 0.06]);
        }
        else
        {
            slots = [("Intro", false, 0.5, true), ("Verse", true, 0.8, false),
                     ("Chorus", true, 1.0, false), ("Verse", true, 0.85, false),
                     ("Chorus", true, 1.0, false), ("Bridge", false, 0.7, true),
                     ("Chorus", true, 1.05, false), ("Outro", false, 0.45, true)];
            fractions = Normalise([0.08, 0.17, 0.18, 0.15, 0.16, 0.08, 0.13, 0.05]);
        }

        int n = slots.Count;
        var alloc = new int[n];
        // Floors scale with the track: on anything but a very short track a verse or
        // chorus gets at least 4 bars so its phrases have room to breathe.
        int vocalFloor = bars >= 16 ? 4 : 2;
        int instrumentalFloor = bars >= 16 ? 2 : 1;
        for (int i = 0; i < n; i++)
        {
            // Vocal sections prefer even bar counts so 2-bar phrases stay whole.
            double raw = fractions[i] * bars;
            alloc[i] = slots[i].Vocal
                ? Math.Max(vocalFloor, 2 * (int)Math.Round(raw / 2.0))
                : Math.Max(instrumentalFloor, (int)Math.Round(raw));
        }

        // Reconcile the total by moving bars on and off the flexible sections.
        var flexible = Enumerable.Range(0, n).Where(i => slots[i].Flexible).ToList();
        var vocal = Enumerable.Range(0, n).Where(i => slots[i].Vocal).ToList();
        int guard = 0;
        while (alloc.Sum() != bars && guard++ < 4000)
        {
            int diff = bars - alloc.Sum();
            if (diff > 0)
            {
                int i = flexible[rng.Next(0, flexible.Count)];
                alloc[i]++;
            }
            else
            {
                var shrinkable = flexible.Where(i => alloc[i] > instrumentalFloor).ToList();
                if (shrinkable.Count > 0) alloc[shrinkable[rng.Next(0, shrinkable.Count)]]--;
                else
                {
                    var v = vocal.Where(i => alloc[i] > vocalFloor).ToList();
                    if (v.Count == 0) break;
                    alloc[v[rng.Next(0, v.Count)]] -= 2;
                }
            }
        }

        // Absorb any leftover (only reachable when every section is at its floor).
        int drift = bars - alloc.Sum();
        if (drift != 0) alloc[^1] = Math.Max(1, alloc[^1] + drift);

        var plan = new List<SectionPlan>();
        int cursor = 0;
        for (int i = 0; i < n; i++)
        {
            if (alloc[i] <= 0) continue;
            plan.Add(new SectionPlan
            {
                Name = slots[i].Name,
                StartBar = cursor,
                Bars = alloc[i],
                HasVocal = slots[i].Vocal,
                Intensity = slots[i].Intensity,
            });
            cursor += alloc[i];
        }
        return plan;
    }

    private static double[] Normalise(double[] v)
    {
        double sum = v.Sum();
        return sum <= 0 ? v : v.Select(x => x / sum).ToArray();
    }

    // ---------------- harmony ----------------

    private static void BuildHarmony(SongScore score, StyleProfile profile, Rng rng)
    {
        var matrix = profile.DegreeTransitions.Length == 7
            ? profile.DegreeTransitions
            : StyleProfile.DefaultTransitions();
        int degrees = Math.Min(7, score.Scale.Length);

        // Harmonic rhythm: how many bars one chord is held for. Changing chord on every
        // single bar makes even a good progression sound restless, so most tracks hold
        // each chord for two bars.
        int chordBars = rng.Chance(0.62) ? 2 : 1;

        int current = 0;
        int repeats = 0;
        foreach (var section in score.Sections)
        {
            int hold = section.Bars >= 4 ? chordBars : 1;
            int bar = 0;
            while (bar < section.Bars)
            {
                int barsLeft = section.Bars - bar;
                int span = Math.Min(hold, barsLeft);
                bool closingSlot = barsLeft <= hold;
                int next;

                if (closingSlot && section.Name is "Chorus" or "Outro")
                    next = 0;                                     // resolve to tonic
                else if (closingSlot && section.Bars >= 4)
                    next = rng.Chance(0.6) ? 4 % degrees : 0;     // half-close on V
                else
                {
                    var row = matrix[Math.Min(current, matrix.Length - 1)]
                        .Take(degrees).Select(x => Math.Max(1e-4, x)).ToList();
                    row[current] = 0;                             // always move somewhere new
                    if (repeats >= 1 && degrees > 2)
                    {
                        // Discourage immediate oscillation back to where we just were,
                        // which is what produced I-V-I-V loops.
                        int previous = score.ChordPerBar.Count >= span + 1
                            ? score.ChordPerBar[^(span + 1)]
                            : -1;
                        if (previous >= 0 && previous < row.Count) row[previous] *= 0.25;
                    }
                    next = rng.PickWeighted(row);
                }

                repeats = next == current ? repeats + 1 : 0;
                current = next;
                for (int i = 0; i < span; i++) score.ChordPerBar.Add(next);
                bar += span;
            }
        }
    }

    // ---------------- lyrics ----------------

    private static void BuildLyrics(SongScore score, GenerationRequest request, Rng rng)
    {
        int vocalPhrases = score.Sections.Where(s => s.HasVocal).Sum(s => Math.Max(1, s.Bars / 2));

        List<LyricLine> lines;
        if (!string.IsNullOrWhiteSpace(request.UserLyrics))
        {
            lines = Lyrics.FromUserText(request.UserLyrics!, request.Language);
            if (lines.Count == 0)
                lines = Lyrics.Assemble(request.Language, rng, vocalPhrases, 0);
        }
        else
        {
            int hooks = score.Sections.Count(s => s.Name == "Chorus") * 2;
            lines = Lyrics.Assemble(request.Language, rng, Math.Max(2, vocalPhrases), Math.Max(1, hooks));
        }
        score.Lyrics.AddRange(lines);
    }

    // ---------------- melody ----------------

    /// <summary>A reusable melodic idea: relative scale degrees and note lengths.</summary>
    private sealed record Motif(int[] Degrees, double[] Lengths);

    private static Motif MakeMotif(Rng rng, int phraseBeats)
    {
        double[][] rhythms =
        [
            [1, 1, 2], [2, 1, 1], [1, 0.5, 0.5, 2], [1.5, 0.5, 2],
            [0.5, 0.5, 1, 1, 1], [2, 2], [1, 1, 1, 1],
        ];

        var lengths = new List<double>();
        while (lengths.Sum() < phraseBeats)
        {
            var cell = rhythms[rng.Next(0, rhythms.Length)];
            double remaining = phraseBeats - lengths.Sum();
            if (cell.Sum() > remaining) { lengths.Add(remaining); break; }
            lengths.AddRange(cell);
        }

        // Contour: a shape the ear can follow, not a random walk.
        int[][] contours =
        [
            [0, 2, 4, 2], [4, 2, 1, 0], [0, 1, 2, 4], [0, 3, 1, 2], [2, 4, 2, 0],
        ];
        var contour = contours[rng.Next(0, contours.Length)];

        var degrees = new int[lengths.Count];
        for (int i = 0; i < degrees.Length; i++)
        {
            int c = contour[i * contour.Length / Math.Max(1, degrees.Length)];
            degrees[i] = c + (rng.Chance(0.25) ? (rng.Chance(0.5) ? 1 : -1) : 0);
        }
        return new Motif(degrees, lengths.ToArray());
    }

    /// <summary>Motif variation — the operations that make a melody sound intentional.</summary>
    private static Motif Vary(Motif motif, Rng rng)
    {
        int op = rng.PickWeighted([0.30, 0.22, 0.18, 0.16, 0.14]);
        return op switch
        {
            0 => motif with { Degrees = motif.Degrees.Select(d => d + rng.Next(-1, 3)).ToArray() },
            1 => motif with { Degrees = motif.Degrees.Select(d => 4 - d).ToArray() },              // inversion
            2 => motif with { Lengths = motif.Lengths.Select(l => Math.Max(0.25, l / 2)).ToArray() },
            3 => motif with { Lengths = motif.Lengths.Select(l => l * 2).ToArray() },
            _ => motif with { Degrees = motif.Degrees.Reverse().ToArray() },
        };
    }

    /// <summary>Stretches or squeezes a rhythm so it has exactly noteCount notes filling the phrase.</summary>
    private static double[] FitNoteCount(double[] lengths, int noteCount, double phraseBeats, Rng rng)
    {
        var result = new List<double>(lengths);
        while (result.Count < noteCount)
        {
            int i = result.IndexOf(result.Max());
            double half = result[i] / 2;
            result[i] = half;
            result.Insert(i + 1, half);
        }
        while (result.Count > noteCount)
        {
            int i = result.IndexOf(result.Min());
            int j = i == 0 ? 1 : i - 1;
            if (j >= result.Count) break;
            result[j] += result[i];
            result.RemoveAt(i);
        }
        double sum = result.Sum();
        if (sum <= 0) return Enumerable.Repeat(phraseBeats / Math.Max(1, noteCount), Math.Max(1, noteCount)).ToArray();
        return result.Select(l => l * phraseBeats / sum).ToArray();
    }

    private static void BuildVocalAndLead(SongScore score, StyleProfile profile, GenerationRequest request, Rng rng)
    {
        var vocalPart = new Part { Kind = PartKind.Vocal, Index = 0, Gain = 1.0, Pan = 0 };
        var leadPart = new Part { Kind = PartKind.Lead, Index = 1, Gain = 0.55, Pan = -0.12 };

        var motif = MakeMotif(rng, score.BeatsPerBar * 2);
        int lyricCursor = 0;
        var hooks = score.Lyrics.Where(l => l.Role == "hook").ToList();
        var verses = score.Lyrics.Where(l => l.Role != "hook").ToList();

        // Vocals sit in a comfortable register for the chosen voice type.
        int vocalCentre = request.VoiceType switch
        {
            VoiceType.Male => score.TonicMidi + 12,
            VoiceType.Female => score.TonicMidi + 19,
            _ => score.TonicMidi + 24,
        };

        foreach (var section in score.Sections)
        {
            int phraseLen = 2;                                   // 2-bar phrases
            for (int bar = section.StartBar; bar < section.EndBar; bar += phraseLen)
            {
                int barsHere = Math.Min(phraseLen, section.EndBar - bar);
                double phraseBeats = barsHere * score.BeatsPerBar;
                double startBeat = bar * score.BeatsPerBar;
                int chordDegree = score.ChordPerBar[Math.Min(bar, score.ChordPerBar.Count - 1)];

                var shape = rng.Chance(0.65) ? Vary(motif, rng) : motif;

                if (section.HasVocal && request.RenderVocals)
                {
                    var pool = section.Name == "Chorus" && hooks.Count > 0 ? hooks : verses;
                    if (pool.Count == 0) pool = score.Lyrics;
                    if (pool.Count == 0) continue;

                    var line = pool[lyricCursor++ % pool.Count];
                    int noteCount = Math.Max(1, line.Syllables.Count);
                    var lengths = FitNoteCount(shape.Lengths, noteCount, phraseBeats, rng);

                    double t = startBeat;
                    for (int i = 0; i < noteCount; i++)
                    {
                        int degree = shape.Degrees[i % shape.Degrees.Length] + chordDegree;
                        int midi = NearestInRegister(score.Scale, score.TonicMidi, degree, vocalCentre);
                        vocalPart.Notes.Add(new NoteEvent
                        {
                            StartBeat = t,
                            LengthBeats = lengths[i] * 0.94,
                            Midi = midi,
                            Velocity = 0.72 + 0.18 * section.Intensity,
                            Syllable = line.Syllables[i],
                        });
                        t += lengths[i];
                    }
                }
                else
                {
                    // Instrumental phrase: the lead instrument carries the motif.
                    double t = startBeat;
                    for (int i = 0; i < shape.Lengths.Length && t < startBeat + phraseBeats; i++)
                    {
                        int degree = shape.Degrees[i % shape.Degrees.Length] + chordDegree;
                        int midi = NearestInRegister(score.Scale, score.TonicMidi, degree, vocalCentre - 2);
                        double len = Math.Min(shape.Lengths[i], startBeat + phraseBeats - t);
                        leadPart.Notes.Add(new NoteEvent
                        {
                            StartBeat = t,
                            LengthBeats = len * 0.92,
                            Midi = midi,
                            Velocity = 0.6 + 0.2 * section.Intensity,
                        });
                        t += shape.Lengths[i];
                    }
                }

                // Short instrumental answer at the end of a sung phrase.
                if (section.HasVocal && rng.Chance(0.35))
                {
                    double t = startBeat + phraseBeats - 1.0;
                    int degree = shape.Degrees[^1] + chordDegree;
                    leadPart.Notes.Add(new NoteEvent
                    {
                        StartBeat = t,
                        LengthBeats = 0.9,
                        Midi = NearestInRegister(score.Scale, score.TonicMidi, degree, vocalCentre + 3),
                        Velocity = 0.45,
                    });
                }
            }
        }

        if (vocalPart.Notes.Count > 0) score.Parts.Add(vocalPart);
        if (leadPart.Notes.Count > 0) score.Parts.Add(leadPart);
    }

    /// <summary>Maps a scale degree into the octave nearest a target register.</summary>
    private static int NearestInRegister(ScaleDef scale, int tonicMidi, int degree, int centre)
    {
        int midi = scale.NoteAt(tonicMidi, degree);
        while (midi < centre - 6) midi += 12;
        while (midi > centre + 8) midi -= 12;
        return Math.Clamp(midi, 36, 96);
    }

    // ---------------- accompaniment ----------------

    private static void BuildAccompaniment(SongScore score, StyleProfile profile, Rng rng)
    {
        var pad = new Part { Kind = PartKind.Pad, Index = 2, Gain = 0.38, Pan = 0.18 };
        var strings = new Part { Kind = PartKind.Strings, Index = 3, Gain = 0.32, Pan = -0.22 };
        var pluck = new Part { Kind = PartKind.Pluck, Index = 4, Gain = 0.34, Pan = 0.28 };
        var bass = new Part { Kind = PartKind.Bass, Index = 5, Gain = 0.5, Pan = 0 };
        var keys = new Part { Kind = PartKind.Keys, Index = 6, Gain = 0.26, Pan = -0.3 };

        double PickWeight(string name) =>
            profile.InstrumentWeights.TryGetValue(name, out double w) ? w : 0.2;

        bool usePluck = PickWeight("Pluck") > 0.12;
        bool useKeys = PickWeight("Keys") > 0.12;
        bool useStrings = PickWeight("Strings") > 0.12;

        foreach (var section in score.Sections)
        {
            for (int bar = section.StartBar; bar < section.EndBar; bar++)
            {
                int degree = score.ChordPerBar[Math.Min(bar, score.ChordPerBar.Count - 1)];
                int[] triad = Chords.Triad(score.Scale, score.TonicMidi, degree);
                double barStart = bar * score.BeatsPerBar;

                // Pad: one sustained chord per bar, voiced mid-range.
                foreach (int note in triad)
                    pad.Notes.Add(new NoteEvent
                    {
                        StartBeat = barStart,
                        LengthBeats = score.BeatsPerBar,
                        Midi = Voice(note, 60),
                        Velocity = 0.34 * section.Intensity,
                    });

                // Strings: enter from the first chorus onward for a sense of build.
                if (useStrings && section.Intensity >= 0.8)
                    foreach (int note in triad)
                        strings.Notes.Add(new NoteEvent
                        {
                            StartBeat = barStart,
                            LengthBeats = score.BeatsPerBar,
                            Midi = Voice(note, 72),
                            Velocity = 0.28 * section.Intensity,
                        });

                // Bass: root, with a passing tone into the next bar.
                bass.Notes.Add(new NoteEvent
                {
                    StartBeat = barStart,
                    LengthBeats = rng.Chance(0.5) ? 1.5 : 2.0,
                    Midi = Voice(triad[0], 40),
                    Velocity = 0.8,
                });
                bass.Notes.Add(new NoteEvent
                {
                    StartBeat = barStart + 2,
                    LengthBeats = 1.5,
                    Midi = Voice(rng.Chance(0.3) ? triad[2] : triad[0], 40),
                    Velocity = 0.7,
                });

                // Pluck: arpeggio through the chord.
                if (usePluck && section.Intensity >= 0.5)
                {
                    int steps = section.Intensity >= 1.0 ? 8 : 4;
                    for (int s = 0; s < steps; s++)
                    {
                        int note = triad[s % triad.Length];
                        pluck.Notes.Add(new NoteEvent
                        {
                            StartBeat = barStart + s * (score.BeatsPerBar / (double)steps),
                            LengthBeats = score.BeatsPerBar / (double)steps * 0.9,
                            Midi = Voice(note, 67) + (s >= triad.Length ? 12 : 0),
                            Velocity = (s % 2 == 0 ? 0.5 : 0.34) * section.Intensity,
                        });
                    }
                }

                // Keys: off-beat stabs.
                if (useKeys && section.Intensity >= 0.8)
                    for (int beat = 1; beat < score.BeatsPerBar; beat += 2)
                        foreach (int note in triad)
                            keys.Notes.Add(new NoteEvent
                            {
                                StartBeat = barStart + beat + 0.5,
                                LengthBeats = 0.45,
                                Midi = Voice(note, 64),
                                Velocity = 0.3 * section.Intensity,
                            });
            }
        }

        foreach (var part in new[] { pad, strings, pluck, bass, keys })
            if (part.Notes.Count > 0) score.Parts.Add(part);
    }

    private static int Voice(int midi, int centre)
    {
        while (midi < centre - 6) midi += 12;
        while (midi > centre + 6) midi -= 12;
        return Math.Clamp(midi, 28, 100);
    }

    // ---------------- percussion ----------------

    private static void BuildDrums(SongScore score, StyleProfile profile, Rng rng)
    {
        var roles = new (string Key, DrumKind Kind, double Gain)[]
        {
            ("kick", DrumKind.Kick, 1.0),
            ("snare", DrumKind.Snare, 0.85),
            ("hat", DrumKind.HatClosed, 0.5),
            ("tabla", DrumKind.TablaNa, 0.6),
            ("dholak", DrumKind.Dholak, 0.55),
        };

        foreach (var section in score.Sections)
        {
            if (section.Name == "Intro" && section.Bars <= 2) continue;   // let the intro breathe

            for (int bar = section.StartBar; bar < section.EndBar; bar++)
            {
                double barStart = bar * score.BeatsPerBar;
                foreach (var (key, kind, gain) in roles)
                {
                    if (!profile.Rhythm.TryGetValue(key, out double[]? grid) || grid.Length == 0) continue;
                    for (int step = 0; step < 16; step++)
                    {
                        double p = grid[step % grid.Length] * Math.Clamp(section.Intensity, 0.35, 1.15);
                        if (!rng.Chance(p)) continue;
                        score.Drums.Add(new DrumHit
                        {
                            StartBeat = barStart + step * (score.BeatsPerBar / 16.0),
                            Kind = kind == DrumKind.TablaNa && rng.Chance(0.3) ? DrumKind.TablaGe : kind,
                            Velocity = gain * rng.Range(0.7, 1.0) * Math.Clamp(section.Intensity, 0.4, 1.1),
                        });
                    }
                }

                // Fill on the last bar of a section.
                if (bar == section.EndBar - 1 && section.Bars > 2 && rng.Chance(0.55))
                    for (int i = 0; i < 4; i++)
                        score.Drums.Add(new DrumHit
                        {
                            StartBeat = barStart + 3 + i * 0.25,
                            Kind = DrumKind.Snare,
                            Velocity = 0.4 + i * 0.14,
                        });
            }
        }

        score.Drums.Sort((a, b) => a.StartBeat.CompareTo(b.StartBeat));
    }
}
