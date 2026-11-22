namespace SimpleMusicPro.Score;

/// <summary>
/// A guitar chord shape: its name (C, Am7, D/F#) and the fret pressed on each string, from the low E string to the
/// high e: -1 for a string not played, 0 for an open string.
/// </summary>
public sealed record ChordShape(string Name, IReadOnlyList<int> Frets)
{
    public const int Muted = -1;

    private IEnumerable<int> Pressed => Frets.Where(f => f > 0);

    /// <summary>The fret at the top of the diagram: 1 (with the nut drawn) when the shape fits in the first four frets.</summary>
    public int FirstFret => Pressed.DefaultIfEmpty(0).Max() <= 4 ? 1 : Pressed.Min();

    /// <summary>How many frets the diagram shows: four, or five for a wide shape.</summary>
    public int FretsShown => Math.Max(4, Pressed.DefaultIfEmpty(0).Max() - FirstFret + 1);

    /// <summary>
    /// A finger laid across several strings at the lowest fret pressed, out to the high e string: its fret and the
    /// first string it covers (0 for the low E). Null when the shape has none.
    /// </summary>
    public (int Fret, int FromString)? Barre
    {
        get
        {
            if (!Pressed.Any()) return null;
            int f = Pressed.Min();
            if (Frets[5] != f || Frets.Count(x => x == f) < 2) return null;
            int from = Enumerable.Range(0, 6).First(i => Frets[i] == f);
            for (int i = from; i < 6; i++) if (Frets[i] < f) return null;   // an open or unplayed string under the finger
            return (f, from);
        }
    }

    /// <summary>A shape written as six characters, low E first: x for not played, a digit (or a letter from a for 10) for the fret.</summary>
    public static ChordShape Parse(string name, string frets) =>
        new(name, frets.Select(c => c is 'x' or 'X' ? Muted : char.IsDigit(c) ? c - '0' : c - 'a' + 10).ToArray());

    public override string ToString() => $"{Name} {string.Concat(Frets.Select(f => f < 0 ? "x" : f < 10 ? f.ToString() : ((char)('a' + f - 10)).ToString()))}";
}

/// <summary>A chord written above a moment of a bar (its index, and its place in sixteenths): a name, drawn with its diagram.</summary>
public sealed record ChordMark(int Bar, int Position, string Name);

/// <summary>The row of chord diagrams above the staves. Chords are entered by hand, at most one at each place.</summary>
public sealed class ChordLane
{
    private readonly List<ChordMark> _marks = [];

    public IReadOnlyList<ChordMark> Marks => _marks;

    public IEnumerable<ChordMark> InBar(int bar) => _marks.Where(m => m.Bar == bar).OrderBy(m => m.Position);

    public ChordMark? At(int bar, int position) => _marks.FirstOrDefault(m => m.Bar == bar && m.Position == position);

    /// <summary>
    /// Writes a chord at a place (any moment of the bar), in the place of the one there, if any. The score checks
    /// that it is not too close to another (see <see cref="ScoreDocument.ChordTooClose"/>).
    /// </summary>
    public ChordMark Set(int bar, int position, string name)
    {
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position), "The place must be inside the bar.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A chord needs a name.", nameof(name));
        Remove(bar, position);
        var mark = new ChordMark(bar, position, name.Trim());
        _marks.Add(mark);
        return mark;
    }

    public bool Remove(int bar, int position) => _marks.RemoveAll(m => m.Bar == bar && m.Position == position) > 0;

    /// <summary>Puts back chords as they were saved.</summary>
    public void Restore(IEnumerable<ChordMark> marks)
    {
        foreach (var m in marks) if (m.Position >= 0 && m.Name.Length > 0 && At(m.Bar, m.Position) is null) _marks.Add(m);
    }
}

