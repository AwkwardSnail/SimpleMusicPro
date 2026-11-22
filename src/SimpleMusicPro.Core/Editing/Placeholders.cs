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
}
