namespace SimpleMusicPro.Score;

/// <summary>
/// Changes to a melody entered by hand (its notes in order, one line: a note never overlaps the next). Places are
/// moments in sixteenths from the start of the piece.
/// </summary>
public static class MelodyEdit
{
    /// <summary>The note sounding at a moment, or null when there is a rest.</summary>
    public static MelodyNote? At(IReadOnlyList<MelodyNote> notes, long moment) =>
        notes.FirstOrDefault(n => n.Start <= moment && moment < n.End);

    /// <summary>
    /// Makes a stretch silent: a note sounding into it is shortened to end where it begins, and the notes starting
    /// in it are taken away.
    /// </summary>
    public static void Clear(List<MelodyNote> notes, long start, int length)
    {
        long end = start + length;
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            var n = notes[i];
            if (n.End <= start || n.Start >= end) continue;
            if (n.Start < start) notes[i] = n with { Length = (int)(start - n.Start) };
            else notes.RemoveAt(i);
        }
    }

    /// <summary>
    /// Adds a note (in the place of whatever sounds in its stretch, see <see cref="Clear"/>), cut to end by
    /// <paramref name="limit"/> (the end of its bar). Returns it.
    /// </summary>
    public static MelodyNote Add(List<MelodyNote> notes, long start, int length, int pitch, bool flats, long limit)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "A note needs a length.");
        if (start >= limit) throw new ArgumentOutOfRangeException(nameof(start), "The place must be inside the bar.");
        if (pitch is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(pitch), "There is no such note.");
        length = (int)Math.Min(length, limit - start);
        Clear(notes, start, length);
        var note = new MelodyNote(start, length, [pitch], flats);
        notes.Add(note);
        notes.Sort((a, b) => a.Start.CompareTo(b.Start));
        return note;
    }

    /// <summary>
    /// Gives the note starting at a moment a new length, cut by <paramref name="limit"/>; a longer one takes the
    /// place of what follows it. Returns it as it now is, or null when no note starts there.
    /// </summary>
    public static MelodyNote? SetLength(List<MelodyNote> notes, long start, int length, long limit)
    {
        int i = notes.FindIndex(n => n.Start == start);
        if (i < 0 || length <= 0) return null;
        var old = notes[i];
        notes.RemoveAt(i);
        length = (int)Math.Min(length, Math.Max(limit, old.End) - start);
        Clear(notes, start, length);
        var note = old with { Length = length };
        notes.Add(note);
        notes.Sort((a, b) => a.Start.CompareTo(b.Start));
        return note;
    }

    /// <summary>A pitch moved by steps on the staff (lines and spaces), keeping to the key signature.</summary>
    public static int StepBy(int midi, int steps, int keySharps, bool flats)
    {
        var p = Pitch.FromMidi(midi, keySharps, flats ? true : null);
        int diatonic = p.Diatonic + steps;
        int step = ((diatonic % 7) + 7) % 7, octave = (diatonic - step) / 7;
        return new Pitch(step, Pitch.KeyAlter(keySharps)[step], octave).Midi;
    }

    /// <summary>The pitch on a place of the staff (counted as <see cref="Pitch.Diatonic"/>), with the key's sharp or flat, or the one given.</summary>
    public static int OnStaff(int diatonic, int keySharps, int? alter = null)
    {
        int step = ((diatonic % 7) + 7) % 7, octave = (diatonic - step) / 7;
        return Math.Clamp(new Pitch(step, alter ?? Pitch.KeyAlter(keySharps)[step], octave).Midi, 0, 127);
    }

    /// <summary>A note's pitch with a sharp (1), a flat (-1) or neither (0) on its letter.</summary>
    public static (int Midi, bool Flats) WithAccidental(int midi, int alter, int keySharps, bool flats)
    {
        var p = Pitch.FromMidi(midi, keySharps, flats ? true : null);
        return (Math.Clamp(new Pitch(p.Step, alter, p.Octave).Midi, 0, 127), alter < 0);
    }
}
