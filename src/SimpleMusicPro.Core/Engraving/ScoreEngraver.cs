using PdfEditor.Core;
using SimpleMusicPro.Glyphs;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Engraving;

/// <summary>What a printed score looks like and says at the top.</summary>
public sealed record ScoreOptions
{
    public string Title { get; init; } = "";
    /// <summary>Under the title, at the right: composer or the like.</summary>
    public string Subtitle { get; init; } = "";
    /// <summary>Quarter notes to the minute, shown above the first bar; 0 shows none.</summary>
    public double Tempo { get; init; }
    public double PageWidth { get; init; } = 595.28;
    public double PageHeight { get; init; } = 841.89;
    public double Margin { get; init; } = 42;
    /// <summary>The gap between two lines of the staff, in points. Everything else is drawn in proportion to it.</summary>
    public double StaffSpace { get; init; } = 5.6;
}

/// <summary>
/// Draws bars of notes as a printed score: a staff with its clef, key and time signature, noteheads, stems,
/// flags and beams, accidentals, dots, ties and rests, barlines and bar numbers, laid out in lines that fill the
/// page width and in pages. The symbols are Bravura's, drawn as vector shapes.
/// </summary>
public static class ScoreEngraver
{
    // Sizes as Bravura's designers give them, in staff spaces.
    private const double StaffLine = 0.13, Stem = 0.12, BarLine = 0.16, LedgerLine = 0.16, LedgerExtension = 0.4;
    private const double BeamThickness = 0.5, BeamGap = 0.25, StemLength = 3.5;
    private const int BottomLineDiatonic = 4 * 7 + 2; // E4: the bottom line of a treble staff
    private const int MiddleLineDiatonic = 34;         // B4

    private static readonly PathStyle Ink = new() { Fill = PdfColor.Black };

    /// <summary>A score of one voice staff, made into a PDF.</summary>
    public static PdfDocument Engrave(IReadOnlyList<Measure> measures, ScoreOptions options)
    {
        var doc = PdfDocument.Create();
        double ss = options.StaffSpace;
        double contentLeft = options.Margin, contentRight = options.PageWidth - options.Margin;

        // The first line starts with clef, key and time signature; the others with clef and key.
        var plans = Plan(measures, options, contentRight - contentLeft);
        var pages = new List<List<SystemPlan>> { new() };
        double y = options.Margin + (HasHeading(options) ? 64 : 0) + 3.5 * ss;
        double systemHeight = (4 + 7.5) * ss, gap = 3.5 * ss;
        foreach (var plan in plans)
        {
            if (y + 4 * ss + 4 * ss > options.PageHeight - options.Margin && pages[^1].Count > 0)
            {
                pages.Add([]);
                y = options.Margin + 3.5 * ss;
            }
            plan.StaffTop = y;
            pages[^1].Add(plan);
            y += systemHeight + gap;
        }

        for (int p = 0; p < pages.Count; p++)
        {
            doc.AddPage(options.PageWidth, options.PageHeight);
            int page = doc.PageCount - 1;
            var systems = pages[p];
            int number = p + 1;
            doc.Draw(page, canvas =>
            {
                if (p == 0) DrawHeading(canvas, options);
                foreach (var system in systems) DrawSystem(canvas, system, measures, options, contentLeft, contentRight);
            });
        }
        return doc;
    }

    private static bool HasHeading(ScoreOptions o) => o.Title.Length > 0 || o.Subtitle.Length > 0 || o.Tempo > 0;

    // ------------------------------------------------------------------ placing notes along a bar

    /// <summary>A note or chord placed in a bar.</summary>
    private sealed class PlacedItem
    {
        public required NotatedItem Item { get; init; }
        public int Index { get; init; }
        /// <summary>Where the left edge of the notehead is: a part that does not stretch, and a part that does, from the bar's start.</summary>
        public double Fixed, Stretch;
        public bool StemUp;
        public List<(Pitch Pitch, double Shift, string? Accidental, double AccidentalOffset)> Notes = [];
        /// <summary>Whether the stem belongs to a beam (set when the bar is laid out).</summary>
        public bool Beamed;
    }