/// <summary>
/// The chords offered in the list: for each of the twelve keys, the common kinds (major, minor, 7, m7, maj7, sus2,
/// sus4, 6, m6, 9, add9, dim, aug), in open shapes where there are well-known ones and otherwise as barre shapes
/// moved up the neck; and the usual slash chords.
/// </summary>
public static class ChordLibrary
{
    public static readonly IReadOnlyList<string> Roots = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];
    public static readonly IReadOnlyList<string> Kinds = ["", "m", "7", "m7", "maj7", "sus2", "sus4", "6", "m6", "9", "add9", "dim", "aug"];

    // The other spelling of each black key, as it may be typed.
    private static readonly Dictionary<string, string> OtherSpelling = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Db"] = "C#", ["D#"] = "Eb", ["Gb"] = "F#", ["G#"] = "Ab", ["A#"] = "Bb",
    };

    // Shapes moved up the neck, as frets above the root's fret (null: string not played), low E first.
    // With the root on the low E string:
    private static readonly Dictionary<string, int?[]> OnSixth = new()
    {
        [""] = [0, 2, 2, 1, 0, 0], ["m"] = [0, 2, 2, 0, 0, 0], ["7"] = [0, 2, 0, 1, 0, 0], ["m7"] = [0, 2, 0, 0, 0, 0],
        ["maj7"] = [0, null, 1, 1, 0, null], ["sus4"] = [0, 2, 2, 2, 0, 0], ["6"] = [0, 2, 2, 1, 2, 0], ["m6"] = [0, 2, 2, 0, 2, 0],
        ["9"] = [0, 2, 0, 1, 0, 2],
    };
    // With the root on the A string:
    private static readonly Dictionary<string, int?[]> OnFifth = new()
    {
        [""] = [null, 0, 2, 2, 2, 0], ["m"] = [null, 0, 2, 2, 1, 0], ["7"] = [null, 0, 2, 0, 2, 0], ["m7"] = [null, 0, 2, 0, 1, 0],
        ["maj7"] = [null, 0, 2, 1, 2, 0], ["sus2"] = [null, 0, 2, 2, 0, 0], ["sus4"] = [null, 0, 2, 2, 3, 0], ["6"] = [null, 0, 2, 2, 2, 2],
        ["m6"] = [null, 0, 2, 2, 1, 2], ["9"] = [null, 0, -1, 0, 0, 0], ["add9"] = [null, 0, 2, 4, 2, 0], ["dim"] = [null, 0, 1, 2, 1, null],
        ["aug"] = [null, 0, -1, -2, -2, null],
    };

    // Well-known open shapes, used in the place of the moved ones.
    private static readonly (string Name, string Frets)[] Open =
    [
        ("C", "x32010"), ("Cmaj7", "x32000"), ("C7", "x32310"), ("C6", "x32210"), ("Cadd9", "x32030"), ("Csus2", "x30033"), ("Csus4", "x33011"),
        ("D", "xx0232"), ("Dm", "xx0231"), ("D7", "xx0212"), ("Dm7", "xx0211"), ("Dmaj7", "xx0222"), ("Dsus2", "xx0230"), ("Dsus4", "xx0233"), ("D6", "xx0202"),
        ("Eadd9", "024100"),
        ("Fmaj7", "xx3210"),
        ("G", "320003"), ("G7", "320001"), ("Gmaj7", "320002"), ("G6", "320000"), ("Gsus2", "300033"), ("Gsus4", "3x0013"), ("Gadd9", "3x0203"),
        ("B7", "x21202"),
        ("Caug", "x32110"), ("Daug", "xx0332"), ("Eaug", "032110"), ("Gaug", "321003"), ("Aaug", "x03221"),
        ("Ddim", "xx0131"),
    ];

    // The usual slash chords: a chord over another bass note.
    private static readonly (string Name, string Frets)[] Slash =
    [
        ("C/E", "032010"), ("C/G", "332010"), ("C/B", "x22010"), ("D/F#", "200232"), ("D/A", "x00232"), ("D/C", "x30232"),
        ("G/B", "x20003"), ("G/F#", "2x0003"), ("Am/G", "3x2210"), ("Am/E", "002210"), ("A/E", "002220"), ("E/G#", "4x2100"),
        ("F/C", "x33211"), ("F/A", "x03211"), ("Dm/C", "x30231"), ("Em/D", "xx0987"),
    ];

    private static readonly Lazy<IReadOnlyList<ChordShape>> _all = new(Build);
    private static readonly Lazy<Dictionary<string, ChordShape>> _byName = new(() => _all.Value.ToDictionary(c => c.Name, StringComparer.Ordinal));

    /// <summary>Every chord in the list, key by key (C first), then the slash chords.</summary>
    public static IReadOnlyList<ChordShape> All => _all.Value;

    /// <summary>The shape for a chord name, also when its key is spelt the other way (Db for C#); null when there is none.</summary>
    public static ChordShape? Find(string name)
    {
        string n = Normalise(name);
        return _byName.Value.TryGetValue(n, out var shape) ? shape : null;
    }

    /// <summary>The chords whose name starts with what was typed (Am finds Am, Am7, Am6, Am/G...), in the list's order.</summary>
    public static IEnumerable<ChordShape> Search(string typed)
    {
        string t = Normalise(typed);
        if (t.Length == 0) return All;
        return All.Where(c => c.Name.StartsWith(t, StringComparison.Ordinal));
    }

    /// <summary>A typed name as the list spells it: the key with a capital letter and the list's spelling (C#, Eb...), ♯ and ♭ as # and b.</summary>
    public static string Normalise(string name)
    {
        string s = name.Trim().Replace('♯', '#').Replace('♭', 'b');
        if (s.Length == 0) return s;
        return string.Join('/', s.Split('/').Select((part, i) => NormaliseRoot(part)));
    }

    private static string NormaliseRoot(string part)
    {
        if (part.Length == 0 || !"abcdefgABCDEFG".Contains(part[0])) return part;
        string root = char.ToUpperInvariant(part[0]).ToString();
        int k = 1;
        if (part.Length > 1 && part[1] is '#' or 'b') { root += part[1]; k = 2; }
        if (OtherSpelling.TryGetValue(root, out var other)) root = other;
        string rest = part[k..];
        // "M7" and "Maj7" are typed for maj7, "min" for m.
        if (rest.StartsWith("Maj", StringComparison.OrdinalIgnoreCase)) rest = "maj" + rest[3..];
        else if (rest.StartsWith("min", StringComparison.Ordinal)) rest = "m" + rest[3..];
        else if (rest.StartsWith("M7", StringComparison.Ordinal)) rest = "maj7" + rest[2..];
        return root + rest;
    }

    private static IReadOnlyList<ChordShape> Build()
    {
        var open = Open.ToDictionary(o => o.Name, o => o.Frets);
        var list = new List<ChordShape>();
        for (int r = 0; r < Roots.Count; r++)
            foreach (string kind in Kinds)
            {
                string name = Roots[r] + kind;
                if (open.TryGetValue(name, out var frets)) { list.Add(ChordShape.Parse(name, frets)); continue; }
                var shape = Moved(name, kind, r);
                if (shape is not null) list.Add(shape);
            }
        list.AddRange(Slash.Select(s => ChordShape.Parse(s.Name, s.Frets)));
        return list;
    }

    /// <summary>The moved shape lowest on the neck for a chord: with the root on the low E string or on the A string.</summary>
    private static ChordShape? Moved(string name, string kind, int root)
    {
        ChordShape? best = null;
        int bestFret = int.MaxValue;
        foreach (var (table, openRoot) in new[] { (OnSixth, 4), (OnFifth, 9) })
        {
            if (!table.TryGetValue(kind, out var offsets)) continue;
            int lowest = offsets.Where(o => o is not null).Min(o => o!.Value);
            int fret = (root - openRoot + 12) % 12;
            if (fret + lowest < 0) fret += 12;   // a shape reaching below the root's fret needs room under it
            if (fret >= bestFret) continue;
            bestFret = fret;
            best = new ChordShape(name, offsets.Select(o => o is null ? ChordShape.Muted : fret + o.Value).ToArray());
        }
        return best;
    }
}
