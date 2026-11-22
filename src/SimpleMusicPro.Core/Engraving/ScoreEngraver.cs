using PdfEditor.Core;
using SimpleMusicPro.Glyphs;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Engraving;

/// <summary>What a printed score looks like.</summary>
public sealed record ScoreOptions
{
    public double PageWidth { get; init; } = 595.28;
    public double PageHeight { get; init; } = 841.89;
    public double Margin { get; init; } = 42;
    /// <summary>The gap between two lines of the staff, in points. Everything else is drawn in proportion to it.</summary>
    public double StaffSpace { get; init; } = 5.6;
    /// <summary>
    /// Whether several empty bars in a row are printed as one multi-bar rest. True for a finished score; false
    /// while editing, so that every bar is there to put strums and chords in.
    /// </summary>
    public bool JoinEmptyBars { get; init; } = true;

    // Kept for the simple overload that takes bars alone.
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public double Tempo { get; init; }
}

/// <summary>Which staff of the stack something belongs to.</summary>
public enum LaneKind { Rhythm, Voice, Chords }

/// <summary>Where a bar was drawn: its page, its place, and where in it each moment that has something at it lies.</summary>
/// <param name="Bar">The bar's index in the score's list of bars.</param>
/// <param name="Columns">For each place in the bar (in sixteenths) that has a note, rest or strum in some staff, the x it was drawn at.</param>
public sealed record BarMap(int Page, int Bar, int Length, double X, double Width, IReadOnlyList<(int Position, double X)> Columns)
{
    /// <summary>The x of a place in the bar: a column's own x, or a point between the columns either side of it.</summary>
    public double XOf(int position)
    {
        if (Columns.Count == 0) return X + Width * position / Math.Max(1, Length);
        var before = Columns.LastOrDefault(c => c.Position <= position);
        var after = Columns.FirstOrDefault(c => c.Position > position);
        if (before == default) return Columns[0].X;
        if (before.Position == position) return before.X;
        double endX = after == default ? X + Width - 1.2 : after.X;
        int endPos = after == default ? Length : after.Position;
        return before.X + (endX - before.X) * (position - before.Position) / Math.Max(1, endPos - before.Position);
    }
}

/// <summary>Where a staff of the stack was drawn on a line: the band of the page it takes, top and bottom.</summary>
public sealed record LaneBand(int Page, int FirstBar, int LastBar, LaneKind Lane, double Top, double Bottom);

/// <summary>A note, rest or strum as drawn, for finding it again under a click.</summary>
public sealed record ItemMap(int Page, LaneKind Lane, int Bar, int Position, int Length, PdfRect Bounds);

/// <summary>A score drawn as a PDF, with a map of where everything landed.</summary>
public sealed class EngravedScore : IDisposable
{
    public required PdfDocument Pdf { get; init; }
    public required IReadOnlyList<BarMap> Bars { get; init; }
    public required IReadOnlyList<LaneBand> Bands { get; init; }
    public required IReadOnlyList<ItemMap> Items { get; init; }
    public void Dispose() => Pdf.Dispose();
}

/// <summary>
/// Draws a score as a printed page: each line a stack of staves (a rhythm staff of slashes above a voice staff
/// with its clef, key and time signature, noteheads, stems, flags and beams, accidentals, dots, ties and rests),
/// with the staves of a line spaced together so that what happens at the same moment stands in one column.
/// The symbols are Bravura's, drawn as vector shapes.
/// </summary>
public static class ScoreEngraver
{
    // Sizes as Bravura's designers give them, in staff spaces.
    private const double StaffLine = 0.13, Stem = 0.12, BarLine = 0.16, LedgerLine = 0.16, LedgerExtension = 0.4;
    private const double BeamThickness = 0.5, BeamGap = 0.25, StemLength = 3.5;
    private const int BottomLineDiatonic = 4 * 7 + 2; // E4: the bottom line of a treble staff
    private const int MiddleLineDiatonic = 34;         // B4
    private const double SlashWidth = 1.1;             // a rhythm slash, in staff spaces
    // A chord diagram, in staff spaces: the gap between strings, a fret's height, the name's size, the whole row's
    // height, and where its middle stands from a column's x (over the middle of a notehead or slash).
    private const double StringGap = 1.0, FretHeight = 1.2, ChordNameSize = 2.0, ChordRowHeight = 11.5, ChordCentre = 0.6;

    private static readonly PathStyle Ink = new() { Fill = PdfColor.Black };

    /// <summary>A score of one voice staff, made into a PDF (kept for callers that have bars alone).</summary>
    public static PdfDocument Engrave(IReadOnlyList<Measure> measures, ScoreOptions options)
    {
        var score = new ScoreDocument { Bars = measures, Title = options.Title, Subtitle = options.Subtitle, Tempo = options.Tempo };
        return Engrave(score, options).Pdf;
    }

    /// <summary>A score made into a PDF, with the map of where its bars, staves and notes landed.</summary>
    public static EngravedScore Engrave(ScoreDocument score, ScoreOptions options)
    {
        var doc = PdfDocument.Create();
        double ss = options.StaffSpace;
        double left = options.Margin, right = options.PageWidth - options.Margin;
        bool rhythm = score.Rhythm is not null, voice = score.ShowVoice || !rhythm;

        var lines = Plan(score, voice, ss, right - left, options.JoinEmptyBars);
        // Vertical layout of a line: the chord diagrams (if any) above the rhythm staff (if any) above the voice staff.
        double chordHeight = score.Chords is not null ? ChordRowHeight * ss : 0;
        double rhythmHeight = rhythm ? 6.5 * ss : 0, voiceHeight = voice ? 11.5 * ss : 0;
        double lineHeight = 3.5 * ss + chordHeight + rhythmHeight + voiceHeight + 1.5 * ss;
        double headingHeight = HasHeading(score) ? 64 : 0;
        var pages = new List<List<LinePlan>> { new() };
        double y = options.Margin + headingHeight;
        foreach (var line in lines)
        {
            if (y + lineHeight > options.PageHeight - options.Margin && pages[^1].Count > 0) { pages.Add([]); y = options.Margin; }
            line.Top = y;
            line.ChordTop = y + 3.5 * ss;
            line.RhythmBase = y + 3.5 * ss + chordHeight + 4.2 * ss;           // the middle of the slashes
            line.StaffTop = y + 3.5 * ss + chordHeight + rhythmHeight + (rhythm ? 2.0 * ss : 0);
            pages[^1].Add(line);
            y += lineHeight;
        }

        var bars = new List<BarMap>();
        var bands = new List<LaneBand>();
        var items = new List<ItemMap>();
        for (int p = 0; p < pages.Count; p++)
        {
            doc.AddPage(options.PageWidth, options.PageHeight);
            int page = doc.PageCount - 1;
            var onPage = pages[p];
            doc.Draw(page, canvas =>
            {
                if (page == 0) DrawHeading(canvas, score, options);
                foreach (var line in onPage)
                    DrawLine(canvas, page, line, score, voice, rhythm, options, left, bars, bands, items);
            });
        }
        return new EngravedScore { Pdf = doc, Bars = bars, Bands = bands, Items = items };
    }

