using SimpleMusicPro.Editing;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Core.Tests;

public class MelodyTests
{
    private static string Show(Measure m) =>
        string.Join(" ", m.Items.Select(i => (i.IsRest ? "r" : string.Join("+", i.Pitches.Select(p => p.Name))) + i.Length + (i.TiedToNext ? "~" : "")));

    [Fact]
    public void Bars_written_again_from_the_same_notes_are_the_same()
    {
        var (midi, track) = QuantizerHelper.Make((0, 4, 60), (4, 2, 62), (6, 2, 64), (8, 12, 65), (24, 4, 67));
        var bars = Quantizer.Voice(midi, track);
        var again = Quantizer.Rewrite(bars, Quantizer.Notes(midi, track));
        Assert.Equal(bars.Select(Show), again.Select(Show));
    }

    [Fact]
    public void A_note_added_inside_another_shortens_it_and_takes_the_place_of_those_it_covers()
    {
        var notes = new List<MelodyNote> { new(0, 8, [60]), new(8, 2, [62]), new(10, 2, [64]), new(12, 4, [65]) };
        MelodyEdit.Add(notes, 4, 8, 67, false, 16);   // a half from beat 2: the first becomes a quarter, the next two go
        Assert.Equal([(0L, 4, 60), (4L, 8, 67), (12L, 4, 65)], notes.Select(n => (n.Start, n.Length, n.Pitches[0])).ToArray());
        MelodyEdit.Add(notes, 14, 8, 69, false, 16);  // cut at the bar's end
        Assert.Equal(2, notes[^1].Length);
        MelodyEdit.Clear(notes, 0, 2);                // a rest at the start: the note starting there goes
        Assert.Equal(4, notes[0].Start);
    }

    [Fact]
    public void Steps_keep_to_the_key_and_accidentals_are_spelt_as_asked()
    {
        Assert.Equal(66, MelodyEdit.StepBy(64, 1, 1, false));       // E up a step in G major: F#
        Assert.Equal(60, MelodyEdit.StepBy(62, -1, 0, false));      // D down a step in C: C
        Assert.Equal(70, MelodyEdit.OnStaff(4 * 7 + 6, -1));         // the B line in F major: Bb
        var (flat, spelt) = MelodyEdit.WithAccidental(67, -1, 0, false);   // G with a flat: Gb, spelt as a flat
        Assert.Equal((66, true), (flat, spelt));
        Assert.Equal("Gb4", Pitch.FromMidi(66, 0, flats: true).Name);
        Assert.Equal("F#4", Pitch.FromMidi(66, 0).Name);
        Assert.Equal("F#4", Pitch.FromMidi(66, 2, flats: true).Name);  // in D major F# is the key's own note
    }

    [Fact]
    public void The_melody_changed_by_hand_is_saved_undone_and_given_back_to_the_midi_file()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SimpleMusicPro-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string midiPath = Path.Combine(folder, "tune.mid");
            File.WriteAllBytes(midiPath, MidiFileTests.Sample());       // 3/4: C4 and E4, a quarter each
            var session = ScoreSession.FromMidi(midiPath);
            Assert.False(session.MelodyChanged);
            string before = Show(session.Build().Bars[0]);
            session.AddNote(0, 8, 4, 67, flats: false);                  // a G on beat 3
            Assert.True(session.MelodyChanged);
            Assert.Equal("C4 4 E4 4 G4 4".Replace(" 4", "4"), Show(session.Build().Bars[0]).Replace("C4 4", "C44"));
            session.SetNoteLength(0, 0, 2);
            session.SetNotePitch(0, 4, 63, flats: true);
            Assert.Equal("C42 r2 Eb44 G44", Show(session.Build().Bars[0]));
            session.RemoveNote(0, 8);
            Assert.Equal("C42 r2 Eb44 r4", Show(session.Build().Bars[0]));
            string path = Path.Combine(folder, "tune.smpro");
            session.Save(path);
            var again = ScoreSession.Open(path);
            Assert.Equal("C42 r2 Eb44 r4", Show(again.Build().Bars[0]));
            again.TakeNotesFromMidi();
            Assert.Equal(before, Show(again.Build().Bars[0]));
            again.Undo();
            Assert.True(again.MelodyChanged);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
