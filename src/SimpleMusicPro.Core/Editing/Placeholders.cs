using SimpleMusicPro.Engraving;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Editing;

/// <summary>A place where something can be added: a staff, a bar (its index) and a place in the bar, in sixteenths.</summary>
public sealed record Placeholder(LaneKind Lane, int Bar, int Position);

/// <summary>
/// Where the "+" signs go, that add a strum with a click: at the moment of the note selected in another staff,
/// just after each strum (so a pattern goes in one click after another), and on each beat where nothing sounds yet.
/// Never where a strum already sounds.
/// </summary>
public static class Placeholders
{
    /// <param name="selected">The note selected in the voice staff (its bar and place), or null.</param>
    /// <param name="lastAdded">The strum added last, or null.</param>
    public static IReadOnlyList<Placeholder> ForRhythm(ScoreDocument score, (int Bar, int Position)? selected, RhythmHit? lastAdded)
    {
        var lane = score.Rhythm;
        if (lane is null) return [];
        var found = new List<Placeholder>();
        void Offer(int bar, int position)
        {
            if (bar < 0 || bar >= score.Bars.Count) return;
            position -= position % RhythmLane.Shortest;               // strums stand on the eighth-note grid
            if (position >= score.Bars[bar].Length || lane.At(bar, position) is not null) return;
            if (!found.Any(f => f.Bar == bar && f.Position == position)) found.Add(new Placeholder(LaneKind.Rhythm, bar, position));
        }

        if (selected is { } s) Offer(s.Bar, s.Position);
        if (lastAdded is { } h)
        {
            if (h.End < score.Bars[h.Bar].Length) Offer(h.Bar, h.End);
            else Offer(h.Bar + 1, 0);
        }
        // Every beat where nothing sounds yet, and the end of each strum when nothing follows it at once: so an
        // empty bar gets one on each beat, and a gap left in a bar can still be filled.
        for (int bar = 0; bar < score.Bars.Count; bar++)
        {
            var m = score.Bars[bar];
            foreach (var strum in lane.InBar(bar)) Offer(bar, strum.End);
            for (int beat = 0; beat < m.Length; beat += m.Beat) Offer(bar, beat);
        }
        found.Sort((a, b) => a.Bar != b.Bar ? a.Bar.CompareTo(b.Bar) : a.Position.CompareTo(b.Position));
        return found;
    }
}
