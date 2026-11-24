using SimpleMusicPro.Midi;

namespace SimpleMusicPro.Score;

/// <summary>
/// Turns notes as they were played into notes as they are written: times snapped to a grid of sixteenth notes,
/// the notes of a track made into one line of notes, chords and rests, cut into bars with ties, and into the
/// note lengths a copyist would use.
/// </summary>
public static class Quantizer
{
    /// <summary>The note lengths there are symbols for, longest first, in sixteenths.</summary>
    private static readonly int[] Lengths = [16, 12, 8, 6, 4, 3, 2, 1];

    /// <summary>
    /// The notes of one track as a single line, in sixteenths: notes that start in the same sixteenth are one chord,
    /// as long as the longest of them lasts, but never past the start of the next one.
    /// </summary>
    public static List<MelodyNote> Notes(MidiFile midi, MidiTrack track)
    {
        double unit = midi.TicksPerQuarter / 4.0; // a sixteenth note, in ticks
        long Slot(long ticks) => (long)Math.Round(ticks / unit);
        var chords = track.Notes
            .GroupBy(n => Slot(n.Start))
            .OrderBy(g => g.Key)
            .Select(g => (Start: g.Key, End: g.Key + g.Max(n => Math.Max(1, Slot(n.End) - g.Key)), Pitches: g.Select(n => n.Pitch).Distinct().Order().ToList()))
            .ToList();
        var notes = new List<MelodyNote>();
        for (int i = 0; i < chords.Count; i++)
        {
            long end = i + 1 < chords.Count ? Math.Min(chords[i].End, chords[i + 1].Start) : chords[i].End;
            if (end > chords[i].Start) notes.Add(new MelodyNote(chords[i].Start, (int)(end - chords[i].Start), chords[i].Pitches));
        }
        return notes;
    }

    /// <summary>
    /// The bars of one track. Notes that start together become a chord; a note lasts until the next one starts
    /// at most, so the track reads as a single line (a melody, or chords one after another).
    /// </summary>
    public static IReadOnlyList<Measure> Voice(MidiFile midi, MidiTrack track)
    {
        double unit = midi.TicksPerQuarter / 4.0; // a sixteenth note, in ticks
        long Slot(long ticks) => (long)Math.Round(ticks / unit);
        var segments = Notes(midi, track);
        long total = segments.Count == 0 ? 0 : segments[^1].End;

        var measures = new List<Measure>();
        long start = 0;
        bool pickup = false;
        int first = 0; // the first segment that can still reach this bar
        while (start < total || measures.Count == 0)
        {
            var ts = midi.TimeSignatureAt((long)Math.Round(start * unit));
            int key = midi.KeySignatures.LastOrDefault(k => Slot(k.Time) <= start)?.Sharps ?? 0;
            int length = Math.Max(1, ts.Numerator * 16 / ts.Denominator);
            // A change of time signature starts a bar of its own, even part-way through one.
            long next = midi.TimeSignatures.Select(t => Slot(t.Time)).Where(s => s > start).DefaultIfEmpty(long.MaxValue).Min();
            if (next < start + length) length = (int)(next - start);
            // A short bar at the very start, followed at once by the real time signature, is an upbeat: it belongs to that time signature.
            if (measures.Count == 0 && next == start + length && next != long.MaxValue)
            {
                var following = midi.TimeSignatureAt((long)Math.Round(next * unit));
                if (following.Numerator * 16 / following.Denominator > length) { ts = following; pickup = true; }
            }
            long barEnd = start + length;
            int beat = Beat(ts.Numerator, ts.Denominator);
            if (pickup && measures.Count == 0) beat = Math.Min(beat, length); // a one-beat upbeat is one beat

            while (first < segments.Count && segments[first].End <= start) first++;
            var items = WriteBar(start, length, key, beat, segments, first);
            measures.Add(new Measure(measures.Count + (pickup ? 0 : 1), start, length, ts.Numerator, ts.Denominator, key, items, Pickup: pickup && measures.Count == 0));
            start = barEnd;
        }
        return measures;
    }

