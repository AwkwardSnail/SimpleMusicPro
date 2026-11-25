using SimpleMusicPro.Engraving;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Editing;

/// <summary>A place where something can be added: a staff, a bar (its index) and a place in the bar, in sixteenths.</summary>
public sealed record Placeholder(LaneKind Lane, int Bar, int Position);

/// <summary>
/// One place of a bar's tree of places: where it starts and how long it is (in sixteenths), how deep in the tree it
/// is (0 for the whole bar), and its parent (its index in the list, -1 for the whole bar).
/// </summary>
public sealed record PlaceNode(int Start, int Length, int Depth, int Parent);

/// <summary>
/// The places of a bar as a tree: the whole bar, cut into halves, quarters, eighths and sixteenths (in a bar of
/// three beats, the bar into its beats; in 6/8, into two dotted quarters, each of three eighths).
/// </summary>
public static class PlaceTree
{
    /// <summary>The tree of a bar, the whole bar first, each place after its parent.</summary>
    public static List<PlaceNode> Of(Measure measure)
    {
        int beat = Math.Max(1, measure.Pickup ? Math.Min(measure.Beat, measure.Length) : measure.Beat);
        var nodes = new List<PlaceNode> { new(0, measure.Length, 0, -1) };
        for (int i = 0; i < nodes.Count; i++)
            foreach (var (start, length) in Children(nodes[i].Start, nodes[i].Length, beat))
                nodes.Add(new PlaceNode(start, length, nodes[i].Depth + 1, i));
        return nodes;
    }

    /// <summary>How a place is cut: longer than a beat, into halves (an even number of beats) or into beats; a beat into two (or three, in 6/8); shorter, into two.</summary>
    private static IEnumerable<(int Start, int Length)> Children(int start, int length, int beat)
    {
        if (length <= 1) yield break;
        if (length > beat)
        {
            if (length % beat == 0 && length / beat % 2 == 0) { yield return (start, length / 2); yield return (start + length / 2, length / 2); yield break; }
            for (int s = 0; s < length; s += beat) yield return (start + s, Math.Min(beat, length - s));
            yield break;
        }
        if (length % 3 == 0 && length >= 3 && (length == beat && beat % 3 == 0 || length == 3))
        {
            for (int k = 0; k < 3; k++) yield return (start + k * length / 3, length / 3);
            yield break;
        }
        if (length % 2 == 0) { yield return (start, length / 2); yield return (start + length / 2, length / 2); }
    }

    /// <summary>The shape a place is drawn with: 0 the whole bar (a large circle), 1 a half (triangle), 2 a beat (square), 3 an eighth (diamond), 4 a sixteenth (small dot).</summary>
    public static int Shape(PlaceNode node) => node.Depth == 0 ? 0 : node.Length >= 8 ? 1 : node.Length >= 4 ? 2 : node.Length >= 2 ? 3 : 4;

    /// <summary>The longest place of the tree that starts at a place of the bar.</summary>
    public static PlaceNode At(List<PlaceNode> tree, int position) =>
        tree.FirstOrDefault(n => n.Start == position) ?? tree[^1];

    /// <summary>The places from the whole bar down to a place (its index), the whole bar first.</summary>
    public static List<int> PathTo(List<PlaceNode> tree, int index)
    {
        var path = new List<int>();
        for (int i = index; i >= 0; i = tree[i].Parent) path.Insert(0, i);
        return path;
    }
}

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