    private sealed class PlacedMeasure
    {
        public required Measure Measure { get; init; }
        public List<PlacedItem> Items { get; } = [];
        public double FixedWidth, StretchWidth;
        public double NaturalWidth => FixedWidth + StretchWidth;
        /// <summary>More than 1 for several whole-bar rests in a row, written as one.</summary>
        public int MultiRest = 1;
        public int LastNumber => Measure.Number + MultiRest - 1;
        public List<BeamGroup> Beams { get; } = [];
    }

    private sealed class BeamGroup
    {
        public List<PlacedItem> Items { get; } = [];
        public bool StemUp;
    }

    private sealed class SystemPlan
    {
        public List<PlacedMeasure> Measures { get; } = [];
        public int FirstMeasureNumber;
        public bool ShowTimeSignature;
        public double PrefixWidth;
        public double Scale = 1;
        public double StaffTop;
    }

    /// <summary>How far a note of a length stretches the bar after it (beyond its own head), in staff spaces.</summary>
    private static double Spacing(int length) => length switch { 1 => 1.5, 2 => 2.1, 3 => 2.7, 4 => 3.1, 6 => 3.9, 8 => 4.6, 12 => 5.4, _ => 6.2 };

    private static double StepY(double staffTop, double ss, int steps) => staffTop + 4 * ss - steps * ss / 2;

    private static List<SystemPlan> Plan(IReadOnlyList<Measure> measures, ScoreOptions options, double width)
    {
        double ss = options.StaffSpace;
        var placed = new List<PlacedMeasure>();
        for (int k = 0; k < measures.Count; k++)
        {
            var m = measures[k];
            int run = 1;
            if (m.IsWholeRest && !m.Pickup)
                while (k + run < measures.Count && measures[k + run].IsWholeRest && measures[k + run].Numerator == m.Numerator && measures[k + run].Denominator == m.Denominator && measures[k + run].KeySharps == m.KeySharps) run++;
            if (run < 2) { placed.Add(PlaceMeasure(m)); continue; }
            placed.Add(new PlacedMeasure { Measure = m, MultiRest = run, FixedWidth = 10, StretchWidth = 0 });
            k += run - 1;
        }
        var systems = new List<SystemPlan>();
        int i = 0;
        while (i < placed.Count)
        {
            var system = new SystemPlan { FirstMeasureNumber = placed[i].Measure.Number };
            bool timeChanged = i == 0 || placed[i].Measure.Numerator != placed[i - 1].Measure.Numerator || placed[i].Measure.Denominator != placed[i - 1].Measure.Denominator;
            system.ShowTimeSignature = timeChanged;
            system.PrefixWidth = PrefixWidth(placed[i].Measure.KeySharps, timeChanged ? placed[i].Measure : null);
            double available = width - system.PrefixWidth * ss, used = 0;
            while (i < placed.Count)
            {
                double w = placed[i].NaturalWidth * ss;
                // A change of time signature part-way along a line is shown before the bar it starts.
                if (system.Measures.Count > 0 && used + w > available) break;
                system.Measures.Add(placed[i]);
                used += w;
                i++;
            }
            double fixedTotal = system.Measures.Sum(m => m.FixedWidth) * ss, stretchTotal = system.Measures.Sum(m => m.StretchWidth) * ss;
            system.Scale = stretchTotal <= 0 ? 1 : Math.Clamp((available - fixedTotal) / stretchTotal, 0.5, i >= placed.Count ? 1.35 : 3);
            systems.Add(system);
        }
        return systems;
    }

