using System.Text;
using SimpleMusicPro.Editing;
using SimpleMusicPro.Engraving;
using SimpleMusicPro.Midi;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Core.Tests;

public class LyricsTests
{
    private const int T = 24; // ticks to a sixteenth, at 96 to a quarter

    /// <summary>A song: a track of notes (start and length in sixteenths) and a track of words (meta type 1 for text, 5 for lyric).</summary>
    private static byte[] Song((int Start, int Length)[] notes, (int At, string Text)[] words, byte type, bool wordsInOwnTrack = true)
    {
        var events = notes.SelectMany(n => new[] { (Time: n.Start * T, Order: 1, Bytes: new byte[] { 0x90, 67, 90 }), (Time: (n.Start + n.Length) * T, Order: 0, Bytes: new byte[] { 0x80, 67, 0 }) })
            .ToList();
        var wordEvents = words.Select(w =>
        {
            var text = Encoding.UTF8.GetBytes(w.Text);
            return (Time: w.At * T, Order: 2, Bytes: (byte[])[0xFF, type, (byte)text.Length, .. text]);
        }).ToList();
        var tracks = wordsInOwnTrack ? new[] { events, wordEvents } : [[.. events, .. wordEvents]];
        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray());
        file.AddRange([0, 0, 0, 6, 0, 1, 0, (byte)tracks.Length, 0, 96]);
        foreach (var list in tracks)
        {
            var track = new List<byte>();
            long last = 0;
            foreach (var e in list.OrderBy(e => e.Time).ThenBy(e => e.Order))
            {
                track.AddRange(VarLen(e.Time - last));
                last = e.Time;
                track.AddRange(e.Bytes);
            }
            track.AddRange([0x00, 0xFF, 0x2F, 0x00]);
            file.AddRange("MTrk"u8.ToArray());
            file.AddRange([(byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count]);
            file.AddRange(track);
        }
        return [.. file];
    }

    private static List<byte> VarLen(long v)
    {
        var bytes = new List<byte> { (byte)(v & 0x7F) };
        while ((v >>= 7) > 0) bytes.Insert(0, (byte)((v & 0x7F) | 0x80));
        return bytes;
    }

    private static string Show(IEnumerable<LyricMark> marks) => string.Join(" ", marks.Select(m => m.Text + (m.Hyphen ? "-" : "")));

    [Fact]
    public void Karaoke_words_go_on_the_notes_joined_into_words_and_the_headers_are_left_out()
    {
        var notes = Enumerable.Range(0, 12).Select(k => (16 + 4 * k, 4)).ToArray();   // from bar 2, twelve quarter notes
        (int, string)[] words =
        [
            (0, "@KMIDI KARAOKE FILE"), (0, "@TA little song"), (0, "www.example.com"), (0, "A title before the notes"),
            (16, "/Twin"), (20, "kle"), (24, " twin"), (28, "kle"), (32, " lit"), (36, "tle"), (40, " star"),
            (44, "/How"), (48, " I"), (52, " won"), (56, "der"), (60, " what"),
        ];
        var midi = MidiFile.Read(Song(notes, words, 0x01));
        Assert.Single(midi.Tracks);            // the words' track has no notes: not offered as a part
        var bars = Quantizer.Voice(midi, midi.Tracks[0]);
        var marks = LyricImport.FromMidi(midi, bars);
        Assert.Equal("Twin- kle twin- kle lit- tle star How I won- der what", Show(marks));
        Assert.DoesNotContain(marks, m => m.Bar == 0);
        Assert.Equal([(1, 0), (1, 4), (1, 8), (1, 12)], marks.Take(4).Select(m => (m.Bar, m.Position)).ToArray());
    }

    [Fact]
    public void Lyric_events_with_hyphens_and_utf8_letters_are_read()
    {
        var notes = new[] { (0, 4), (4, 4), (8, 4), (12, 4) };
        var midi = MidiFile.Read(Song(notes, [(0, "Can-"), (4, "ção "), (8, "do "), (12, "mar")], 0x05, wordsInOwnTrack: false));
        var marks = LyricImport.FromMidi(midi, Quantizer.Voice(midi, midi.Tracks[0]));
        Assert.Equal("Can- ção do mar", Show(marks));
    }