    /// <summary>
    /// The same bars (their places, time and key signatures) with other notes written in them: the melody as entered
    /// by hand. The notes are made a single line first: each lasts until the next one starts at most.
    /// </summary>
    public static IReadOnlyList<Measure> Rewrite(IReadOnlyList<Measure> bars, IEnumerable<MelodyNote> notes)
    {
        var sorted = notes.Where(n => n.Length > 0 && n.Pitches.Count > 0).OrderBy(n => n.Start).ToList();
        var line = new List<MelodyNote>();
        for (int i = 0; i < sorted.Count; i++)
        {
            if (i + 1 < sorted.Count && sorted[i + 1].Start == sorted[i].Start) continue;     // two at one place: the later entry wins
            long end = i + 1 < sorted.Count ? Math.Min(sorted[i].End, sorted[i + 1].Start) : sorted[i].End;
            line.Add(sorted[i] with { Length = (int)(end - sorted[i].Start) });
        }
        var result = new List<Measure>();
        int first = 0;
        foreach (var m in bars)
        {
            while (first < line.Count && line[first].End <= m.Start) first++;
            int beat = m.Pickup ? Math.Min(m.Beat, m.Length) : m.Beat;
            result.Add(m with { Items = WriteBar(m.Start, m.Length, m.KeySharps, beat, line, first) });
        }
        return result;
    }

    /// <summary>The notes and rests of one bar, from the notes of the line (from <paramref name="first"/> on) that sound in it.</summary>
    private static List<NotatedItem> WriteBar(long start, int length, int key, int beat, List<MelodyNote> line, int first)
    {
        long barEnd = start + length;
        var items = new List<NotatedItem>();
        int cursor = 0; // where in the bar writing has got to
        void Write(int from, int to, IReadOnlyList<int> pitches, bool? flats, bool tiedOut)
        {
            var spelled = pitches.Select(m => Pitch.FromMidi(m, key, flats)).ToList();
            var parts = Split(from, to - from, length, beat);
            for (int k = 0; k < parts.Count; k++)
                items.Add(new NotatedItem(parts[k].Start, parts[k].Length, spelled, TiedToNext: pitches.Count > 0 && (k < parts.Count - 1 || tiedOut)));
        }
        for (int s = first; s < line.Count && line[s].Start < barEnd; s++)
        {
            var seg = line[s];
            if (seg.End <= start) continue;
            int from = (int)(Math.Max(seg.Start, start) - start), to = (int)(Math.Min(seg.End, barEnd) - start);
            if (from > cursor) Write(cursor, from, [], null, false);                 // a rest before it
            Write(from, to, seg.Pitches, seg.Flats ? true : null, seg.End > barEnd);
            cursor = to;
        }
        if (cursor < length) Write(cursor, length, [], null, false);
        // A bar with nothing in it but a rest is one rest, however long the bar is.
        if (items.Count > 0 && items.All(i => i.IsRest) && items.Sum(i => i.Length) == length) items = [new NotatedItem(0, length, [])];
        return items;
    }

    private static int Beat(int numerator, int denominator) => denominator == 8 && numerator % 3 == 0 && numerator > 3 ? 6 : 16 / denominator;

    /// <summary>
    /// Cuts a stretch of a bar into note lengths there are symbols for, in the order and places a copyist would
    /// choose: a note shorter than a beat stays inside its beat; a longer one starts on a beat and does not step
    /// across the middle of the bar.
    /// </summary>
    internal static List<(int Start, int Length)> Split(int position, int length, int barLength, int beat)
    {
        var parts = new List<(int, int)>();
        // The middle of the bar, in a bar of an even number of beats (a bar of three beats has none).
        int half = barLength >= 8 && barLength % beat == 0 && barLength / beat % 2 == 0 ? barLength / 2 : 0;
        bool Fits(int pos, int d)
        {
            if (d < beat) return pos % beat + d <= beat;
            if (pos % beat != 0) return false;
            if (pos == 0) return true;
            return half == 0 || pos >= half || pos + d <= half;      // not across the middle of the bar
        }
        while (length > 0)
        {
            int take = 1;
            foreach (int d in Lengths)
            {
                if (d > length || !Fits(position, d)) continue;
                if (d == 12 && position != 0) continue;               // a dotted half only opens a bar
                if (d == 6 && beat == 4 && position % 8 != 0) continue; // a dotted quarter starts on the 1st or 3rd beat
                take = d;
                break;
            }
            parts.Add((position, take));
            position += take;
            length -= take;
        }
        return parts;
    }
}