    /// <summary>The width of clef, key signature and, where wanted, time signature at the start of a line, in staff spaces.</summary>
    private static double PrefixWidth(int keySharps, Measure? timeFrom)
    {
        double w = 0.7 + Bravura.Get("gClef").Advance + 0.8;
        if (keySharps != 0) w += Math.Abs(keySharps) * 1.15 + 0.6;
        if (timeFrom is not null) w += 0.3 + Math.Max(DigitsWidth(timeFrom.Numerator), DigitsWidth(timeFrom.Denominator)) + 1.0;
        return w;
    }

    private static double DigitsWidth(int number) => number.ToString().Sum(d => Bravura.Get("timeSig" + d).Advance);

    private static PlacedMeasure PlaceMeasure(Measure measure)
    {
        var result = new PlacedMeasure { Measure = measure };
        double head = Bravura.Get("noteheadBlack").Advance;
        var state = new Dictionary<int, int>(); // the sharp or flat each place on the staff has in this bar
        var keyAlter = Pitch.KeyAlter(measure.KeySharps);
        int Current(Pitch p) => state.TryGetValue(p.Diatonic, out int a) ? a : keyAlter[p.Step];

        double fixedX = 1.0, stretchX = 0;
        int index = 0;
        foreach (var item in measure.Items)
        {
            var placed = new PlacedItem { Item = item, Index = index++ };
            if (item.IsRest)
            {
                double w = item.Length == measure.Numerator * 16 / measure.Denominator ? Bravura.Get("restWhole").Advance : RestWidth(item);
                placed.Fixed = fixedX; placed.Stretch = stretchX;
                fixedX += w + (item.Dotted ? 0.8 : 0);
                stretchX += Spacing(item.Length);
                result.Items.Add(placed);
                continue;
            }

            var pitches = item.Pitches.OrderBy(p => p.Diatonic).ThenBy(p => p.Alter).ToList();
            int low = pitches[0].Diatonic, high = pitches[^1].Diatonic;
            placed.StemUp = item.Length < 16 && (MiddleLineDiatonic - low) > (high - MiddleLineDiatonic);

            // Seconds in a chord: the head that touches the one before goes to the other side of the stem.
            var shifts = new double[pitches.Count];
            if (placed.StemUp) { for (int k = 1; k < pitches.Count; k++) if (pitches[k].Diatonic - pitches[k - 1].Diatonic == 1 && shifts[k - 1] == 0) shifts[k] = head - Stem; }
            else { for (int k = pitches.Count - 2; k >= 0; k--) if (pitches[k + 1].Diatonic - pitches[k].Diatonic == 1 && shifts[k + 1] == 0) shifts[k] = -(head - Stem); }

            // Accidentals this chord needs, stacked in columns to the left, highest first.
            var needed = new List<(int Index, string Glyph)>();
            for (int k = 0; k < pitches.Count; k++)
            {
                if (pitches[k].Alter == Current(pitches[k])) continue;
                needed.Add((k, AccidentalGlyph(pitches[k].Alter)));
                state[pitches[k].Diatonic] = pitches[k].Alter;
            }
            var columns = new List<int>(); // for each accidental, its column
            var lastInColumn = new List<int>();
            foreach (var (k, _) in needed.OrderByDescending(n => n.Index))
            {
                int c = 0;
                while (c < lastInColumn.Count && Math.Abs(pitches[k].Diatonic - lastInColumn[c]) < 6) c++;
                if (c == lastInColumn.Count) lastInColumn.Add(int.MinValue);
                lastInColumn[c] = pitches[k].Diatonic;
                columns.Add(c);
            }
            double accidentalWidth = needed.Count == 0 ? 0 : lastInColumn.Count * (Bravura.Get("accidentalSharp").Advance + 0.2) + 0.1;
            double leftExtra = shifts.Any(s => s < 0) ? head - Stem : 0;

            placed.Fixed = fixedX + accidentalWidth + leftExtra;
            placed.Stretch = stretchX;
            int ni = 0;
            for (int k = 0; k < pitches.Count; k++)
            {
                string? glyph = null;
                double offset = 0;
                int at = needed.FindIndex(n => n.Index == k);
                if (at >= 0)
                {
                    glyph = needed[at].Glyph;
                    int column = columns[needed.OrderByDescending(n => n.Index).ToList().FindIndex(n => n.Index == k)];
                    offset = -(column + 1) * (Bravura.Get("accidentalSharp").Advance + 0.2) - 0.1 + Math.Min(0, shifts[k]);
                }
                placed.Notes.Add((pitches[k], shifts[k], glyph, offset));
                ni++;
            }
            fixedX = placed.Fixed + head + (shifts.Any(s => s > 0) ? head - Stem : 0) + (item.Dotted ? 0.9 : 0.1);
            stretchX += Spacing(item.Length);
            result.Items.Add(placed);
        }
        result.FixedWidth = fixedX + 0.6;
        result.StretchWidth = stretchX;
        BeamNotes(result);
        return result;
    }

