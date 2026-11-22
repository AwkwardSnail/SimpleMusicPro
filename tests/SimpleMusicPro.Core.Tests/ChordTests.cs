using SimpleMusicPro.Editing;
using SimpleMusicPro.Engraving;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Core.Tests;

public class ChordTests
{
    [Fact]
    public void The_list_has_every_kind_in_every_key_and_the_slash_chords()
    {
        foreach (var root in ChordLibrary.Roots)
            foreach (var kind in ChordLibrary.Kinds)
                Assert.NotNull(ChordLibrary.Find(root + kind));
        Assert.NotNull(ChordLibrary.Find("D/F#"));
        Assert.NotNull(ChordLibrary.Find("Am/G"));
        Assert.Equal(ChordLibrary.All.Count, ChordLibrary.All.Select(c => c.Name).Distinct().Count());
    }

    [Theory]
    [InlineData("C", "x32010")]
    [InlineData("Am", "x02210")]
    [InlineData("E", "022100")]
    [InlineData("F", "133211")]
    [InlineData("Bm", "x24432")]
    [InlineData("G", "320003")]
    [InlineData("A9", "575657")]
    [InlineData("Bb7", "x13131")]
    public void Well_known_shapes_are_as_guitarists_play_them(string name, string frets) =>
        Assert.Equal($"{name} {frets}", ChordLibrary.Find(name)!.ToString());

    [Fact]
    public void Every_shape_is_playable_within_five_frets()
    {
        foreach (var c in ChordLibrary.All)
        {
            var pressed = c.Frets.Where(f => f > 0).ToList();
            Assert.True(c.Frets.Count == 6, c.Name);
            Assert.True(c.Frets.Count(f => f >= 0) >= 3, c.Name);
            if (pressed.Count > 0) Assert.True(pressed.Max() - pressed.Min() <= 4, c.ToString());
            Assert.InRange(c.FretsShown, 4, 5);
        }
    }

    [Fact]
    public void A_barre_and_the_first_fret_shown()
    {
        var f = ChordLibrary.Find("F")!;
        Assert.Equal((1, 0), f.Barre);
        Assert.Equal(1, f.FirstFret);
        var bm = ChordLibrary.Find("Bm")!;
        Assert.Equal((2, 1), bm.Barre);
        var cm = ChordLibrary.Find("Cm")!;          // x35543: up the neck
        Assert.Equal(3, cm.FirstFret);
        Assert.Null(ChordLibrary.Find("C")!.Barre);
    }

    [Theory]
    [InlineData("am7", "Am7")]
    [InlineData("Db", "C#")]
    [InlineData("G#m", "Abm")]
    [InlineData("CMaj7", "Cmaj7")]
    [InlineData("d/f#", "D/F#")]
    [InlineData("E♭", "Eb")]
    public void Typed_names_are_spelt_as_the_list_spells_them(string typed, string name) =>
        Assert.Equal(name, ChordLibrary.Normalise(typed));

    [Fact]
    public void Searching_finds_the_chords_that_start_with_what_was_typed()
    {
        var found = ChordLibrary.Search("am").Select(c => c.Name).ToList();
        Assert.Contains("Am", found);
        Assert.Contains("Am7", found);
        Assert.Contains("Am/G", found);
        Assert.DoesNotContain("A", found);
    }

