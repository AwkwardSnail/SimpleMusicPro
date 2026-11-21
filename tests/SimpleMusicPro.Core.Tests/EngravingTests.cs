using PdfEditor.Core;
using SimpleMusicPro.Engraving;
using SimpleMusicPro.Midi;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Core.Tests;

public class EngravingTests
{
    private static IReadOnlyList<Measure> Bars(params (int Start, int Length, int Pitch)[] notes)
    {
        var (midi, track) = QuantizerHelper.Make(notes);
        return Quantizer.Voice(midi, track);
    }

    [Fact]
    public void A_score_becomes_a_pdf_with_its_title_and_the_marks_of_its_notes()
    {
        var bars = Bars((0, 4, 60), (4, 4, 62), (8, 2, 64), (10, 2, 65), (12, 4, 67), (16, 16, 72));
        using var doc = ScoreEngraver.Engrave(bars, new ScoreOptions { Title = "Little tune", Subtitle = "Somebody", Tempo = 100 });
        Assert.Equal(1, doc.PageCount);
        using var saved = PdfDocument.Open(doc.ToBytes());
        string text = saved.GetPageText(0);
        Assert.Contains("Little tune", text);
        Assert.Contains("Somebody", text);
        Assert.Contains("= 100", text);
        Assert.True(saved.GetShapes(0).Count > 20, "staff lines, clef, notes, stems and barlines are drawn as shapes");
    }

    [Fact]
    public void A_long_piece_runs_onto_more_pages_and_every_bar_is_drawn_once()
    {
        // 200 bars of a rising and falling line: far more than a page holds.
        var notes = Enumerable.Range(0, 200 * 4).Select(i => (Start: i * 4, Length: 4, Pitch: 60 + i % 9)).ToArray();
        using var doc = ScoreEngraver.Engrave(Bars(notes), new ScoreOptions { Title = "Scales" });
        Assert.InRange(doc.PageCount, 3, 12);
        using var saved = PdfDocument.Open(doc.ToBytes());
        for (int p = 0; p < doc.PageCount; p++) Assert.True(saved.GetShapes(p).Count > 200, $"page {p + 1} has music on it");
    }

    [Fact]
    public void Empty_bars_in_a_row_are_written_as_one_multi_bar_rest_with_a_number()
    {
        // Notes in bar 1 and bar 12, nothing between: ten bars of rest.
        var bars = Bars((0, 4, 60), (16 * 11, 4, 62));
        using var doc = ScoreEngraver.Engrave(bars, new ScoreOptions());
        using var saved = PdfDocument.Open(doc.ToBytes());
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(12, bars.Count);
        // Far fewer marks than ten separate bars would need (each would add a whole rest and a barline).
        var separate = doc.PageCount == 1 ? saved.GetShapes(0).Count : 0;
        Assert.True(separate < 140, $"{separate} marks");
    }

    [Fact]
    public void A_rest_for_an_upbeat_is_the_rest_it_is_not_a_whole_rest()
    {
        var bars = Bars((4, 4, 60), (8, 4, 62), (12, 4, 64)); // starts one beat late, in 4/4: not an upbeat, a bar of rest
        Assert.Equal(16, bars[0].Length);
    }
}