    private static double RestWidth(NotatedItem item) => Bravura.Get(RestGlyph(item.Value)).Advance;

    private static string RestGlyph(int value) => value switch { 16 => "restWhole", 8 => "restHalf", 4 => "restQuarter", 2 => "rest8th", _ => "rest16th" };

    private static string AccidentalGlyph(int alter) => alter switch { 2 => "accidentalDoubleSharp", 1 => "accidentalSharp", -1 => "accidentalFlat", -2 => "accidentalDoubleFlat", _ => "accidentalNatural" };

    // ------------------------------------------------------------------ beams

    /// <summary>Gathers the eighth and sixteenth notes of a beat into beams, and settles which way each group's stems go.</summary>
    private static void BeamNotes(PlacedMeasure m)
    {
        BeamGroup? current = null;
        int beat = m.Measure.Beat;
        foreach (var p in m.Items)
        {
            bool beamable = !p.Item.IsRest && p.Item.Flags > 0;
            if (!beamable) { current = null; continue; }
            if (current is null || p.Item.Start / beat != current.Items[^1].Item.Start / beat)
            {
                current = new BeamGroup();
                m.Beams.Add(current);
            }
            current.Items.Add(p);
        }
        m.Beams.RemoveAll(g => g.Items.Count < 2);
        foreach (var g in m.Beams)
        {
            // The stems of a beamed group go the way the notes farthest from the middle line ask.
            int far = g.Items.SelectMany(i => i.Notes).Select(n => n.Pitch.Diatonic - MiddleLineDiatonic).MaxBy(Math.Abs);
            g.StemUp = far < 0;
            foreach (var i in g.Items) { i.StemUp = g.StemUp; i.Beamed = true; }
        }
    }

    // ------------------------------------------------------------------ drawing

    private static void DrawHeading(PageCanvas canvas, ScoreOptions o)
    {
        double width = o.PageWidth;
        if (o.Title.Length > 0)
        {
            var style = new TextStyle { Font = StandardFont.Times, Bold = true, Size = 22 };
            canvas.Text((width - canvas.MeasureText(o.Title, style)) / 2, o.Margin + 20, o.Title, style);
        }
        if (o.Subtitle.Length > 0)
        {
            var style = new TextStyle { Font = StandardFont.Times, Size = 11 };
            canvas.Text(width - o.Margin - canvas.MeasureText(o.Subtitle, style), o.Margin + 38, o.Subtitle, style);
        }
    }

