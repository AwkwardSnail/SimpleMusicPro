using PdfEditor.Core;
using SimpleMusicPro.Engraving;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Core.Tests;

public class RhythmLaneTests
{
    [Fact]
    public void A_strum_is_cut_to_fit_its_bar_and_replaces_what_it_covers()
    {
        var lane = new RhythmLane();
        lane.Add(0, 0, 4, 16);
        lane.Add(0, 4, 4, 16);
        lane.Add(0, 8, 4, 16);
        var added = lane.Add(0, 12, 8, 16);           // a half note at the last beat: only a quarter fits
        Assert.Equal(4, added.Length);
        lane.Add(0, 2, 4, 16);                         // over the second half of beat 1 and the first half of beat 2
        Assert.Equal([(0, 2), (2, 4), (8, 4), (12, 4)], lane.InBar(0).Select(h => (h.Position, h.Length)).ToArray());
        Assert.Equal(2, lane.At(0, 5)!.Position);
        Assert.True(lane.Remove(0, 9));
        Assert.Null(lane.At(0, 9));
        Assert.True(lane.ToggleAccent(0, 0));
        Assert.True(lane.At(0, 0)!.Accent);
    }

    [Fact]
    public void Strums_and_notes_at_the_same_moment_stand_in_one_column()
    {
        // A melody of half notes, and strums of quarters: the strums on beats 1 and 3 stand over the notes.
        var (midi, track) = QuantizerHelper.Make((0, 8, 72), (8, 8, 74));
        var score = new ScoreDocument { Bars = Quantizer.Voice(midi, track), Rhythm = new RhythmLane { Label = "Gtr. 1" } };
        foreach (int beat in new[] { 0, 4, 8, 12 }) score.Rhythm.Add(0, beat, 4, 16, accent: beat == 0);
        using var engraved = ScoreEngraver.Engrave(score, new ScoreOptions());
        var notes = engraved.Items.Where(i => i.Lane == LaneKind.Voice).ToList();
        var strums = engraved.Items.Where(i => i.Lane == LaneKind.Rhythm).ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal(4, strums.Count);
        // Same moment, same column: the left edges line up within a staff space.
        Assert.Equal(notes[0].Bounds.X, strums.Single(s => s.Position == 0).Bounds.X, 1.5);
        Assert.Equal(notes[1].Bounds.X, strums.Single(s => s.Position == 8).Bounds.X, 1.5);
        // The rhythm staff lies above the voice staff.
        var bands = engraved.Bands.Where(b => b.Page == 0).ToList();
        Assert.True(bands.Single(b => b.Lane == LaneKind.Rhythm).Bottom <= bands.Single(b => b.Lane == LaneKind.Voice).Top + 6);
        // The map knows where the beats between the notes are, for showing where a new strum would go.
        var bar = engraved.Bars.Single(b => b.Bar == 0);
        Assert.True(bar.XOf(0) < bar.XOf(4) && bar.XOf(4) < bar.XOf(8) && bar.XOf(8) < bar.XOf(12));
        using var saved = PdfDocument.Open(engraved.Pdf.ToBytes());
        Assert.Contains("Gtr. 1", saved.GetPageText(0));
    }
}