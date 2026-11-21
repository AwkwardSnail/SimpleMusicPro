using SimpleMusicPro.Midi;

namespace SimpleMusicPro.Core.Tests;

public class MidiFileTests
{
    /// <summary>A small MIDI file made by hand: 96 ticks to a quarter, one conductor track and one track of notes.</summary>
    internal static byte[] Sample()
    {
        var conductor = new List<byte>();
        conductor.AddRange([0x00, 0xFF, 0x03, 0x05, .. "Title"u8.ToArray()]);          // track name
        conductor.AddRange([0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20]);                 // 500000 us per quarter = 120 bpm
        conductor.AddRange([0x00, 0xFF, 0x58, 0x04, 0x03, 0x02, 0x18, 0x08]);           // 3/4
        conductor.AddRange([0x00, 0xFF, 0x59, 0x02, 0xFE, 0x00]);                       // two flats, major
        conductor.AddRange([0x00, 0xFF, 0x2F, 0x00]);

        var notes = new List<byte>();
        notes.AddRange([0x00, 0xFF, 0x03, 0x04, .. "Lead"u8.ToArray()]);
        notes.AddRange([0x00, 0xC0, 0x49]);                                             // program 73 (flute) on channel 0
        notes.AddRange([0x00, 0x90, 60, 100]);                                          // note on C4
        notes.AddRange([0x60, 0x80, 60, 0]);                                            // 96 ticks later: off
        notes.AddRange([0x00, 0x90, 64, 90]);                                           // running status would follow; use a full byte here
        notes.AddRange([0x00, 0xFF, 0x05, 0x02, .. "La"u8.ToArray()]);                  // a lyric
        notes.AddRange([0x60, 64, 0]);                                                  // running status: note on with velocity 0 = off
        notes.AddRange([0x00, 0xFF, 0x2F, 0x00]);

        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray());
        file.AddRange([0, 0, 0, 6, 0, 1, 0, 2, 0, 96]);
        foreach (var track in new[] { conductor, notes })
        {
            file.AddRange("MTrk"u8.ToArray());
            file.AddRange([0, 0, (byte)(track.Count >> 8), (byte)track.Count]);
            file.AddRange(track);
        }
        return [.. file];
    }

    [Fact]
    public void A_midi_file_is_read_into_notes_tempo_time_and_key()
    {
        var m = MidiFile.Read(Sample());
        Assert.Equal(96, m.TicksPerQuarter);
        Assert.Equal("Title", m.Title);
        Assert.Equal(120, m.Tempos[0].BeatsPerMinute, 0.01);
        Assert.Equal((3, 4), (m.TimeSignatures[0].Numerator, m.TimeSignatures[0].Denominator));
        Assert.Equal((-2, false), (m.KeySignatures[0].Sharps, m.KeySignatures[0].Minor));

        var lead = Assert.Single(m.Tracks); // the conductor's track has no notes and is not offered as a part
        Assert.Equal("Lead", lead.Title(1));
        Assert.Equal(73, lead.Program);
        Assert.Equal([0], lead.Channels);
        Assert.Equal(2, lead.Notes.Count);
        Assert.Equal(new MidiNote(0, 96, 60, 100, 0), lead.Notes[0]);
        Assert.Equal(new MidiNote(96, 96, 64, 90, 0), lead.Notes[1]);
        Assert.Equal(1, lead.MostAtOnce);
        Assert.Equal("La", Assert.Single(lead.Lyrics).Text);
        Assert.Equal(192, m.Length);
    }

    [Fact]
    public void A_file_that_is_not_midi_is_refused_plainly()
    {
        var e = Assert.Throws<InvalidDataException>(() => MidiFile.Read("This is not music"u8.ToArray()));
        Assert.Contains("not a MIDI file", e.Message);
    }

    [Fact]
    public void A_track_is_named_by_its_name_its_instrument_or_its_number()
    {
        Assert.Equal("Bass", new MidiTrack { Name = "Bass", Instrument = "Fretless" }.Title(3));
        Assert.Equal("Fretless", new MidiTrack { Instrument = "Fretless" }.Title(3));
        Assert.Equal("Track 3", new MidiTrack().Title(3));
    }
}
