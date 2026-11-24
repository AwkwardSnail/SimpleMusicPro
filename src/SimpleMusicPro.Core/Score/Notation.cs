namespace SimpleMusicPro.Score;

/// <summary>
/// A pitch as written: a letter (Step, 0 = C to 6 = B), a sharp or flat (Alter: -2 to 2) and an octave
/// (middle C is the C of octave 4).
/// </summary>
public readonly record struct Pitch(int Step, int Alter, int Octave)
{
    private static readonly int[] Semitones = [0, 2, 4, 5, 7, 9, 11];

    /// <summary>The MIDI key number (middle C is 60).</summary>
    public int Midi => 12 * (Octave + 1) + Semitones[Step] + Alter;

    /// <summary>The place on the page, counted in letters from C at the bottom of octave 0: two pitches one step apart are one staff step apart.</summary>
    public int Diatonic => Octave * 7 + Step;

    public string Name => "CDEFGAB"[Step] + (Alter switch { 1 => "#", -1 => "b", 2 => "##", -2 => "bb", _ => "" }) + Octave;

    private static readonly (int Step, int Alter)[] Sharps = [(0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (3, 0), (3, 1), (4, 0), (4, 1), (5, 0), (5, 1), (6, 0)];
    private static readonly (int Step, int Alter)[] Flats = [(0, 0), (1, -1), (1, 0), (2, -1), (2, 0), (3, 0), (4, -1), (4, 0), (5, -1), (5, 0), (6, -1), (6, 0)];

    /// <summary>
    /// Writes a MIDI key as a pitch: notes of the key are spelled as the key spells them, and the notes in between
    /// as sharps in a key with sharps (or none) and as flats in a key with flats.
    /// </summary>
    /// <param name="keySharps">-7 (seven flats) to 7 (seven sharps).</param>
    /// <param name="flats">For a note outside the key: true to spell it as a flat, false as a sharp, null as the key would.</param>
    public static Pitch FromMidi(int midi, int keySharps, bool? flats = null)
    {
        int octave = midi / 12 - 1, pc = midi % 12;
        // The notes of the major scale of the key, spelled: each letter gets the sharp or flat the key puts on it.
        var inKey = KeyAlter(keySharps);
        var spelled = (flats ?? keySharps < 0) ? Flats[pc] : Sharps[pc];
        // A note of the key's scale is spelled with the key's own letter, for example F# in G major, or Bb in F major.
        for (int step = 0; step < 7; step++)
            if (((Semitones[step] + inKey[step]) % 12 + 12) % 12 == pc) { spelled = (step, inKey[step]); break; }
        // B sharp and C flat belong to the next or the previous octave.
        int o = octave;
        if (spelled.Step == 0 && spelled.Alter < 0 && pc == 11) o++;      // C flat sounds as B of the octave below
        if (spelled.Step == 6 && spelled.Alter > 0 && pc == 0) o--;       // B sharp sounds as C of the octave above
        return new Pitch(spelled.Step, spelled.Alter, o);
    }

    /// <summary>The sharp (1) or flat (-1) the key signature puts on each letter, C to B.</summary>
    public static int[] KeyAlter(int keySharps)
    {
        var alter = new int[7];
        int[] sharpOrder = [3, 0, 4, 1, 5, 2, 6]; // F C G D A E B
        int[] flatOrder = [6, 2, 5, 1, 4, 0, 3];  // B E A D G C F
        for (int i = 0; i < Math.Abs(keySharps) && i < 7; i++) alter[(keySharps > 0 ? sharpOrder : flatOrder)[i]] = keySharps > 0 ? 1 : -1;
        return alter;
    }
}

/// <summary>
/// What is written at one place in a bar: a note or a chord (one or more pitches, lowest first) or a rest (none),
/// of one of the lengths music has a note for.
/// </summary>
/// <param name="Start">Where it starts in the bar, in sixteenth notes.</param>
/// <param name="Length">How long it lasts, in sixteenth notes: 1, 2, 3, 4, 6, 8, 12 or 16 (a sixteenth, an eighth, a dotted eighth, a quarter, a dotted quarter, a half, a dotted half, a whole).</param>
/// <param name="TiedToNext">The note goes on, tied, into the next item (in this bar or the next).</param>
public sealed record NotatedItem(int Start, int Length, IReadOnlyList<Pitch> Pitches, bool TiedToNext = false)
{
    public bool IsRest => Pitches.Count == 0;
    public int End => Start + Length;

    /// <summary>Whole, half, quarter, 8th, 16th: how many flags or beams (0 for a quarter or longer).</summary>
    public int Flags => (Length switch { 1 => 2, 2 or 3 => 1, _ => 0 });
    public bool Dotted => Length is 3 or 6 or 12;
    /// <summary>The length of the plain note value, without its dot, in sixteenths.</summary>
    public int Value => Dotted ? Length * 2 / 3 : Length;
}

/// <summary>
/// A note (or a chord: several keys at once) of the melody as entered by hand: where it starts, in sixteenths from
/// the start of the piece, how long it lasts, its MIDI keys, and whether a note outside the key is spelled as a
/// flat rather than a sharp.
/// </summary>
public sealed record MelodyNote(long Start, int Length, IReadOnlyList<int> Pitches, bool Flats = false)
{
    public long End => Start + Length;
}

/// <summary>One bar of a staff.</summary>
/// <param name="Number">The bar number, from 1.</param>
/// <param name="Start">Where the bar starts, in sixteenth notes from the start of the piece.</param>
/// <param name="Length">How long it is, in sixteenth notes (16 in 4/4).</param>
/// <param name="Pickup">A short bar before the first full one (an upbeat), numbered 0.</param>
public sealed record Measure(int Number, long Start, int Length, int Numerator, int Denominator, int KeySharps, IReadOnlyList<NotatedItem> Items, bool Pickup = false)
{
    /// <summary>A beat, in sixteenths: a quarter, or three eighths in a compound time such as 6/8.</summary>
    public int Beat => Denominator == 8 && Numerator % 3 == 0 && Numerator > 3 ? 6 : 16 / Denominator;
    public bool IsWholeRest => Items.Count == 1 && Items[0].IsRest && Items[0].Length == Length;
}