    private static void DrawSystem(PageCanvas canvas, SystemPlan system, IReadOnlyList<Measure> all, ScoreOptions o, double left, double right)
    {
        double ss = o.StaffSpace, top = system.StaffTop;
        double Y(int steps) => StepY(top, ss, steps);

        // The staff.
        double lineEnd = left + system.PrefixWidth * ss + system.Measures.Sum(m => m.FixedWidth * ss + m.StretchWidth * ss * system.Scale);
        for (int l = 0; l < 5; l++) canvas.Line(new PdfPoint(left, top + l * ss), new PdfPoint(lineEnd, top + l * ss), StaffLine * ss);
        canvas.Line(new PdfPoint(left + StaffLine * ss / 2, top), new PdfPoint(left + StaffLine * ss / 2, top + 4 * ss), BarLine * ss);

        // Clef, key signature, time signature.
        double x = left + 0.7 * ss;
        Place(canvas, "gClef", x, Y(2), ss);
        x += (Bravura.Get("gClef").Advance + 0.8) * ss;
        var first = system.Measures[0].Measure;
        if (first.KeySharps != 0)
        {
            int[] sharpSteps = [8, 5, 9, 6, 3, 7, 4];     // F C G D A E B, in steps above the bottom line
            int[] flatSteps = [4, 7, 3, 6, 2, 5, 1];      // B E A D G C F
            for (int k = 0; k < Math.Abs(first.KeySharps); k++)
            {
                Place(canvas, first.KeySharps > 0 ? "accidentalSharp" : "accidentalFlat", x, Y((first.KeySharps > 0 ? sharpSteps : flatSteps)[k]), ss);
                x += 1.15 * ss;
            }
            x += 0.6 * ss;
        }
        if (system.ShowTimeSignature)
        {
            x += 0.3 * ss;
            double width = Math.Max(DigitsWidth(first.Numerator), DigitsWidth(first.Denominator));
            DrawDigits(canvas, first.Numerator, x, Y(6), ss, width);
            DrawDigits(canvas, first.Denominator, x, Y(2), ss, width);
        }

        if (o.Tempo > 0 && ReferenceEquals(system.Measures[0].Measure, all[0]))
        {
            double tx = left + system.PrefixWidth * ss;
            Place(canvas, "noteQuarterUp", tx, top - 3.2 * ss, ss * 0.85);
            canvas.Text(tx + 1.6 * ss, top - 3.2 * ss + 0.15 * ss, $"= {Math.Round(o.Tempo)}", new TextStyle { Size = (float)(2.0 * ss), Bold = true });
        }

        // Bar number above the first bar of the line (not the very first).
        if (system.FirstMeasureNumber > 1 && system.Measures[0].Measure.Number > 1)
            canvas.Text(left, top - 1.2 * ss, system.FirstMeasureNumber.ToString(), new TextStyle { Size = (float)(1.6 * ss), Italic = true });

        // The bars.
        double barX = left + system.PrefixWidth * ss;
        var drawn = new List<(PlacedMeasure Bar, double X)>();
        foreach (var m in system.Measures)
        {
            drawn.Add((m, barX));
            double Xof(PlacedItem p) => barX + (p.Fixed + p.Stretch * system.Scale) * ss;
            double width = (m.FixedWidth + m.StretchWidth * system.Scale) * ss;
            if (m.MultiRest > 1) DrawMultiRest(canvas, m.MultiRest, barX, width, top, ss);
            foreach (var p in m.Items) DrawItem(canvas, p, m, Xof(p), top, ss, barX, width);
            foreach (var group in m.Beams) DrawBeam(canvas, group, m, Xof, top, ss);
            barX += width;
            bool last = m.LastNumber == all[^1].Number;
            if (last)
            {
                canvas.Line(new PdfPoint(barX - 0.9 * ss, top), new PdfPoint(barX - 0.9 * ss, top + 4 * ss), BarLine * ss);
                canvas.Line(new PdfPoint(barX - 0.25 * ss, top), new PdfPoint(barX - 0.25 * ss, top + 4 * ss), 0.5 * ss);
            }
            else canvas.Line(new PdfPoint(barX, top), new PdfPoint(barX, top + 4 * ss), BarLine * ss);
        }

        // Ties, now that the places are known (a tie may run into the next bar or off the end of the line).
        for (int b = 0; b < drawn.Count; b++)
        {
            var (bar, bx) = drawn[b];
            for (int k = 0; k < bar.Items.Count; k++)
            {
                var p = bar.Items[k];
                if (!p.Item.TiedToNext || p.Item.IsRest) continue;
                double x1 = bx + (p.Fixed + p.Stretch * system.Scale) * ss;
                PlacedItem? next = k + 1 < bar.Items.Count ? bar.Items[k + 1] : b + 1 < drawn.Count ? drawn[b + 1].Bar.Items.FirstOrDefault() : null;
                double x2 = next is null ? barX : (k + 1 < bar.Items.Count ? bx : drawn[b + 1].X) + (next.Fixed + next.Stretch * system.Scale) * ss;
                DrawTies(canvas, p, x1, x2, top, ss, next is null);
            }
        }
    }