    private static bool HasHeading(ScoreDocument s) => s.Title.Length > 0 || s.Subtitle.Length > 0 || s.Tempo > 0;

    // ------------------------------------------------------------------ placing things along a bar

    /// <summary>A note, chord or rest of the voice staff placed in a bar.</summary>
    private sealed class PlacedItem
    {
        public required NotatedItem Item { get; init; }
        public double Fixed, Stretch;
        public bool StemUp, Beamed;
        public List<(Pitch Pitch, double Shift, string? Accidental, double AccidentalOffset)> Notes = [];
        public double Pre, Body;
    }

    /// <summary>A strum or rest of the rhythm staff placed in a bar.</summary>
    private sealed class PlacedHit
    {
        public int Position, Length;
        public bool Rest, Accent, Beamed;
        /// <summary>One part of a strum written as tied lengths, with more of it after.</summary>
        public bool TiedToNext;
        public double Fixed, Stretch;
        public int Flags => Length switch { 1 => 2, 2 or 3 => 1, _ => 0 };
        public bool Dotted => Length is 3 or 6 or 12;
        public int Value => Dotted ? Length * 2 / 3 : Length;
    }

    private sealed class PlacedBar
    {
        public required Measure Measure { get; init; }
        public int Index;
        public List<PlacedItem> Items { get; } = [];
        public List<PlacedHit> Hits { get; } = [];
        public List<(int Position, double Fixed, double Stretch)> Columns { get; } = [];
        public double FixedWidth, StretchWidth;
        public double NaturalWidth => FixedWidth + StretchWidth;
        public List<List<PlacedItem>> Beams { get; } = [];
        public List<bool> BeamUp { get; } = [];
        public List<List<PlacedHit>> HitBeams { get; } = [];
        public List<ChordMark> Chords { get; } = [];
        /// <summary>More than 1 for several empty bars in a row, written as one.</summary>
        public int MultiRest = 1;
        public int LastIndex => Index + MultiRest - 1;
    }

    private sealed class LinePlan
    {
        public List<PlacedBar> Bars { get; } = [];
        public bool ShowTimeSignature;
        public double PrefixWidth;
        public double Scale = 1;
        public double Top, ChordTop, RhythmBase, StaffTop;
    }

    /// <summary>How much room a stretch of time takes after a moment, beyond what is drawn there, in staff spaces.</summary>
    private static double Spacing(int sixteenths)
    {
        (double At, double Room)[] table = [(1, 1.5), (2, 2.1), (4, 3.1), (8, 4.6), (16, 6.2), (32, 7.6)];
        double g = Math.Max(1, sixteenths), log = Math.Log2(g);
        for (int i = 0; i + 1 < table.Length; i++)
        {
            double a = Math.Log2(table[i].At), b = Math.Log2(table[i + 1].At);
            if (log <= b) return table[i].Room + (table[i + 1].Room - table[i].Room) * (log - a) / (b - a);
        }
        return table[^1].Room;
    }

    private static double StepY(double staffTop, double ss, int steps) => staffTop + 4 * ss - steps * ss / 2;

