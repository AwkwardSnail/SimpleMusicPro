using SimpleMusicPro.Engraving;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Editing;

/// <summary>A place where something can be added: a staff, a bar (its index) and a place in the bar, in sixteenths.</summary>
public sealed record Placeholder(LaneKind Lane, int Bar, int Position);

/// <summary>
/// Where the "+" signs go, that add a strum with a click: only at the moment of the note (or rest) selected in
/// the voice staff, unless a strum starts there. A strum added inside one that sounds on shortens that one.
/// </summary>
public static class Placeholders
{
    /// <param name="selected">The note selected in the voice staff (its bar and place), or null for none.</param>
    public static IReadOnlyList<Placeholder> ForRhythm(ScoreDocument score, (int Bar, int Position)? selected)
    {
        var lane = score.Rhythm;
        if (lane is null || selected is not { } s || s.Bar < 0 || s.Bar >= score.Bars.Count) return [];
        int position = s.Position;   // the sixteenth-note grid: any note's moment
        // Not where a strum starts (that strum is clicked to change it); inside one, a new strum shortens it.
        if (position < 0 || position >= score.Bars[s.Bar].Length || lane.At(s.Bar, position)?.Position == position) return [];
        return [new Placeholder(LaneKind.Rhythm, s.Bar, position)];
    }

    /// <summary>
    /// The "+" of the chord row: at the very moment of the note or strum selected, unless a chord is there already
    /// or stands closer than a quarter note to it (also across a bar line).
    /// </summary>
    /// <param name="selected">The note or strum selected (its bar and place), or null for none.</param>
    public static IReadOnlyList<Placeholder> ForChords(ScoreDocument score, (int Bar, int Position)? selected)
    {
        var lane = score.Chords;
        if (lane is null || selected is not { } s || s.Bar < 0 || s.Bar >= score.Bars.Count) return [];
        if (s.Position < 0 || s.Position >= score.Bars[s.Bar].Length || lane.At(s.Bar, s.Position) is not null || score.ChordTooClose(s.Bar, s.Position)) return [];
        return [new Placeholder(LaneKind.Chords, s.Bar, s.Position)];
    }

    /// <summary>The "+" of the words' line: under the note selected, when a syllable can be sung on it and none is there yet.</summary>
    /// <param name="selected">The note selected in the voice staff, or null for none.</param>
    public static IReadOnlyList<Placeholder> ForLyrics(ScoreDocument score, (int Bar, int Position)? selected)
    {
        if (score.Lyrics is null || selected is not { } s || !SungNotes(score).Contains((s.Bar, s.Position)) || score.Lyrics.At(s.Bar, s.Position) is not null) return [];
        return [new Placeholder(LaneKind.Lyrics, s.Bar, s.Position)];
    }

    /// <summary>The notes a syllable can be sung on, in order: every note of the voice staff but rests and the notes a tie carries on.</summary>
    public static List<(int Bar, int Position)> SungNotes(ScoreDocument score)
    {
        var notes = new List<(int, int)>();
        bool tiedOn = false;
        for (int b = 0; b < score.Bars.Count; b++)
            foreach (var item in score.Bars[b].Items)
            {
                if (!item.IsRest && !tiedOn) notes.Add((b, item.Start));
                tiedOn = !item.IsRest && item.TiedToNext;
            }
        return notes;
    }

    /// <summary>The next note a syllable can be sung on after a place, or null at the end of the song.</summary>
    public static (int Bar, int Position)? NextSungNote(ScoreDocument score, int bar, int position) =>
        SungNotes(score).Where(n => n.Bar > bar || n.Bar == bar && n.Position > position).Select(n => ((int, int)?)n).FirstOrDefault();
}
