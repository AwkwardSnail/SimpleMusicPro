using SimpleMusicPro.Engraving;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Editing;

/// <summary>A place where something can be added: a staff, a bar (its index) and a place in the bar, in sixteenths.</summary>
public sealed record Placeholder(LaneKind Lane, int Bar, int Position);

/// <summary>
/// Where the "+" signs go, that add a strum with a click: only at the moment of the note (or rest) selected in
/// the voice staff, rounded to the eighth-note grid, and never where a strum already sounds.
/// </summary>
public static class Placeholders
{
    /// <param name="selected">The note selected in the voice staff (its bar and place), or null for none.</param>
    public static IReadOnlyList<Placeholder> ForRhythm(ScoreDocument score, (int Bar, int Position)? selected)
    {
        var lane = score.Rhythm;
        if (lane is null || selected is not { } s || s.Bar < 0 || s.Bar >= score.Bars.Count) return [];
        int position = s.Position - s.Position % RhythmLane.Shortest;   // strums stand on the eighth-note grid
        if (position >= score.Bars[s.Bar].Length || lane.At(s.Bar, position) is not null) return [];
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
}