    private static List<LinePlan> Plan(ScoreDocument score, bool voice, double ss, double width, bool join)
    {
        var all = score.Bars;
        var placed = new List<PlacedBar>();
        bool Empty(int k) => all[k].IsWholeRest && (score.Rhythm is null || !score.Rhythm.InBar(k).Any()) && (score.Chords is null || !score.Chords.InBar(k).Any());
        for (int k = 0; k < all.Count; k++)
        {
            var m = all[k];
            int run = 1;
            if (join && Empty(k) && !m.Pickup)
                while (k + run < all.Count && Empty(k + run) && all[k + run].Numerator == m.Numerator && all[k + run].Denominator == m.Denominator && all[k + run].KeySharps == m.KeySharps) run++;
            if (run < 2) { placed.Add(PlaceBar(m, k, voice, score.Rhythm, score.Chords)); continue; }
            placed.Add(new PlacedBar { Measure = m, Index = k, MultiRest = run, FixedWidth = 10, StretchWidth = 0 });
            k += run - 1;
        }

        var lines = new List<LinePlan>();
        int i = 0;
        while (i < placed.Count)
        {
            var line = new LinePlan();
            var m0 = placed[i].Measure;
            bool timeChanged = i == 0 || m0.Numerator != placed[i - 1].Measure.Numerator || m0.Denominator != placed[i - 1].Measure.Denominator;
            line.ShowTimeSignature = timeChanged;
            line.PrefixWidth = PrefixWidth(voice ? m0.KeySharps : 0, timeChanged ? m0 : null, voice);
            double available = width - line.PrefixWidth * ss, used = 0;
            while (i < placed.Count)
            {
                double w = placed[i].NaturalWidth * ss;
                if (line.Bars.Count > 0 && used + w > available) break;
                line.Bars.Add(placed[i]);
                used += w;
                i++;
            }
            double fixedTotal = line.Bars.Sum(b => b.FixedWidth) * ss, stretchTotal = line.Bars.Sum(b => b.StretchWidth) * ss;
            line.Scale = stretchTotal <= 0 ? 1 : Math.Clamp((available - fixedTotal) / stretchTotal, 0.5, i >= placed.Count ? 1.35 : 3);
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>The width of clef, key signature and, where wanted, time signature at the start of a line, in staff spaces.</summary>
    private static double PrefixWidth(int keySharps, Measure? timeFrom, bool clef)
    {
        double w = clef ? 0.7 + Bravura.Get("gClef").Advance + 0.8 : 1.0;
        if (keySharps != 0) w += Math.Abs(keySharps) * 1.15 + 0.6;
        if (timeFrom is not null) w += 0.3 + Math.Max(DigitsWidth(timeFrom.Numerator), DigitsWidth(timeFrom.Denominator)) + 1.0;
        return w;
    }

    private static double DigitsWidth(int number) => number.ToString().Sum(d => Bravura.Get("timeSig" + d).Advance);

    private static int NominalLength(Measure m) => m.Numerator * 16 / m.Denominator;

    /// <summary>Places a bar: what each staff has in it, and one column for each moment something starts in any staff.</summary>
    private static PlacedBar PlaceBar(Measure measure, int index, bool voice, RhythmLane? rhythm, ChordLane? chords)
    {
        var bar = new PlacedBar { Measure = measure, Index = index };
        double head = Bravura.Get("noteheadBlack").Advance;
        int nominal = NominalLength(measure);

        if (voice)
        {
            var state = new Dictionary<int, int>(); // the sharp or flat each place on the staff has in this bar
            var keyAlter = Pitch.KeyAlter(measure.KeySharps);
            int Current(Pitch p) => state.TryGetValue(p.Diatonic, out int a) ? a : keyAlter[p.Step];
            double accidentalStep = Bravura.Get("accidentalSharp").Advance + 0.2;
            foreach (var item in measure.Items)
            {
                var placed = new PlacedItem { Item = item };
                if (item.IsRest)
                {
                    // A whole-bar rest is centred in the bar later and takes no room of its own here.
                    placed.Body = item.Length == nominal ? 0 : Bravura.Get(RestGlyph(item.Value)).Advance + (item.Dotted ? 0.8 : 0);
                    bar.Items.Add(placed);
                    continue;
                }
                var pitches = item.Pitches.OrderBy(p => p.Diatonic).ThenBy(p => p.Alter).ToList();
                int low = pitches[0].Diatonic, high = pitches[^1].Diatonic;
                placed.StemUp = item.Length < 16 && (MiddleLineDiatonic - low) > (high - MiddleLineDiatonic);
                // Seconds in a chord: the head that touches the one before goes to the other side of the stem.
                var shifts = new double[pitches.Count];
                if (placed.StemUp) { for (int k = 1; k < pitches.Count; k++) if (pitches[k].Diatonic - pitches[k - 1].Diatonic == 1 && shifts[k - 1] == 0) shifts[k] = head - Stem; }
                else { for (int k = pitches.Count - 2; k >= 0; k--) if (pitches[k + 1].Diatonic - pitches[k].Diatonic == 1 && shifts[k + 1] == 0) shifts[k] = -(head - Stem); }
                // Accidentals, stacked in columns to the left, highest first.
                var column = new int[pitches.Count];
                var glyphs = new string?[pitches.Count];
                var lastInColumn = new List<int>();
                for (int k = pitches.Count - 1; k >= 0; k--)
                {
                    if (pitches[k].Alter == Current(pitches[k])) continue;
                    glyphs[k] = AccidentalGlyph(pitches[k].Alter);
                    state[pitches[k].Diatonic] = pitches[k].Alter;
                    int c = 0;
                    while (c < lastInColumn.Count && Math.Abs(pitches[k].Diatonic - lastInColumn[c]) < 6) c++;
                    if (c == lastInColumn.Count) lastInColumn.Add(int.MinValue);
                    lastInColumn[c] = pitches[k].Diatonic;
                    column[k] = c;
                }
                double leftExtra = shifts.Any(s => s < 0) ? head - Stem : 0;
                for (int k = 0; k < pitches.Count; k++)
                {
                    double offset = glyphs[k] is null ? 0 : -(column[k] + 1) * accidentalStep - 0.1 + Math.Min(0, shifts[k]);
                    placed.Notes.Add((pitches[k], shifts[k], glyphs[k], offset));
                }
                placed.Pre = (lastInColumn.Count == 0 ? 0 : lastInColumn.Count * accidentalStep + 0.1) + leftExtra;
                placed.Body = head + (shifts.Any(s => s > 0) ? head - Stem : 0) + (item.Dotted ? 0.9 : 0.1);
                bar.Items.Add(placed);
            }
        }

        if (rhythm is not null)
        {
            int beat = measure.Beat;
            foreach (var hit in rhythm.InBar(index))
            {
                // Each strum is one slash of its own length, wherever it starts (not split at the beats, as the
                // melody's notes are). Only a length there is no single symbol for (a strum cut short by a later one)
                // is written as the lengths that make it up, tied.
                int length = Math.Min(hit.Length, measure.Length - hit.Position);
                List<(int Start, int Length)> parts = RhythmLane.Lengths.Contains(length)
                    ? [(hit.Position, length)]
                    : Quantizer.Split(hit.Position, length, measure.Length, beat);
                for (int k = 0; k < parts.Count; k++)
                    bar.Hits.Add(new PlacedHit { Position = parts[k].Start, Length = parts[k].Length, Rest = hit.Rest, Accent = hit.Accent && k == 0, TiedToNext = !hit.Rest && k < parts.Count - 1 });
            }
        }

        // One column for each moment something starts, in either staff; its room is the widest thing there.
        var positions = bar.Items.Where(p => !(p.Item.IsRest && p.Item.Length == nominal)).Select(p => p.Item.Start)
            .Concat(bar.Hits.Select(h => h.Position)).Distinct().Order().ToList();
        // The rhythm staff keeps room where a strum can still go: each beat where nothing sounds, and the end of each
        // strum when nothing follows it at once.
        if (rhythm is not null)
        {
            bool Free(int pos) => pos < measure.Length && !bar.Hits.Any(h => h.Position <= pos && pos < h.Position + h.Length);
            var open = Enumerable.Range(0, (measure.Length + measure.Beat - 1) / measure.Beat).Select(b => b * measure.Beat)
                .Concat(bar.Hits.Select(h => h.Position + h.Length)).Where(Free);
            positions = positions.Concat(open).Distinct().Order().ToList();
        }
        // A chord stands over a moment of its own (drawn centred over the notes there).
        if (chords is not null)
            foreach (var mark in chords.InBar(index).Where(m => m.Position < measure.Length)) bar.Chords.Add(mark);
        positions = positions.Concat(bar.Chords.Select(c => c.Position)).Distinct().Order().ToList();
        if (positions.Count == 0) positions.Add(0);
        double fixedX = 1.0, stretchX = 0;
        double? chordRight = null;   // where the last chord's diagram (or name) ends, with a little room
        for (int c = 0; c < positions.Count; c++)
        {
            int pos = positions[c];
            double pre = bar.Items.Where(p => p.Item.Start == pos).Select(p => p.Pre).DefaultIfEmpty(0).Max();
            double body = bar.Items.Where(p => p.Item.Start == pos).Select(p => p.Body)
                .Concat(bar.Hits.Where(h => h.Position == pos).Select(h => h.Rest ? Bravura.Get(RestGlyph(h.Value)).Advance : SlashWidth + (h.Dotted ? 0.7 : 0)))
                .DefaultIfEmpty(0).Max();
            double colFixed = fixedX + pre;
            if (bar.Chords.FirstOrDefault(m => m.Position == pos) is { } chord)
            {
                // Diagrams never touch: the bar is made wider where they stand close together.
                var (leftSide, rightSide) = ChordExtent(chord.Name);
                double leftEdge = colFixed + ChordCentre - leftSide, limit = chordRight ?? 0.4;
                if (leftEdge < limit) colFixed += limit - leftEdge;
                chordRight = colFixed + ChordCentre + rightSide + 0.8;
            }
            bar.Columns.Add((pos, colFixed, stretchX));
            fixedX = colFixed + body;
            int next = c + 1 < positions.Count ? positions[c + 1] : measure.Length;
            stretchX += Spacing(next - pos);
        }
        foreach (var p in bar.Items)
        {
            var col = bar.Columns.FirstOrDefault(c => c.Position == p.Item.Start);
            (p.Fixed, p.Stretch) = (col.Fixed, col.Stretch);
        }
        foreach (var h in bar.Hits)
        {
            var col = bar.Columns.First(c => c.Position == h.Position);
            (h.Fixed, h.Stretch) = (col.Fixed, col.Stretch);
        }
        fixedX = Math.Max(fixedX, (chordRight ?? 0) - 0.6);
        bar.FixedWidth = fixedX + 0.6;
        bar.StretchWidth = stretchX;
        BeamNotes(bar);
        return bar;
    }

    private static string RestGlyph(int value) => value switch { 16 => "restWhole", 8 => "restHalf", 4 => "restQuarter", 2 => "rest8th", _ => "rest16th" };

    private static string AccidentalGlyph(int alter) => alter switch { 2 => "accidentalDoubleSharp", 1 => "accidentalSharp", -1 => "accidentalFlat", -2 => "accidentalDoubleFlat", _ => "accidentalNatural" };

    /// <summary>Gathers the eighth and sixteenth notes of a beat into beams, in each staff, and settles the voice's stem directions.</summary>
    private static void BeamNotes(PlacedBar bar)
    {
        int beat = bar.Measure.Beat;
        List<PlacedItem>? current = null;
        foreach (var p in bar.Items)
        {
            if (p.Item.IsRest || p.Item.Flags == 0) { current = null; continue; }
            if (current is null || p.Item.Start / beat != current[^1].Item.Start / beat) bar.Beams.Add(current = []);
            current.Add(p);
        }
        bar.Beams.RemoveAll(g => g.Count < 2);
        foreach (var g in bar.Beams)
        {
            int far = g.SelectMany(i => i.Notes).Select(n => n.Pitch.Diatonic - MiddleLineDiatonic).MaxBy(Math.Abs);
            bool up = far < 0;
            bar.BeamUp.Add(up);
            foreach (var i in g) { i.StemUp = up; i.Beamed = true; }
        }

        // Eighth and sixteenth strums that follow one another within a beat are beamed together,
        // by their own timing, whatever the melody's notes do.
        List<PlacedHit>? run = null;
        foreach (var h in bar.Hits)
        {
            if (h.Rest || h.Flags == 0 || h.TiedToNext) { run = null; continue; }
            if (run is null || h.Position / beat != run[^1].Position / beat || run[^1].Position + run[^1].Length != h.Position) bar.HitBeams.Add(run = []);
            run.Add(h);
        }
        bar.HitBeams.RemoveAll(g => g.Count < 2);
        foreach (var g in bar.HitBeams) foreach (var h in g) h.Beamed = true;
    }

    // ------------------------------------------------------------------ drawing

    private static void DrawHeading(PageCanvas canvas, ScoreDocument s, ScoreOptions o)
    {
        if (s.Title.Length > 0)
        {
            var style = new TextStyle { Font = StandardFont.Times, Bold = true, Size = 22 };
            canvas.Text((o.PageWidth - canvas.MeasureText(s.Title, style)) / 2, o.Margin + 20, s.Title, style);
        }
        if (s.Subtitle.Length > 0)
        {
            var style = new TextStyle { Font = StandardFont.Times, Size = 11 };
            canvas.Text(o.PageWidth - o.Margin - canvas.MeasureText(s.Subtitle, style), o.Margin + 38, s.Subtitle, style);
        }
    }

    private static void DrawLine(PageCanvas canvas, int page, LinePlan line, ScoreDocument score, bool voice, bool rhythm, ScoreOptions o, double left,
                                 List<BarMap> barMaps, List<LaneBand> bands, List<ItemMap> itemMaps)
    {
        double ss = o.StaffSpace, top = line.StaffTop;
        double Y(int steps) => StepY(top, ss, steps);
        var first = line.Bars[0].Measure;
        double lineEnd = left + line.PrefixWidth * ss + line.Bars.Sum(b => (b.FixedWidth + b.StretchWidth * line.Scale) * ss);
        int firstBar = line.Bars[0].Index, lastBar = line.Bars[^1].LastIndex;

        if (voice)
        {
            for (int l = 0; l < 5; l++) canvas.Line(new PdfPoint(left, top + l * ss), new PdfPoint(lineEnd, top + l * ss), StaffLine * ss);
            canvas.Line(new PdfPoint(left + StaffLine * ss / 2, top), new PdfPoint(left + StaffLine * ss / 2, top + 4 * ss), BarLine * ss);
            double x = left + 0.7 * ss;
            Place(canvas, "gClef", x, Y(2), ss);
            x += (Bravura.Get("gClef").Advance + 0.8) * ss;
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
            if (line.ShowTimeSignature)
            {
                x += 0.3 * ss;
                double w = Math.Max(DigitsWidth(first.Numerator), DigitsWidth(first.Denominator));
                DrawDigits(canvas, first.Numerator, x, Y(6), ss, w);
                DrawDigits(canvas, first.Denominator, x, Y(2), ss, w);
            }
            bands.Add(new LaneBand(page, firstBar, lastBar, LaneKind.Voice, top - 3 * ss, top + 7 * ss));
        }
        if (rhythm)
        {
            bands.Add(new LaneBand(page, firstBar, lastBar, LaneKind.Rhythm, line.RhythmBase - 4.5 * ss, line.RhythmBase + 2.2 * ss));
            if (firstBar == 0 && score.Rhythm!.Label.Length > 0)
                canvas.Text(left, line.RhythmBase - 4.6 * ss, score.Rhythm.Label, new TextStyle { Size = (float)(1.8 * ss), Bold = true });
        }
        bool chords = score.Chords is not null;
        if (chords) bands.Add(new LaneBand(page, firstBar, lastBar, LaneKind.Chords, line.ChordTop, line.ChordTop + ChordRowHeight * ss));

        // The tempo mark stands above everything else on the first line, above the rhythm staff's name when there is one.
        double markTop = chords ? line.ChordTop - 1.0 * ss
            : rhythm ? line.RhythmBase - (score.Rhythm!.Label.Length > 0 ? 8.2 : 6.2) * ss : top - 3.2 * ss;
        if (score.Tempo > 0 && firstBar == 0)
        {
            double tx = left + line.PrefixWidth * ss;
            Place(canvas, "noteQuarterUp", tx, markTop, ss * 0.85);
            canvas.Text(tx + 1.6 * ss, markTop + 0.15 * ss, $"= {Math.Round(score.Tempo)}", new TextStyle { Size = (float)(2.0 * ss), Bold = true });
        }
        if (first.Number > 1)
            canvas.Text(left, (voice ? top : line.RhythmBase - 3 * ss) - 1.2 * ss, first.Number.ToString(), new TextStyle { Size = (float)(1.6 * ss), Italic = true });

        // The bars.
        double barX = left + line.PrefixWidth * ss;
        var drawn = new List<(PlacedBar Bar, double X)>();
        foreach (var b in line.Bars)
        {
            drawn.Add((b, barX));
            double Xof(double fix, double stretch) => barX + (fix + stretch * line.Scale) * ss;
            double width = (b.FixedWidth + b.StretchWidth * line.Scale) * ss;
            barMaps.Add(new BarMap(page, b.Index, b.Measure.Length, barX, width, b.Columns.Select(c => (c.Position, Xof(c.Fixed, c.Stretch))).ToList()));
            for (int extra = 1; extra < b.MultiRest; extra++) barMaps.Add(new BarMap(page, b.Index + extra, b.Measure.Length, barX, width, []));

            if (voice)
            {
                if (b.MultiRest > 1) DrawMultiRest(canvas, b.MultiRest, barX, width, top, ss);
                foreach (var p in b.Items)
                {
                    var bounds = DrawItem(canvas, p, b, Xof(p.Fixed, p.Stretch), top, ss, barX, width);
                    itemMaps.Add(new ItemMap(page, LaneKind.Voice, b.Index, p.Item.Start, p.Item.Length, bounds));
                }
                for (int g = 0; g < b.Beams.Count; g++) DrawBeam(canvas, b.Beams[g], b.BeamUp[g], p => Xof(p.Fixed, p.Stretch), top, ss);
                bool last = b.LastIndex == score.Bars.Count - 1;
                double end = barX + width;
                if (last)
                {
                    canvas.Line(new PdfPoint(end - 0.9 * ss, top), new PdfPoint(end - 0.9 * ss, top + 4 * ss), BarLine * ss);
                    canvas.Line(new PdfPoint(end - 0.25 * ss, top), new PdfPoint(end - 0.25 * ss, top + 4 * ss), 0.5 * ss);
                }
                else canvas.Line(new PdfPoint(end, top), new PdfPoint(end, top + 4 * ss), BarLine * ss);
            }
            if (rhythm)
            {
                foreach (var h in b.Hits)
                {
                    var bounds = DrawHit(canvas, h, Xof(h.Fixed, h.Stretch), line.RhythmBase, ss);
                    itemMaps.Add(new ItemMap(page, LaneKind.Rhythm, b.Index, h.Position, h.Length, bounds));
                }
                foreach (var g in b.HitBeams) DrawHitBeam(canvas, g, h => Xof(h.Fixed, h.Stretch), line.RhythmBase, ss);
                // A strum written as tied lengths: a tie below the slashes, from one part to the next.
                for (int k = 0; k + 1 < b.Hits.Count; k++)
                    if (b.Hits[k].TiedToNext)
                        DrawHitTie(canvas, Xof(b.Hits[k].Fixed, b.Hits[k].Stretch), Xof(b.Hits[k + 1].Fixed, b.Hits[k + 1].Stretch), line.RhythmBase, ss);
            }
            foreach (var mark in b.Chords)
            {
                var col = b.Columns.First(c => c.Position == mark.Position);
                var bounds = DrawChord(canvas, mark.Name, Xof(col.Fixed, col.Stretch) + ChordCentre * ss, line.ChordTop, ss);
                itemMaps.Add(new ItemMap(page, LaneKind.Chords, b.Index, mark.Position, 0, bounds));
            }
            barX += width;
        }

        // Ties of the voice staff, now that the places are known (one may run into the next bar or off the line).
        if (!voice) return;
        for (int b = 0; b < drawn.Count; b++)
        {
            var (bar, bx) = drawn[b];
            for (int k = 0; k < bar.Items.Count; k++)
            {
                var p = bar.Items[k];
                if (!p.Item.TiedToNext || p.Item.IsRest) continue;
                double x1 = bx + (p.Fixed + p.Stretch * line.Scale) * ss;
                PlacedItem? next = k + 1 < bar.Items.Count ? bar.Items[k + 1] : b + 1 < drawn.Count ? drawn[b + 1].Bar.Items.FirstOrDefault() : null;
                double x2 = next is null ? barX : (k + 1 < bar.Items.Count ? bx : drawn[b + 1].X) + (next.Fixed + next.Stretch * line.Scale) * ss;
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

    /// <summary>Draws a note, chord or rest of the voice staff; returns the box it takes, for clicking.</summary>
    private static PdfRect DrawItem(PageCanvas canvas, PlacedItem p, PlacedBar b, double x, double top, double ss, double barX, double barWidth)
    {
        var item = p.Item;
        if (item.IsRest)
        {
            bool whole = item.Length == NominalLength(b.Measure);
            string name = whole ? "restWhole" : RestGlyph(item.Value);
            int restSteps = name == "restWhole" ? 6 : 4;   // a whole rest hangs from the fourth line; the others are centred on the middle line
            double rx = whole ? barX + (barWidth - Bravura.Get(name).Advance * ss) / 2 : x;
            Place(canvas, name, rx, StepY(top, ss, restSteps), ss);
            if (item.Dotted) Place(canvas, "augmentationDot", rx + Bravura.Get(name).Advance * ss + 0.3 * ss, StepY(top, ss, 5), ss);
            return new PdfRect(rx - 0.3 * ss, top, Bravura.Get(name).Advance * ss + 0.6 * ss, 4 * ss);
        }

        string headGlyph = item.Value >= 16 ? "noteheadWhole" : item.Value >= 8 ? "noteheadHalf" : "noteheadBlack";
        double headWidth = Bravura.Get(headGlyph).Advance;
        double stemX = p.StemUp ? x + (headWidth - Stem) * ss : x;       // the stem's left edge
        int lowSteps = p.Notes.Min(n => n.Pitch.Diatonic) - BottomLineDiatonic, highSteps = p.Notes.Max(n => n.Pitch.Diatonic) - BottomLineDiatonic;

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
        double yTop = StepY(top, ss, highSteps), yBottom = StepY(top, ss, lowSteps);
        var bounds = new PdfRect(x - 0.3 * ss, yTop - 0.8 * ss, (headWidth + 0.6) * ss, yBottom - yTop + 1.6 * ss);

        if (item.Length >= 16 || p.Beamed) return bounds;   // a whole note has no stem; a beamed stem is drawn with its beam
        double yStemEnd = p.StemUp ? yTop - StemLength * ss : yBottom + StemLength * ss;
        double middle = StepY(top, ss, 4);   // a stem for a note far outside the staff reaches the middle line, at least
        if (p.StemUp && yStemEnd > middle) yStemEnd = middle;
        if (!p.StemUp && yStemEnd < middle) yStemEnd = middle;
        canvas.Line(new PdfPoint(stemX + Stem * ss / 2, p.StemUp ? yBottom : yTop), new PdfPoint(stemX + Stem * ss / 2, yStemEnd), Stem * ss);
        if (item.Flags > 0) Place(canvas, $"flag{(item.Flags == 1 ? "8th" : "16th")}{(p.StemUp ? "Up" : "Down")}", stemX, yStemEnd, ss);
        return bounds;
    }

    private static void Ledger(PageCanvas canvas, double x, double y, double headWidth, double ss) =>
        canvas.Line(new PdfPoint(x - LedgerExtension * ss, y), new PdfPoint(x + (headWidth + LedgerExtension) * ss, y), LedgerLine * ss);

    private static void DrawBeam(PageCanvas canvas, List<PlacedItem> g, bool up, Func<PlacedItem, double> xOf, double top, double ss)
    {
        double head = Bravura.Get("noteheadBlack").Advance;
        double StemX(PlacedItem p) => up ? xOf(p) + (head - Stem) * ss : xOf(p);
        double NoteEnd(PlacedItem p) => up ? StepY(top, ss, p.Notes.Max(n => n.Pitch.Diatonic) - BottomLineDiatonic) : StepY(top, ss, p.Notes.Min(n => n.Pitch.Diatonic) - BottomLineDiatonic);
        double NoteStart(PlacedItem p) => up ? StepY(top, ss, p.Notes.Min(n => n.Pitch.Diatonic) - BottomLineDiatonic) : StepY(top, ss, p.Notes.Max(n => n.Pitch.Diatonic) - BottomLineDiatonic);

        // The beam runs from the first stem's end to the last's, slanting at most a staff space, then is moved out until
        // every stem is at least two and a half staff spaces long.
        double x1 = StemX(g[0]), x2 = StemX(g[^1]);
        double y1 = NoteEnd(g[0]) + (up ? -StemLength : StemLength) * ss, y2 = NoteEnd(g[^1]) + (up ? -StemLength : StemLength) * ss;
        double slant = Math.Clamp(y2 - y1, -ss, ss), mid = (y1 + y2) / 2;
        y1 = mid - slant / 2; y2 = mid + slant / 2;
        double Line(double x) => x2 == x1 ? y1 : y1 + (y2 - y1) * (x - x1) / (x2 - x1);
        double move = 0;
        foreach (var p in g)
        {
            double reach = Math.Abs(Line(StemX(p)) - NoteEnd(p)), need = 2.5 * ss;
            if (reach < need) move = Math.Max(move, need - reach);
        }
        y1 += up ? -move : move; y2 += up ? -move : move;
        foreach (var p in g)
            canvas.Line(new PdfPoint(StemX(p) + Stem * ss / 2, NoteStart(p)), new PdfPoint(StemX(p) + Stem * ss / 2, Line(StemX(p))), Stem * ss);
        BeamBars(canvas, g.Select(p => (StemX(p), p.Item.Flags)).ToList(), Line, up, ss);
    }

    /// <summary>The primary beam across a group, and a second over runs of sixteenths (a lone sixteenth gets a short stub).</summary>
    private static void BeamBars(PageCanvas canvas, List<(double StemX, int Flags)> stems, Func<double, double> line, bool up, double ss)
    {
        void Bar(double xa, double xb, int level)
        {
            double offset = level * (BeamThickness + BeamGap) * ss * (up ? 1 : -1);
            double a = line(xa) + offset, b = line(xb) + offset, t = BeamThickness * ss * (up ? 1 : -1);
            canvas.Path([
                new('M', new PdfPoint(xa, a)), new('L', new PdfPoint(xb + Stem * ss, b)),
                new('L', new PdfPoint(xb + Stem * ss, b + t)), new('L', new PdfPoint(xa, a + t)), new('Z', default)], Ink);
        }
        Bar(stems[0].StemX, stems[^1].StemX, 0);
        for (int i = 0; i < stems.Count;)
        {
            if (stems[i].Flags < 2) { i++; continue; }
            int j = i;
            while (j + 1 < stems.Count && stems[j + 1].Flags >= 2) j++;
            double xa = stems[i].StemX, xb = stems[j].StemX;
            if (i == j)
            {
                double stub = 1.1 * ss;
                if (i == stems.Count - 1) { xb = xa; xa -= stub; } else xb = xa + stub;
            }
            Bar(xa, xb, 1);
            i = j + 1;
        }
    }

    /// <summary>
    /// Draws a strum of the rhythm staff: a slash with a stem up (hollow for a half or whole, no stem for a whole),
    /// its flag when not beamed, a dot, an accent below; or a rest. Returns the box it takes, for clicking.
    /// </summary>
    private static PdfRect DrawHit(PageCanvas canvas, PlacedHit h, double x, double baseY, double ss)
    {
        if (h.Rest)
        {
            string name = RestGlyph(h.Value);
            Place(canvas, name, x, name == "restWhole" ? baseY - 0.5 * ss : baseY, ss);
            if (h.Dotted) Place(canvas, "augmentationDot", x + (Bravura.Get(name).Advance + 0.3) * ss, baseY - 0.5 * ss, ss);
            return new PdfRect(x - 0.3 * ss, baseY - 2 * ss, (Bravura.Get(name).Advance + 0.6) * ss, 4 * ss);
        }
        double w = SlashWidth * ss, t = 0.42 * ss, half = 0.62 * ss;
        var outline = new List<ShapeStep>
        {
            new('M', new PdfPoint(x, baseY + half)), new('L', new PdfPoint(x + t, baseY + half)),
            new('L', new PdfPoint(x + w, baseY - half)), new('L', new PdfPoint(x + w - t, baseY - half)), new('Z', default),
        };
        bool hollow = h.Value >= 8;
        canvas.Path(outline, hollow ? new PathStyle { Stroke = PdfColor.Black, StrokeWidth = 0.16 * ss } : Ink);
        double stemX = x + w - Stem * ss / 2;
        if (h.Value < 16 && !h.Beamed)
        {
            double end = baseY - StemLength * ss;
            canvas.Line(new PdfPoint(stemX, baseY - half), new PdfPoint(stemX, end), Stem * ss);
            if (h.Flags > 0) Place(canvas, h.Flags == 1 ? "flag8thUp" : "flag16thUp", stemX - Stem * ss / 2, end, ss);
        }
        if (h.Dotted) Place(canvas, "augmentationDot", x + w + 0.35 * ss, baseY - 0.4 * ss, ss);
        if (h.Accent) Place(canvas, "articAccentBelow", x + (w - Bravura.Get("articAccentBelow").Advance * ss) / 2, baseY + half + (h.TiedToNext ? 2.2 : 1.5) * ss, ss);
        return new PdfRect(x - 0.3 * ss, baseY - StemLength * ss - 0.3 * ss, w + 0.6 * ss, (StemLength + 1.0) * ss + half);
    }

    /// <summary>The stems and beams of beamed strums: the stems end level, at a stem length above the slashes.</summary>
    private static void DrawHitBeam(PageCanvas canvas, List<PlacedHit> g, Func<PlacedHit, double> xOf, double baseY, double ss)
    {
        double half = 0.62 * ss, end = baseY - StemLength * ss;
        double StemX(PlacedHit h) => xOf(h) + SlashWidth * ss - Stem * ss;
        foreach (var h in g) canvas.Line(new PdfPoint(StemX(h) + Stem * ss / 2, baseY - half), new PdfPoint(StemX(h) + Stem * ss / 2, end), Stem * ss);
        BeamBars(canvas, g.Select(h => (StemX(h), h.Flags)).ToList(), _ => end, true, ss);
    }

    /// <summary>How far a chord's name and diagram reach to the left and right of its middle, in staff spaces (the name's width a close guess).</summary>
    private static (double Left, double Right) ChordExtent(string name)
    {
        double nameHalf = name.Length * 0.6 * ChordNameSize / 2;
        if (ChordLibrary.Find(name) is not { } shape) return (nameHalf, nameHalf);
        double half = 2.5 * StringGap + 0.4;
        return (Math.Max(half, nameHalf), Math.Max(half + (shape.FirstFret > 1 ? 2.1 : 0), nameHalf));
    }

    /// <summary>
    /// Draws a chord: its name, and below it the diagram from the list of chords (strings upright, low E at the left,
    /// the nut or the first fret's number, a dot for each finger, a bar for a finger across strings, o over an open
    /// string and × over one not played). A name with no shape in the list is written alone. Returns the box it takes.
    /// </summary>
    private static PdfRect DrawChord(PageCanvas canvas, string name, double cx, double top, double ss)
    {
        var style = new TextStyle { Size = (float)(ChordNameSize * ss), Bold = true };
        double nameWidth = canvas.MeasureText(name, style);
        canvas.Text(cx - nameWidth / 2, top + 1.9 * ss, name, style);
        double leftX = cx - nameWidth / 2, rightX = cx + nameWidth / 2, bottom = top + 2.4 * ss;
        if (ChordLibrary.Find(name) is { } shape)
        {
            double gap = StringGap * ss, w = 5 * gap, x0 = cx - w / 2, gridTop = top + 3.6 * ss, fh = FretHeight * ss, thin = 0.1 * ss;
            int frets = shape.FretsShown, first = shape.FirstFret;
            for (int i = 0; i < 6; i++) canvas.Line(new PdfPoint(x0 + i * gap, gridTop), new PdfPoint(x0 + i * gap, gridTop + frets * fh), thin);
            for (int j = 0; j <= frets; j++) canvas.Line(new PdfPoint(x0 - thin / 2, gridTop + j * fh), new PdfPoint(x0 + w + thin / 2, gridTop + j * fh), thin);
            if (first == 1) canvas.Rectangle(x0 - thin / 2, gridTop - 0.4 * ss, w + thin, 0.4 * ss, PdfColor.Black);
            else
            {
                var fr = new TextStyle { Size = (float)(1.3 * ss) };
                string label = $"{first}fr";
                canvas.Text(x0 + w + 0.6 * ss, gridTop + 0.5 * fh + 0.45 * ss, label, fr);
                rightX = Math.Max(rightX, x0 + w + 0.6 * ss + canvas.MeasureText(label, fr));
            }
            double FretY(int f) => gridTop + (f - first + 0.5) * fh;
            if (shape.Barre is { } barre)
                canvas.Line(new PdfPoint(x0 + barre.FromString * gap, FretY(barre.Fret)), new PdfPoint(x0 + w, FretY(barre.Fret)), 0.56 * ss);
            double markY = gridTop - 0.85 * ss;
            for (int i = 0; i < 6; i++)
            {
                double sx = x0 + i * gap;
                int f = shape.Frets[i];
                if (f < 0)
                {
                    double d = 0.27 * ss;
                    canvas.Line(new PdfPoint(sx - d, markY - d), new PdfPoint(sx + d, markY + d), thin);
                    canvas.Line(new PdfPoint(sx - d, markY + d), new PdfPoint(sx + d, markY - d), thin);
                }
                else if (f == 0) canvas.Path(Circle(sx, markY, 0.28 * ss), new PathStyle { Stroke = PdfColor.Black, StrokeWidth = thin });
                else canvas.Path(Circle(sx, FretY(f), 0.36 * ss), Ink);
            }
            leftX = Math.Min(leftX, x0 - 0.4 * ss);
            rightX = Math.Max(rightX, x0 + w + 0.4 * ss);
            bottom = gridTop + frets * fh;
        }
        return new PdfRect(leftX - 0.2 * ss, top, rightX - leftX + 0.4 * ss, bottom - top + 0.4 * ss);
    }

    private static List<ShapeStep> Circle(double cx, double cy, double r)
    {
        double k = 0.5523 * r;
        return
        [
            new('M', new PdfPoint(cx + r, cy)),
            new('C', new PdfPoint(cx, cy + r), new PdfPoint(cx + r, cy + k), new PdfPoint(cx + k, cy + r)),
            new('C', new PdfPoint(cx - r, cy), new PdfPoint(cx - k, cy + r), new PdfPoint(cx - r, cy + k)),
            new('C', new PdfPoint(cx, cy - r), new PdfPoint(cx - r, cy - k), new PdfPoint(cx - k, cy - r)),
            new('C', new PdfPoint(cx + r, cy), new PdfPoint(cx + k, cy - r), new PdfPoint(cx + r, cy - k)),
            new('Z', default),
        ];
    }

    private static void DrawHitTie(PageCanvas canvas, double x1, double x2, double baseY, double ss)
    {
        double a = x1 + (SlashWidth / 2 + 0.2) * ss, b = x2 + (SlashWidth / 2 - 0.2) * ss, y = baseY + 0.8 * ss;
        double w = b - a, h = Math.Clamp(w * 0.1, 0.5 * ss, 0.8 * ss), t = -0.2 * ss;
        canvas.Path([
            new('M', new PdfPoint(a, y)),
            new('C', new PdfPoint(b, y), new PdfPoint(a + w * 0.2, y + h * 1.33), new PdfPoint(b - w * 0.2, y + h * 1.33)),
            new('C', new PdfPoint(a, y), new PdfPoint(b - w * 0.2, y + h * 1.33 + t), new PdfPoint(a + w * 0.2, y + h * 1.33 + t)),
            new('Z', default)], Ink);
    }

    private static void DrawTies(PageCanvas canvas, PlacedItem p, double x1, double x2, double top, double ss, bool offLine)
    {
        double head = Bravura.Get("noteheadBlack").Advance;
        bool above = !p.StemUp;   // a tie goes on the side away from the stem
        foreach (var n in p.Notes)
        {
            int s = n.Pitch.Diatonic - BottomLineDiatonic;
            double y = StepY(top, ss, s) + (above ? -0.45 * ss : 0.45 * ss);
            double a = x1 + (head + 0.15 + Math.Max(0, n.Shift)) * ss, b = offLine ? x2 - 0.4 * ss : x2 - 0.2 * ss;
            if (b - a < 1.2 * ss) b = a + 1.2 * ss;
            double h = Math.Clamp((b - a) * 0.12, 0.6 * ss, 1.2 * ss) * (above ? -1 : 1), t = 0.22 * ss * (above ? 1 : -1);
            double w = b - a;
            canvas.Path([
                new('M', new PdfPoint(a, y)),
                new('C', new PdfPoint(b, y), new PdfPoint(a + w * 0.2, y + h * 1.33), new PdfPoint(b - w * 0.2, y + h * 1.33)),
                new('C', new PdfPoint(a, y), new PdfPoint(b - w * 0.2, y + h * 1.33 + t), new PdfPoint(a + w * 0.2, y + h * 1.33 + t)),
                new('Z', default)], Ink);
        }
    }
}
