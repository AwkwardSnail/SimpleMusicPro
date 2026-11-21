using SimpleMusicPro.Editing;
using SimpleMusicPro.Engraving;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Core.Tests;

public class EditingTests
{
    private static ScoreDocument TwoBars()
    {
        var (midi, track) = QuantizerHelper.Make((0, 8, 72), (8, 8, 74), (16, 16, 76));
        return new ScoreDocument { Bars = Quantizer.Voice(midi, track), Rhythm = new RhythmLane() };
    }

    [Fact]
    public void Strums_are_eighth_notes_or_longer_and_start_on_the_eighth_note_grid()
    {
        var lane = new RhythmLane();
        Assert.Throws<ArgumentOutOfRangeException>(() => lane.Add(0, 0, 1, 16));   // a sixteenth: not for now
        Assert.Throws<ArgumentOutOfRangeException>(() => lane.Add(0, 3, 2, 16));   // off the grid
        Assert.Throws<ArgumentOutOfRangeException>(() => lane.Add(0, 0, 3, 16));   // a dotted eighth: needs a sixteenth after it
        Assert.Equal(6, lane.Add(0, 0, 6, 16).Length);                             // a dotted quarter is fine
    }

    [Fact]
    public void Empty_bars_offer_a_plus_on_each_beat_and_never_where_a_strum_sounds()
    {
        var score = TwoBars();
        var plus = Placeholders.ForRhythm(score, null, null);
        Assert.Equal([(0, 0), (0, 4), (0, 8), (0, 12), (1, 0), (1, 4), (1, 8), (1, 12)], plus.Select(p => (p.Bar, p.Position)).ToArray());
        score.Rhythm!.Add(0, 0, 4, 16);
        plus = Placeholders.ForRhythm(score, null, null);
        // Where the strum sounds there is none any more; the beats still free, and the strum's end, keep theirs.
        Assert.Equal([(0, 4), (0, 8), (0, 12)], plus.Where(p => p.Bar == 0).Select(p => (p.Bar, p.Position)).ToArray());
    }

    [Fact]
    public void A_gap_left_in_a_bar_can_still_be_filled()
    {
        var score = TwoBars();
        score.Rhythm!.Add(0, 0, 2, 16);                   // an eighth on beat 1
        score.Rhythm.Add(0, 4, 8, 16);                    // a half from beat 2 to beat 3
        var plus = Placeholders.ForRhythm(score, null, null).Where(p => p.Bar == 0).Select(p => p.Position).ToArray();
        Assert.Equal([2, 12], plus);                      // after the eighth, and the fourth beat
    }

    [Fact]
    public void After_a_strum_the_next_plus_is_where_it_ends_even_in_the_next_bar()
    {
        var score = TwoBars();
        var first = score.Rhythm!.Add(0, 0, 6, 16);       // a dotted quarter
        Assert.Contains(Placeholders.ForRhythm(score, null, first), p => p.Bar == 0 && p.Position == 6);
        var last = score.Rhythm.Add(0, 8, 8, 16);         // a half to the end of the bar
        Assert.Contains(Placeholders.ForRhythm(score, null, last), p => p.Bar == 1 && p.Position == 0);
    }

    [Fact]
    public void A_selected_note_offers_a_plus_at_its_moment_rounded_to_the_eighth()
    {
        var score = TwoBars();
        score.Rhythm!.Add(1, 0, 8, 16);                   // a half on beats 1 and 2
        Assert.DoesNotContain(Placeholders.ForRhythm(score, null, null), p => p.Bar == 1 && p.Position == 10);
        var plus = Placeholders.ForRhythm(score, (1, 11), null);
        Assert.Contains(plus, p => p.Lane == LaneKind.Rhythm && p.Bar == 1 && p.Position == 10);
        Assert.DoesNotContain(Placeholders.ForRhythm(score, (1, 3), null), p => p.Bar == 1 && p.Position == 2); // a strum is there already
    }

