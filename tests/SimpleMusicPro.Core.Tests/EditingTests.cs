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
    public void Strums_are_sixteenth_notes_or_longer_and_start_on_any_sixteenth()
    {
        var lane = new RhythmLane();
        Assert.Throws<ArgumentOutOfRangeException>(() => lane.Add(0, 0, 5, 16));   // no single symbol for five sixteenths
        Assert.Throws<ArgumentOutOfRangeException>(() => lane.Add(0, 16, 1, 16));  // outside the bar
        Assert.Equal(3, lane.Add(0, 0, 3, 16).Length);                             // a dotted eighth
        Assert.Equal(1, lane.Add(0, 3, 1, 16).Length);                             // and a sixteenth off the beat
        Assert.Equal(6, lane.Add(0, 4, 6, 16).Length);                             // a dotted quarter
    }

    [Theory]
    [InlineData(4, 4, "0:16 | 0:8 8:8 | 0:4 4:4 8:4 12:4")]   // whole, halves, quarters
    [InlineData(3, 4, "0:12 | 0:4 4:4 8:4 | 0:2 2:2 4:2")]      // the bar, its three beats, eighths
    [InlineData(6, 8, "0:12 | 0:6 6:6 | 0:2 2:2 4:2")]          // two dotted quarters, three eighths each
    public void A_bar_opens_up_from_the_whole_bar_to_its_halves_beats_eighths_and_sixteenths(int numerator, int denominator, string firstLevels)
    {
        var measure = new Measure(1, 0, numerator * 16 / denominator, numerator, denominator, 0, []);
        var tree = PlaceTree.Of(measure);
        string Level(int depth) => string.Join(" ", tree.Where(n => n.Depth == depth).Take(depth == 2 ? (numerator == 4 ? 4 : 3) : 99).Select(n => $"{n.Start}:{n.Length}"));
        Assert.Equal(firstLevels, $"{Level(0)} | {Level(1)} | {Level(2)}");
        Assert.All(tree.Where(n => n.Depth > 0), n => Assert.True(tree[n.Parent].Start <= n.Start && n.Start + n.Length <= tree[n.Parent].Start + tree[n.Parent].Length));
        Assert.Equal(measure.Length, tree.Count(n => n.Length == 1));                 // down to every sixteenth
        Assert.Equal([0, 1, 2, 3, 4], new[] { 16, 8, 4, 2, 1 }.Select(l => PlaceTree.Shape(numerator == 4 ? tree.First(n => n.Length == l) : tree[0] with { Length = l, Depth = l == 16 ? 0 : 1 })).ToArray());
        Assert.Equal(tree[0], PlaceTree.At(tree, 0));                                // the longest place starting there
        Assert.Equal([0, tree.IndexOf(tree.First(n => n.Depth == 1))], PlaceTree.PathTo(tree, tree.IndexOf(tree.First(n => n.Depth == 1))));
    }

    [Fact]
    public void Sixteenth_strums_in_a_beat_share_a_beam()
    {
        var score = TwoBars();
        for (int p = 0; p < 4; p++) score.Rhythm!.Add(0, p, 1, 16);   // four sixteenths on beat 1
        score.Rhythm!.Add(0, 4, 3, 16);                               // a dotted eighth and a sixteenth on beat 2
        score.Rhythm.Add(0, 7, 1, 16);
        score.Rhythm.Add(0, 9, 1, 16);                                // a lone sixteenth after a gap
        using var engraved = ScoreEngraver.Engrave(score, new ScoreOptions());
        Assert.Equal(7, engraved.Items.Count(i => i.Lane == LaneKind.Rhythm && i.Bar == 0));
    }

    [Fact]
    public void With_no_note_selected_there_is_no_plus()
    {
        var score = TwoBars();
        Assert.Empty(Placeholders.ForRhythm(score, null));
        score.Rhythm!.Add(0, 0, 4, 16);
        Assert.Empty(Placeholders.ForRhythm(score, null));
    }

    [Fact]
    public void A_selected_note_offers_one_plus_at_its_very_moment()
    {
        var score = TwoBars();
        score.Rhythm!.Add(1, 0, 8, 16);                   // a half on beats 1 and 2
        var plus = Placeholders.ForRhythm(score, (1, 11));
        Assert.Equal([(LaneKind.Rhythm, 1, 11)], plus.Select(p => (p.Lane, p.Bar, p.Position)).ToArray());
        Assert.Empty(Placeholders.ForRhythm(score, (1, 0)));   // a strum starts there: click it to change it
        Assert.Single(Placeholders.ForRhythm(score, (1, 3)));  // inside the half: a strum there shortens it
    }

    [Fact]
    public void A_strum_can_be_given_another_length_rest_or_accent()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SimpleMusicPro-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string midiPath = Path.Combine(folder, "tune.mid");
            File.WriteAllBytes(midiPath, MidiFileTests.Sample());
            var session = ScoreSession.FromMidi(midiPath);           // 3/4 bars of 12 sixteenths
            session.AddStrum(0, 0, 4, rest: false, accent: false);
            session.AddStrum(0, 4, 4, rest: false, accent: false);
            session.AddStrum(0, 8, 4, rest: false, accent: false);
            // Clicked inside the strum, not only at its start: a half now, taking the place of the second.
            var changed = session.ChangeStrum(0, 1, 8, rest: false, accent: true)!;
            Assert.Equal(new RhythmHit(0, 0, 8, false, true), changed);
            Assert.Equal([(0, 8), (8, 4)], session.Build().Rhythm!.InBar(0).Select(h => (h.Position, h.Length)).ToArray());
            // A whole note in a bar of three beats is cut to fit: from beat 3, a quarter.
            Assert.Equal(4, session.ChangeStrum(0, 8, 16, rest: false, accent: false)!.Length);
            // A rest has no accent.
            Assert.Equal(new RhythmHit(0, 0, 8, true, false), session.ChangeStrum(0, 0, 8, rest: true, accent: true));
            // Shorter again: an eighth, leaving a gap; one step undoes it.
            session.ChangeStrum(0, 0, 2, rest: false, accent: false);
            Assert.Equal([(0, 2), (8, 4)], session.Build().Rhythm!.InBar(0).Select(h => (h.Position, h.Length)).ToArray());
            session.Undo();
            Assert.True(session.Build().Rhythm!.At(0, 0)!.Rest);
            Assert.Null(session.ChangeStrum(1, 0, 4, rest: false, accent: false));   // nothing there
        }
        finally { Directory.Delete(folder, recursive: true); }
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
    public void A_strum_across_a_beat_is_one_slash_not_tied_parts()
    {
        var score = TwoBars();
        score.Rhythm!.Add(0, 0, 2, 16);    // an eighth
        score.Rhythm.Add(0, 2, 4, 16);     // a quarter starting after beat 1, sounding into beat 2
        score.Rhythm.Add(0, 6, 8, 16);     // a half from after beat 2
        using var engraved = ScoreEngraver.Engrave(score, new ScoreOptions());
        Assert.Equal([(0, 2), (2, 4), (6, 8)], engraved.Items.Where(i => i.Lane == LaneKind.Rhythm && i.Bar == 0).Select(i => (i.Position, i.Length)).ToArray());
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