    /// <summary>Several bars of rest as a thick bar across the middle line with a stroke at each end and the number above.</summary>
    private static void DrawMultiRest(PageCanvas canvas, int bars, double barX, double width, double top, double ss)
    {
        double x1 = barX + 1.2 * ss, x2 = barX + width - 1.2 * ss, mid = top + 2 * ss;
        canvas.Rectangle(x1, mid - 0.25 * ss, x2 - x1, 0.5 * ss, PdfColor.Black);
        canvas.Line(new PdfPoint(x1, mid - ss), new PdfPoint(x1, mid + ss), 0.16 * ss);
        canvas.Line(new PdfPoint(x2, mid - ss), new PdfPoint(x2, mid + ss), 0.16 * ss);
        DrawDigits(canvas, bars, barX + (width - DigitsWidth(bars) * ss) / 2, top - 1.4 * ss, ss, DigitsWidth(bars));
    }

    private static void DrawDigits(PageCanvas canvas, int number, double x, double y, double ss, double width)
    {
        string digits = number.ToString();
        double w = digits.Sum(d => Bravura.Get("timeSig" + d).Advance);
        double at = x + (width - w) / 2 * ss;
        foreach (char d in digits)
        {
            var g = Bravura.Get("timeSig" + d);
            canvas.PlacedPath(g.Steps, Ink, ss, at, y);
            at += g.Advance * ss;
        }
    }

    private static void Place(PageCanvas canvas, string glyph, double x, double y, double ss) =>
        canvas.PlacedPath(Bravura.Get(glyph).Steps, Ink, ss, x, y);