    [Fact]
    public void A_score_is_saved_with_its_strums_and_opened_again_and_changes_can_be_undone()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SimpleMusicPro-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string midiPath = Path.Combine(folder, "tune.mid");
            File.WriteAllBytes(midiPath, MidiFileTests.Sample());
            var session = ScoreSession.FromMidi(midiPath);
            Assert.Equal("Title", session.Project.Title);
            session.Change(p => p.RhythmLabel = "Gtr. 1");
            session.AddStrum(0, 0, 4, rest: false, accent: true);
            session.AddStrum(0, 4, 8, rest: false, accent: false);   // cut to fit the 3/4 bar: a half
            Assert.Equal([4, 8], session.Build().Rhythm!.InBar(0).Select(h => h.Length).ToArray());
            session.AddStrum(0, 2, 2, rest: false, accent: false);   // shortens the first to an eighth
            Assert.Equal([(0, 2), (2, 2), (4, 8)], session.Build().Rhythm!.InBar(0).Select(h => (h.Position, h.Length)).ToArray());
            session.AddStrum(0, 4, 8, rest: false, accent: false);
            session.AddStrum(0, 14 - 4, 2, rest: false, accent: false); // in a 3/4 bar (12): shortens the half at 4 to 6 sixteenths
            session.Undo();
            session.Undo();
            Assert.Equal([(0, 4), (4, 8)], session.Build().Rhythm!.InBar(0).Select(h => (h.Position, h.Length)).ToArray());
            session.Redo();

            string projectPath = Path.Combine(folder, "tune.smpro");
            session.Save(projectPath);
            Assert.False(session.Modified);
            Assert.Contains("\"MidiFile\": \"tune.mid\"", File.ReadAllText(projectPath));   // kept beside, so the two move together

            var again = ScoreSession.Open(projectPath);
            Assert.Equal("Gtr. 1", again.Project.RhythmLabel);
            var lane = again.Build().Rhythm!;
            Assert.Equal([(0, 2), (2, 2), (4, 8)], lane.InBar(0).Select(h => (h.Position, h.Length)).ToArray());
            Assert.True(lane.At(0, 0)!.Accent);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void While_editing_every_empty_bar_is_kept_and_the_finished_score_joins_them()
    {
        var (midi, track) = QuantizerHelper.Make((0, 4, 60), (16 * 11, 4, 62));
        var score = new ScoreDocument { Bars = Quantizer.Voice(midi, track), Rhythm = new RhythmLane() };
        using var editing = ScoreEngraver.Engrave(score, new ScoreOptions { JoinEmptyBars = false });
        using var finished = ScoreEngraver.Engrave(score, new ScoreOptions());
        Assert.Equal(12, editing.Bars.Count(b => b.Columns.Count > 0));         // each bar has places of its own
        Assert.True(finished.Bars.Count(b => b.Columns.Count > 0) < 12);        // the empty ones share one multi-bar rest
    }

    [Fact]
    public void A_strum_cut_to_a_length_with_no_single_symbol_is_kept_and_written_as_tied_lengths()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SimpleMusicPro-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string midiPath = Path.Combine(folder, "tune.mid");
            File.WriteAllBytes(midiPath, MidiFileTests.Sample());
            var session = ScoreSession.FromMidi(midiPath);
            session.AddStrum(0, 0, 12, rest: false, accent: false);   // a dotted half filling the 3/4 bar
            session.AddStrum(0, 10, 2, rest: false, accent: false);   // an eighth at its end: the first becomes 10 sixteenths long
            var lane = session.Build().Rhythm!;
            Assert.Equal([(0, 10), (10, 2)], lane.InBar(0).Select(h => (h.Position, h.Length)).ToArray());
            using var engraved = ScoreEngraver.Engrave(session.Build(), new ScoreOptions());
            // 10 sixteenths are written as a half and an eighth: three marks for two strums.
            Assert.Equal(3, engraved.Items.Count(i => i.Lane == LaneKind.Rhythm));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}