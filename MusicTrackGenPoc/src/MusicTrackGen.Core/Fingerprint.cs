using System.Security.Cryptography;
using System.Text;

namespace MusicTrackGen.Core;

/// <summary>
/// Content fingerprint used to guarantee that no two generated tracks are the same song
/// (requirement R5).
///
/// Deliberately transposition-invariant: it hashes melodic *intervals*, not absolute
/// pitches, so the same tune in a different key is correctly recognised as the same song
/// rather than passing as new. Tempo and tuning are likewise excluded — those are
/// performance parameters, not composition.
/// </summary>
public static class Fingerprint
{
    public static string Compute(SongScore score)
    {
        var sb = new StringBuilder();

        // 1. Melodic contour as intervals, from the vocal part if present, else the lead.
        var melody = score.Parts.FirstOrDefault(p => p.Kind == PartKind.Vocal)
                     ?? score.Parts.FirstOrDefault(p => p.Kind == PartKind.Lead);
        if (melody is not null)
        {
            var notes = melody.Notes.OrderBy(n => n.StartBeat).ToList();
            sb.Append("M:");
            for (int i = 1; i < notes.Count; i++)
                sb.Append(notes[i].Midi - notes[i - 1].Midi).Append(',');

            // Rhythm of the melody, quantised to a 1/16 grid.
            sb.Append("|R:");
            foreach (var n in notes)
                sb.Append((int)Math.Round(n.LengthBeats * 4)).Append(',');
        }

        // 2. Harmonic motion as scale degrees (already key-relative).
        sb.Append("|H:").AppendJoin(',', score.ChordPerBar);

        // 3. Percussion onset bitmap per instrument, on a 1/16 grid.
        sb.Append("|D:");
        foreach (var group in score.Drums.GroupBy(d => d.Kind).OrderBy(g => g.Key))
        {
            sb.Append((int)group.Key).Append(':');
            var grid = group.Select(d => (int)Math.Round(d.StartBeat * 4)).Distinct().OrderBy(x => x);
            sb.AppendJoin('.', grid).Append(';');
        }

        // 4. Arrangement shape.
        sb.Append("|S:");
        foreach (var section in score.Sections)
            sb.Append(section.Name).Append(section.Bars).Append('.');

        // 5. Lyric identity — same tune with different words is a different track.
        sb.Append("|L:");
        foreach (var line in score.Lyrics) sb.Append(line.Text).Append('/');

        sb.Append("|K:").Append(score.Scale.Name);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static string Short(string fingerprint) =>
        fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];
}