    private static void DrawItem(PageCanvas canvas, PlacedItem p, PlacedMeasure m, double x, double top, double ss, double barX, double barWidth)
    {
        var item = p.Item;
        double head = Bravura.Get("noteheadBlack").Advance;
        if (item.IsRest)
        {
            // A rest for a whole bar is the whole rest, in any time signature; an upbeat, shorter than a bar, is written as the rest it is.
            bool whole = item.Length == m.Measure.Numerator * 16 / m.Measure.Denominator;
            string name = whole ? "restWhole" : RestGlyph(item.Value);
            int restSteps = name == "restWhole" ? 6 : 4;   // a whole rest hangs from the fourth line; the others are centred on the middle line
            double rx = whole ? barX + (barWidth - Bravura.Get(name).Advance * ss) / 2 : x;   // a rest for the whole bar is centred in it
            Place(canvas, name, rx, StepY(top, ss, restSteps), ss);
            if (item.Dotted) Place(canvas, "augmentationDot", rx + Bravura.Get(name).Advance * ss + 0.3 * ss, StepY(top, ss, 5), ss);
            return;
        }

        string headGlyph = item.Value >= 16 ? "noteheadWhole" : item.Value >= 8 ? "noteheadHalf" : "noteheadBlack";
        double headWidth = Bravura.Get(headGlyph).Advance;
        double stemX = p.StemUp ? x + (headWidth - Stem) * ss : x + 0 * ss;       // the stem's left edge
        var steps = p.Notes.Select(n => n.Pitch.Diatonic - BottomLineDiatonic).ToList();
        int lowSteps = steps.Min(), highSteps = steps.Max();

        foreach (var (pitch, shift, accidental, offset) in p.Notes)
        {
            int s = pitch.Diatonic - BottomLineDiatonic;
            double y = StepY(top, ss, s);
            // Ledger lines for notes outside the staff, as far as the note.
            if (s <= -2) for (int l = -2; l >= s - (s % 2 == 0 ? 0 : 1); l -= 2) Ledger(canvas, x + shift * ss, StepY(top, ss, l), headWidth, ss);
            if (s >= 10) for (int l = 10; l <= s - (s % 2 == 0 ? 0 : 1); l += 2) Ledger(canvas, x + shift * ss, StepY(top, ss, l), headWidth, ss);
            Place(canvas, headGlyph, x + shift * ss, y, ss);
            if (accidental is not null) Place(canvas, accidental, x + offset * ss, y, ss);
            if (item.Dotted)
            {
                double dotY = s % 2 == 0 ? StepY(top, ss, s + 1) : y; // in the space above a line
                Place(canvas, "augmentationDot", x + (headWidth + 0.35) * ss + Math.Max(0, shift) * ss, dotY, ss);
            }
        }

        if (item.Length >= 16) return;
        // The stem runs from the head at one end of the chord to its end, a stem length beyond the other.
        double yTop = StepY(top, ss, highSteps), yBottom = StepY(top, ss, lowSteps);
        double yStemEnd;
        if (p.Beamed) yStemEnd = double.NaN;  // a beamed stem is drawn with its beam
        else
        {
            yStemEnd = p.StemUp ? yTop - StemLength * ss : yBottom + StemLength * ss;
            // A stem for a note far outside the staff reaches the middle line, at least.
            double middle = StepY(top, ss, 4);
            if (p.StemUp && yStemEnd > middle) yStemEnd = middle;
            if (!p.StemUp && yStemEnd < middle) yStemEnd = middle;
            canvas.Line(new PdfPoint(stemX + Stem * ss / 2, p.StemUp ? yBottom : yTop), new PdfPoint(stemX + Stem * ss / 2, yStemEnd), Stem * ss);
            if (item.Flags > 0) Place(canvas, $"flag{(item.Flags == 1 ? "8th" : "16th")}{(p.StemUp ? "Up" : "Down")}", stemX, yStemEnd, ss);
        }
    }

    private static void Ledger(PageCanvas canvas, double x, double y, double headWidth, double ss) =>
        canvas.Line(new PdfPoint(x - LedgerExtension * ss, y), new PdfPoint(x + (headWidth + LedgerExtension) * ss, y), LedgerLine * ss);