    [Fact]
    public void A_syllable_a_little_early_goes_on_its_note_and_a_tied_note_gets_none()
    {
        // A note across the bar line (tied into bar 2), then one more; the words come a sixteenth early.
        var notes = new[] { (0, 4), (4, 4), (8, 4), (12, 8), (20, 4) };
        var midi = MidiFile.Read(Song(notes, [(0, "/One"), (3, " two"), (7, " three"), (11, " four"), (19, " five")], 0x05));
        var bars = Quantizer.Voice(midi, midi.Tracks[0]);
        var marks = LyricImport.FromMidi(midi, bars);
        Assert.Equal([(0, 0), (0, 4), (0, 8), (0, 12), (1, 4)], marks.Select(m => (m.Bar, m.Position)).ToArray());
        var score = new ScoreDocument { Bars = bars, Lyrics = new LyricLane() };
        Assert.Equal((1, 4), Placeholders.NextSungNote(score, 0, 12));       // the tied note is passed over
        Assert.Empty(Placeholders.ForLyrics(score, (1, 0)));
        Assert.Single(Placeholders.ForLyrics(score, (1, 4)));
    }

    [Fact]
    public void Words_are_typed_saved_and_undone()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SimpleMusicPro-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string midiPath = Path.Combine(folder, "tune.mid");
            File.WriteAllBytes(midiPath, MidiFileTests.Sample());
            var session = ScoreSession.FromMidi(midiPath);
            Assert.Equal("La", Assert.Single(session.Project.Lyrics).Text);   // the sample's one lyric, on its note
            session.SetLyric(0, 0, "Hel", hyphen: true);
            session.SetLyric(0, 0, "  ", hyphen: false);                       // empty: taken away
            Assert.Null(session.Build().Lyrics!.At(0, 0));
            session.Undo();
            Assert.Equal(new LyricMark(0, 0, "Hel", true), session.Build().Lyrics!.At(0, 0));
            string path = Path.Combine(folder, "tune.smpro");
            session.Save(path);
            var again = ScoreSession.Open(path);
            Assert.Equal(2, again.Build().Lyrics!.Marks.Count);
            again.RemoveLyric(0, 0);
            Assert.Single(again.Build().Lyrics!.Marks);
            Assert.Equal(1, again.TakeLyricsFromMidi());                       // the file's words again, in the place of these
            again.Change(p => p.ShowLyrics = false);
            Assert.Null(again.Build().Lyrics);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Long_words_on_short_notes_are_spaced_so_they_never_touch()
    {
        var (midi, track) = QuantizerHelper.Make((0, 1, 67), (1, 1, 69), (2, 1, 71), (3, 1, 72), (4, 12, 74));
        var score = new ScoreDocument { Bars = Quantizer.Voice(midi, track), Lyrics = new LyricLane() };
        score.Lyrics.Set(0, 0, "Wonderful", true);
        score.Lyrics.Set(0, 1, "marvellous", false);
        score.Lyrics.Set(0, 2, "extraordinary", false);
        score.Lyrics.Set(0, 3, "W", false);
        score.Lyrics.Set(0, 4, "end", false);
        using var engraved = ScoreEngraver.Engrave(score, new ScoreOptions());
        var boxes = engraved.Items.Where(i => i.Lane == LaneKind.Lyrics).OrderBy(i => i.Bounds.X).ToList();
        Assert.Equal(5, boxes.Count);
        for (int k = 0; k + 1 < boxes.Count; k++) Assert.True(boxes[k].Bounds.X + boxes[k].Bounds.Width <= boxes[k + 1].Bounds.X + 0.01, $"word {k} touches the next");
        // Each word stands under its note, below the staff.
        var notes = engraved.Items.Where(i => i.Lane == LaneKind.Voice).ToList();
        foreach (var w in boxes)
        {
            var n = notes.First(i => i.Position == w.Position);
            Assert.InRange(w.Bounds.X + w.Bounds.Width / 2 - (n.Bounds.X + n.Bounds.Width / 2), -3, 3);
            Assert.True(w.Bounds.Y > n.Bounds.Y + n.Bounds.Height, "below the note");
        }
    }
}
