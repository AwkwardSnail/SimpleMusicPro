using System.Globalization;
using PdfEditor.Core;

namespace SimpleMusicPro.Glyphs;

/// <summary>
/// One music symbol: its outline, in staff spaces (the gap between two lines of a staff), y upwards, with the
/// origin where SMuFL puts it (for a notehead, the middle of the head; for a clef, its reference line).
/// </summary>
public sealed class Glyph
{
    /// <summary>The SMuFL name, for example "noteheadBlack".</summary>
    public string Name { get; }
    /// <summary>How far the next symbol would start, in staff spaces.</summary>
    public double Advance { get; }
    /// <summary>The outline as move, line, curve and close steps, in staff spaces.</summary>
    public IReadOnlyList<ShapeStep> Steps { get; }
    /// <summary>The box the outline fits in, in staff spaces.</summary>
    public double Left { get; }
    public double Bottom { get; }
    public double Right { get; }
    public double Top { get; }

    public double Width => Right - Left;
    public double Height => Top - Bottom;

    internal Glyph(string name, int advanceUnits, string path)
    {
        const double unit = Bravura.UnitsPerStaffSpace;
        Name = name;
        Advance = advanceUnits / unit;
        var steps = new List<ShapeStep>();
        var tokens = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        PdfPoint Read(ref int at)
        {
            double x = double.Parse(tokens[at++], CultureInfo.InvariantCulture) / unit;
            double y = double.Parse(tokens[at++], CultureInfo.InvariantCulture) / unit;
            left = Math.Min(left, x); right = Math.Max(right, x);
            bottom = Math.Min(bottom, y); top = Math.Max(top, y);
            return new PdfPoint(x, y);
        }
        for (int i = 0; i < tokens.Length;)
        {
            char kind = tokens[i++][0];
            switch (kind)
            {
                case 'M': steps.Add(new ShapeStep('M', Read(ref i))); break;
                case 'L': steps.Add(new ShapeStep('L', Read(ref i))); break;
                case 'C':
                {
                    var c1 = Read(ref i);
                    var c2 = Read(ref i);
                    var end = Read(ref i);
                    steps.Add(new ShapeStep('C', end, c1, c2));
                    break;
                }
                case 'Z': steps.Add(new ShapeStep('Z', default)); break;
            }
        }
        Steps = steps;
        (Left, Bottom, Right, Top) = steps.Count == 0 ? (0, 0, 0, 0) : (left, bottom, right, top);
    }
}

/// <summary>The music symbols of Bravura, the open music font (SIL Open Font Licence 1.1), by SMuFL name.</summary>
public static class Bravura
{
    /// <summary>Bravura is drawn with 250 units to a staff space.</summary>
    public const double UnitsPerStaffSpace = 250;

    private static readonly Lazy<Dictionary<string, Glyph>> All = new(() =>
        BravuraData.All.ToDictionary(g => g.Name, g => new Glyph(g.Name, g.Advance, g.Path), StringComparer.Ordinal));

    /// <summary>The symbol with the given SMuFL name; throws when this program does not have it.</summary>
    public static Glyph Get(string name) =>
        All.Value.TryGetValue(name, out var glyph) ? glyph : throw new KeyNotFoundException($"The music symbol '{name}' is not one of those this program carries.");

    public static bool Has(string name) => All.Value.ContainsKey(name);

    /// <summary>Every symbol, in the order they were written.</summary>
    public static IEnumerable<Glyph> Every => All.Value.Values;
}