    private static void DrawBeam(PageCanvas canvas, BeamGroup g, PlacedMeasure m, Func<PlacedItem, double> xOf, double top, double ss)
    {
        double head = Bravura.Get("noteheadBlack").Advance;
        double StemX(PlacedItem p) => g.StemUp ? xOf(p) + (head - Stem) * ss : xOf(p);
        double NoteEnd(PlacedItem p) => g.StemUp ? StepY(top, ss, p.Notes.Max(n => n.Pitch.Diatonic - BottomLineDiatonic)) : StepY(top, ss, p.Notes.Min(n => n.Pitch.Diatonic - BottomLineDiatonic));
        double NoteStart(PlacedItem p) => g.StemUp ? StepY(top, ss, p.Notes.Min(n => n.Pitch.Diatonic - BottomLineDiatonic)) : StepY(top, ss, p.Notes.Max(n => n.Pitch.Diatonic - BottomLineDiatonic));

        // The beam runs from the first stem's end to the last's, slanting at most a staff space, then is moved out until
        // every stem is at least two and a half staff spaces long.
        var first = g.Items[0]; var last = g.Items[^1];
        double x1 = StemX(first), x2 = StemX(last);
        double y1 = NoteEnd(first) + (g.StemUp ? -StemLength : StemLength) * ss;
        double y2 = NoteEnd(last) + (g.StemUp ? -StemLength : StemLength) * ss;
        double slant = Math.Clamp(y2 - y1, -ss, ss);
        double mid = (y1 + y2) / 2;
        y1 = mid - slant / 2; y2 = mid + slant / 2;
        double Line(double x) => x2 == x1 ? y1 : y1 + (y2 - y1) * (x - x1) / (x2 - x1);
        double move = 0;
        foreach (var p in g.Items)
        {
            double beamAt = Line(StemX(p)), reach = Math.Abs(beamAt - NoteEnd(p)), need = 2.5 * ss;
            if (reach < need) move = Math.Max(move, need - reach);
        }
        y1 += g.StemUp ? -move : move; y2 += g.StemUp ? -move : move;

        // Stems.
        foreach (var p in g.Items)
            canvas.Line(new PdfPoint(StemX(p) + Stem * ss / 2, NoteStart(p)), new PdfPoint(StemX(p) + Stem * ss / 2, Line(StemX(p))), Stem * ss);

        // Beams: a primary beam across the group, a second where the notes are sixteenths.
        void Bar(double xa, double xb, int level)
        {
            double offset = level * (BeamThickness + BeamGap) * ss * (g.StemUp ? 1 : -1);
            double a = Line(xa) + offset, b = Line(xb) + offset, t = BeamThickness * ss;
            // The beam hangs from its edge on the stem side: for up stems, its top edge is at the stem end.
            double sign = g.StemUp ? 1 : -1;
            canvas.Path([
                new('M', new PdfPoint(xa, a)), new('L', new PdfPoint(xb + Stem * ss, b)),
                new('L', new PdfPoint(xb + Stem * ss, b + sign * t)), new('L', new PdfPoint(xa, a + sign * t)), new('Z', default)],
                new PathStyle { Fill = PdfColor.Black });
        }
        Bar(x1, x2, 0);
        // Second beams over runs of sixteenths; a lone sixteenth gets a short stub.
        for (int i = 0; i < g.Items.Count;)
        {
            if (g.Items[i].Item.Flags < 2) { i++; continue; }
            int j = i;
            while (j + 1 < g.Items.Count && g.Items[j + 1].Item.Flags >= 2) j++;
            double xa = StemX(g.Items[i]), xb = StemX(g.Items[j]);
            if (i == j)
            {
                double stub = 1.1 * ss;
                if (i == g.Items.Count - 1) { xb = xa; xa -= stub; } else xb = xa + stub;
            }
            Bar(xa, xb, 1);
            i = j + 1;
        }
    }

    private static void DrawTies(PageCanvas canvas, PlacedItem p, double x1, double x2, double top, double ss, bool offLine)
    {
        double head = Bravura.Get("noteheadBlack").Advance;
        // A tie goes on the side away from the stem.
        bool above = !p.StemUp;
        foreach (var n in p.Notes)
        {
            int s = n.Pitch.Diatonic - BottomLineDiatonic;
            double y = StepY(top, ss, s) + (above ? -0.45 * ss : 0.45 * ss);
            double a = x1 + (head + 0.15 + Math.Max(0, n.Shift / ss)) * ss, b = offLine ? x2 - 0.4 * ss : x2 - 0.2 * ss;
            if (b - a < 1.2 * ss) b = a + 1.2 * ss;
            double h = Math.Clamp((b - a) * 0.12, 0.6 * ss, 1.2 * ss) * (above ? -1 : 1), t = 0.22 * ss * (above ? 1 : -1);
            double w = b - a;
            canvas.Path([
                new('M', new PdfPoint(a, y)),
                new('C', new PdfPoint(b, y), new PdfPoint(a + w * 0.2, y + h * 1.33), new PdfPoint(b - w * 0.2, y + h * 1.33)),
                new('C', new PdfPoint(a, y), new PdfPoint(b - w * 0.2, y + h * 1.33 + t), new PdfPoint(a + w * 0.2, y + h * 1.33 + t)),
                new('Z', default)], new PathStyle { Fill = PdfColor.Black });
        }
    }
}