    [Fact]
    public void A_chord_plus_stands_at_the_very_moment_of_the_note_and_a_quarter_note_from_other_chords()
    {
        var (midi, track) = QuantizerHelper.Make((0, 8, 72), (8, 8, 74), (16, 16, 76));
        var score = new ScoreDocument { Bars = Quantizer.Voice(midi, track), Chords = new ChordLane() };
        Assert.Empty(Placeholders.ForChords(score, null));
        // A sixteenth after the beat: the + is there, not on the beat.
        Assert.Equal([(LaneKind.Chords, 0, 5)], Placeholders.ForChords(score, (0, 5)).Select(p => (p.Lane, p.Bar, p.Position)).ToArray());
        score.Chords.Set(0, 5, "G");
        Assert.Empty(Placeholders.ForChords(score, (0, 5)));    // a chord is there
        Assert.Empty(Placeholders.ForChords(score, (0, 7)));    // closer than a quarter note
        Assert.Empty(Placeholders.ForChords(score, (0, 2)));
        Assert.NotEmpty(Placeholders.ForChords(score, (0, 1)));  // a quarter note before it
        Assert.NotEmpty(Placeholders.ForChords(score, (0, 9)));
        // Across the bar line: a chord on the last sixteenth of the bar keeps the next bar's first three sixteenths free of chords.
        score.Chords.Set(0, 15, "C");
        Assert.Empty(Placeholders.ForChords(score, (1, 2)));
        Assert.NotEmpty(Placeholders.ForChords(score, (1, 3)));
    }

    [Fact]
    public void A_chord_too_close_to_another_is_refused()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SimpleMusicPro-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string midiPath = Path.Combine(folder, "tune.mid");
            File.WriteAllBytes(midiPath, MidiFileTests.Sample());
            var session = ScoreSession.FromMidi(midiPath);
            session.SetChord(0, 1, "Am");
            Assert.Throws<ArgumentOutOfRangeException>(() => session.SetChord(0, 3, "G"));
            Assert.Equal("C", session.SetChord(0, 1, "C").Name);   // the same place: changed, not refused
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Chords_are_saved_changed_and_undone()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SimpleMusicPro-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string midiPath = Path.Combine(folder, "tune.mid");
            File.WriteAllBytes(midiPath, MidiFileTests.Sample());
            var session = ScoreSession.FromMidi(midiPath);
            session.SetChord(0, 0, "Am");
            session.SetChord(0, 4, "Am/G");
            session.SetChord(0, 0, "C");                       // in the place of Am
            Assert.Equal([(0, "C"), (4, "Am/G")], session.Build().Chords!.InBar(0).Select(c => (c.Position, c.Name)).ToArray());
            session.RemoveChord(0, 4);
            session.Undo();
            Assert.Equal(2, session.Build().Chords!.Marks.Count);
            string path = Path.Combine(folder, "tune.smpro");
            session.Save(path);
            var again = ScoreSession.Open(path);
            Assert.Equal("Am/G", again.Build().Chords!.At(0, 4)!.Name);
            again.Change(p => p.ShowChords = false);
            Assert.Null(again.Build().Chords);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Chords_a_beat_apart_are_drawn_without_touching()
    {
        var (midi, track) = QuantizerHelper.Make((0, 2, 72), (2, 2, 74), (4, 2, 76), (6, 2, 77), (8, 8, 79));
        var score = new ScoreDocument { Bars = Quantizer.Voice(midi, track), Rhythm = new RhythmLane(), Chords = new ChordLane() };
        string[] names = ["Cmaj7", "Bb7", "D/F#", "G#m"];
        for (int k = 0; k < 4; k++) score.Chords.Set(0, 4 * k, names[k]);
        using var engraved = ScoreEngraver.Engrave(score, new ScoreOptions());
        var boxes = engraved.Items.Where(i => i.Lane == LaneKind.Chords).OrderBy(i => i.Bounds.X).ToList();
        Assert.Equal(4, boxes.Count);
        for (int k = 0; k + 1 < boxes.Count; k++) Assert.True(boxes[k].Bounds.X + boxes[k].Bounds.Width <= boxes[k + 1].Bounds.X, $"{names[k]} touches the next");
        var rhythmBand = engraved.Bands.First(b => b.Lane == LaneKind.Rhythm);
        Assert.True(boxes.All(b => b.Bounds.Y + b.Bounds.Height <= rhythmBand.Top), "the diagrams stay above the rhythm staff");
    }
}
