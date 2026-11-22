namespace SimpleMusicPro.Score;

/// <summary>
/// One strum of a rhythm staff, or a rest in it, entered by hand: in a bar (counted from 0 in the score's list of
/// bars), at a place in the bar and for a length, both in sixteenth notes.
/// </summary>
public sealed record RhythmHit(int Bar, int Position, int Length, bool Rest = false, bool Accent = false)
{
    public int End => Position + Length;
}

/// <summary>
/// A rhythm staff: slashes with stems, flags and beams, for the strumming pattern. Its strums are entered by
/// hand, not taken from a MIDI file, and never overlap: a new one takes the place of what was there.
/// </summary>
public sealed class RhythmLane
{
    /// <summary>The shortest strum, in sixteenths: an eighth note, for now. Strums start on an eighth-note grid.</summary>
    public const int Shortest = 2;

    /// <summary>The lengths a strum can be, in sixteenths: an eighth, a quarter, a dotted quarter, a half, a dotted half, a whole.</summary>
    public static readonly IReadOnlyList<int> Lengths = [2, 4, 6, 8, 12, 16];

    private readonly List<RhythmHit> _hits = [];

    /// <summary>Written at the left of the first line, for example "Gtrs. 1 &amp; 2 (Acoustic)".</summary>
    public string Label { get; set; } = "";

    public IReadOnlyList<RhythmHit> Hits => _hits;

    /// <summary>The strums and rests of one bar, in order.</summary>
    public IEnumerable<RhythmHit> InBar(int bar) => _hits.Where(h => h.Bar == bar).OrderBy(h => h.Position);

    /// <summary>The strum or rest sounding at a place in a bar, or null when there is none.</summary>
    public RhythmHit? At(int bar, int position) => _hits.FirstOrDefault(h => h.Bar == bar && h.Position <= position && position < h.End);

    /// <summary>
    /// Adds a strum (or a rest) at a place in a bar, cut to fit the bar. Whatever it covers is taken away, and a
    /// strum it starts in the middle of is shortened to end where it begins. Returns what was added.
    /// </summary>
    public RhythmHit Add(int bar, int position, int length, int barLength, bool rest = false, bool accent = false)
    {
        if (position < 0 || position >= barLength) throw new ArgumentOutOfRangeException(nameof(position), "The place must be inside the bar.");
        if (position % Shortest != 0) throw new ArgumentOutOfRangeException(nameof(position), "A strum starts on an eighth note.");
        if (!Lengths.Contains(length)) throw new ArgumentOutOfRangeException(nameof(length), "A strum is an eighth, a quarter, a half or a whole note, or a dotted quarter or half.");
        length = Math.Min(length, barLength - position);
        int end = position + length;
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var h = _hits[i];
            if (h.Bar != bar || h.End <= position || h.Position >= end) continue;
            if (h.Position < position) _hits[i] = h with { Length = position - h.Position };   // starts before: shortened
            else _hits.RemoveAt(i);                                                              // inside the new one: gone
        }
        var hit = new RhythmHit(bar, position, length, rest, accent);
        _hits.Add(hit);
        return hit;
    }

    /// <summary>
    /// Puts back strums as they were saved, without the checks of <see cref="Add"/>: a strum shortened by a later
    /// one can have a length there is no single symbol for (it is written as tied lengths).
    /// </summary>
    public void Restore(IEnumerable<RhythmHit> hits)
    {
        foreach (var h in hits) if (h.Length > 0 && h.Position >= 0 && At(h.Bar, h.Position) is null) _hits.Add(h);
    }

    /// <summary>Takes away the strum or rest sounding at a place; false when there is none.</summary>
    public bool Remove(int bar, int position)
    {
        var hit = At(bar, position);
        return hit is not null && _hits.Remove(hit);
    }

    /// <summary>Turns the accent of the strum sounding at a place on or off.</summary>
    public bool ToggleAccent(int bar, int position)
    {
        var hit = At(bar, position);
        if (hit is null || hit.Rest) return false;
        _hits[_hits.IndexOf(hit)] = hit with { Accent = !hit.Accent };
        return true;
    }
}

/// <summary>
/// A score being made: its bars (from the MIDI file: their time and key signatures, and the notes of the voice
/// staff) and the staves stacked on each line.
/// </summary>
public sealed class ScoreDocument
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Quarter notes to the minute; 0 shows no tempo mark.</summary>
    public double Tempo { get; set; }

    /// <summary>The bars, with the notes of the voice staff in them.</summary>
    public IReadOnlyList<Measure> Bars { get; set; } = [];

    /// <summary>Whether the voice staff (five lines with notes) is part of the stack.</summary>
    public bool ShowVoice { get; set; } = true;

    /// <summary>The rhythm staff above the voice staff, or null for none.</summary>
    public RhythmLane? Rhythm { get; set; }

    /// <summary>The row of chord diagrams above the staves, or null for none.</summary>
    public ChordLane? Chords { get; set; }

    /// <summary>Chords stand no closer together than a quarter note, for now (in sixteenths).</summary>
    public const int ChordStep = 4;

    /// <summary>A place in a bar as sixteenths from the start of the score, to tell how far apart two places are across bar lines.</summary>
    public int Moment(int bar, int position)
    {
        int at = 0;
        for (int i = 0; i < bar && i < Bars.Count; i++) at += Bars[i].Length;
        return at + position;
    }

    /// <summary>Whether a chord at a place would stand closer than a quarter note to another chord (one at that very place excepted).</summary>
    public bool ChordTooClose(int bar, int position) =>
        Chords is not null && Chords.Marks.Any(m => !(m.Bar == bar && m.Position == position) && Math.Abs(Moment(m.Bar, m.Position) - Moment(bar, position)) < ChordStep);
}
