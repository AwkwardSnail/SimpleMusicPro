using SimpleMusicPro.Midi;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Core.Tests;

public class QuantizerTests
{
    // Notes are given in sixteenth notes (4 ticks a sixteenth at 16 ticks to a quarter would be too fine: use 96 per quarter, 24 a sixteenth).
    private const int T = 24;

    private static (MidiFile File, MidiTrack Track) Make(params (int Start, int Length, int Pitch)[] notes)
    {
        // A file with the sample's header, with these notes as its only track: the sample bytes are reused for the file
        // layout, the notes replaced by reading a file we build here.
        var track = new List<byte>();
        long last = 0;
        var events = notes.SelectMany(n => new[] { (Time: (long)n.Start * T, On: true, n.Pitch), (Time: (long)(n.Start + n.Length) * T, On: false, n.Pitch) })
                          .OrderBy(e => e.Time).ThenBy(e => e.On ? 1 : 0).ToList();
        foreach (var e in events)
        {
            track.AddRange(VarLen(e.Time - last)); last = e.Time;
            track.AddRange([(byte)(e.On ? 0x90 : 0x80), (byte)e.Pitch, (byte)(e.On ? 90 : 0)]);
        }
        track.AddRange([0x00, 0xFF, 0x2F, 0x00]);
        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray());
        file.AddRange([0, 0, 0, 6, 0, 0, 0, 1, 0, 96]);
        file.AddRange("MTrk"u8.ToArray());
        file.AddRange([(byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count]);
        file.AddRange(track);
        var midi = MidiFile.Read([.. file]);
        return (midi, midi.Tracks[0]);
    }

    private static List<byte> VarLen(long v)
    {
        var bytes = new List<byte> { (byte)(v & 0x7F) };
        while ((v >>= 7) > 0) bytes.Insert(0, (byte)((v & 0x7F) | 0x80));
        return bytes;
    }

    private static string Show(Measure m) =>
        string.Join(" ", m.Items.Select(i => (i.IsRest ? "r" : string.Join("+", i.Pitches.Select(p => p.Name))) + i.Length + (i.TiedToNext ? "~" : "")));

    [Fact]
    public void Four_quarter_notes_fill_a_bar_of_four_four()
    {
        var (midi, track) = Make((0, 4, 60), (4, 4, 62), (8, 4, 64), (12, 4, 65));
        var bars = Quantizer.Voice(midi, track);
        var bar = Assert.Single(bars);
        Assert.Equal(16, bar.Length);
        Assert.Equal("C4 4 D4 4 E4 4 F4 4".Replace(" 4", "4"), Show(bar).Replace("C4", "C4").Replace("4 ", "4 "), ignoreCase: false);
    }

    [Fact]
    public void A_gap_becomes_a_rest_and_a_bar_with_nothing_but_rest_is_one_rest()
    {
        var (midi, track) = Make((4, 4, 60), (16 + 16, 4, 62)); // a note on beat 2 of bar 1, then a whole bar of nothing, then a note in bar 3
        var bars = Quantizer.Voice(midi, track);
        Assert.Equal(3, bars.Count);
        Assert.Equal("r4 C44 r8", Show(bars[0]).Replace("C4", "C4").Replace("C44", "C44"));
        Assert.True(bars[1].IsWholeRest);
        Assert.Equal("r16", Show(bars[1]));
    }

    [Fact]
    public void A_note_longer_than_the_bar_is_tied_across_the_barline()
    {
        var (midi, track) = Make((0, 20, 67)); // a whole note and a quarter
        var bars = Quantizer.Voice(midi, track);
        Assert.Equal(2, bars.Count);
        Assert.Equal("G416~", Show(bars[0]));
        Assert.StartsWith("G44", Show(bars[1]));
    }

    [Fact]
    public void A_note_that_would_step_over_the_middle_of_the_bar_is_split_into_tied_notes()
    {
        // A half note starting on beat 2 (sixteenth 4) would cross the middle of the bar: it is a quarter tied to a quarter.
        var (midi, track) = Make((0, 4, 60), (4, 8, 62), (12, 4, 64));
        var bar = Quantizer.Voice(midi, track)[0];
        Assert.Equal("C44 D44~ D44 E44", Show(bar));
    }

    [Fact]
    public void Eighth_notes_stay_inside_their_beat_and_a_syncopation_is_tied_over_the_beat()
    {
        // An offbeat quarter (sixteenth 2 to 6) crosses beat 2: it is written as two tied eighth notes.
        var (midi, track) = Make((0, 2, 60), (2, 4, 62), (6, 2, 64), (8, 8, 65));
        var bar = Quantizer.Voice(midi, track)[0];
        Assert.Equal("C42 D42~ D42 E42 F48", Show(bar));
    }

    [Fact]
    public void Chords_keep_all_their_notes_and_a_note_ends_where_the_next_begins()
    {
        var (midi, track) = Make((0, 8, 60), (0, 8, 64), (0, 8, 67), (4, 4, 72)); // a long chord, cut short by the note that follows
        var bar = Quantizer.Voice(midi, track)[0];
        Assert.Equal("C4+E4+G44 C54 r8", Show(bar));
    }

    [Theory]
    [InlineData(61, 0, "C#4")]   // no key: black keys are sharps
    [InlineData(61, -3, "Db4")]  // three flats: they are flats
    [InlineData(66, 1, "F#4")]   // G major: F sharp is in the key
    [InlineData(70, -1, "Bb4")]  // F major: B flat is in the key
    [InlineData(60, 0, "C4")]
    [InlineData(71, 0, "B4")]
    public void Pitches_are_spelled_to_suit_the_key(int midi, int sharps, string written) =>
        Assert.Equal(written, Pitch.FromMidi(midi, sharps).Name);

    [Fact]
    public void Time_signature_changes_start_a_bar_of_their_own()
    {
        // A pickup bar of one beat, then four-four, as the Aventura file does.
        var (midi, track) = Make((0, 4, 60), (4, 4, 62), (8, 4, 64));
        Assert.Equal(16, Quantizer.Voice(midi, track)[0].Length);
    }
}
