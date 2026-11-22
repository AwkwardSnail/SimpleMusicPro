using System.Text;

namespace SimpleMusicPro.Midi;

/// <summary>A note as played: when it starts and how long it lasts, in ticks, and which key.</summary>
public sealed record MidiNote(long Start, long Length, int Pitch, int Velocity, int Channel)
{
    public long End => Start + Length;
}

/// <summary>A word or syllable set in the file (a lyric event), at a time in ticks.</summary>
public sealed record MidiLyric(long Time, string Text);

/// <summary>One track of a MIDI file, with what is needed to offer it in a list and to draw it.</summary>
public sealed class MidiTrack
{
    /// <summary>The track's own name (a track-name event), or empty.</summary>
    public string Name { get; init; } = "";
    /// <summary>The instrument name event, when there is one.</summary>
    public string Instrument { get; init; } = "";
    /// <summary>The General MIDI program (0 to 127) first chosen on the track, or -1 when none.</summary>
    public int Program { get; init; } = -1;
    /// <summary>The channels the notes are on (channel 10, number 9, is percussion).</summary>
    public IReadOnlyList<int> Channels { get; init; } = [];
    public IReadOnlyList<MidiNote> Notes { get; init; } = [];
    public IReadOnlyList<MidiLyric> Lyrics { get; init; } = [];
    /// <summary>The track's text events: karaoke files keep their words here, a syllable to an event.</summary>
    public IReadOnlyList<MidiLyric> Texts { get; init; } = [];

    public bool IsPercussion => Channels.Count > 0 && Channels.All(c => c == 9);
    public int LowestPitch => Notes.Count == 0 ? 0 : Notes.Min(n => n.Pitch);
    public int HighestPitch => Notes.Count == 0 ? 0 : Notes.Max(n => n.Pitch);
    /// <summary>How many notes sound at once, at most (1 for a melody; more for chords or several voices).</summary>
    public int MostAtOnce { get; init; } = 1;

    /// <summary>The name shown in a list: the track's name, else its instrument, else "Track n".</summary>
    public string Title(int number) => Name.Length > 0 ? Name : Instrument.Length > 0 ? Instrument : $"Track {number}";
}

public sealed record TempoChange(long Time, double MicrosecondsPerQuarter)
{
    public double BeatsPerMinute => 60_000_000.0 / MicrosecondsPerQuarter;
}

public sealed record TimeSignatureChange(long Time, int Numerator, int Denominator);

/// <summary>A key signature: -7 (seven flats) to 7 (seven sharps), and whether it is minor.</summary>
public sealed record KeySignatureChange(long Time, int Sharps, bool Minor);

/// <summary>
/// A standard MIDI file, read: its tracks of notes and lyrics, and the tempo, time signature and key changes
/// (which are kept apart from the tracks, since they belong to the whole piece).
/// </summary>
public sealed class MidiFile
{
    /// <summary>Ticks to a quarter note.</summary>
    public int TicksPerQuarter { get; private init; }
    public IReadOnlyList<MidiTrack> Tracks { get; private init; } = [];
    /// <summary>Tracks with text events and no notes (karaoke files often keep their words in one); not offered as parts.</summary>
    public IReadOnlyList<MidiTrack> TextTracks { get; private init; } = [];
    public IReadOnlyList<TempoChange> Tempos { get; private init; } = [];
    public IReadOnlyList<TimeSignatureChange> TimeSignatures { get; private init; } = [];
    public IReadOnlyList<KeySignatureChange> KeySignatures { get; private init; } = [];
    /// <summary>The title of the piece: the first track's name when that is not an instrument, or empty.</summary>
    public string Title { get; private init; } = "";
    /// <summary>The time of the last note ending, in ticks.</summary>
    public long Length => Tracks.Count == 0 ? 0 : Tracks.Max(t => t.Notes.Count == 0 ? 0 : t.Notes.Max(n => n.End));

    public TimeSignatureChange TimeSignatureAt(long time) =>
        TimeSignatures.LastOrDefault(t => t.Time <= time) ?? new TimeSignatureChange(0, 4, 4);

    public double TempoAt(long time) => (Tempos.LastOrDefault(t => t.Time <= time)?.BeatsPerMinute) ?? 120;

    public static MidiFile Read(string path) => Read(File.ReadAllBytes(path));

    /// <exception cref="InvalidDataException">The data is not a MIDI file.</exception>
    public static MidiFile Read(byte[] data)
    {
        var r = new Reader(data);
        if (r.Remaining < 14 || r.Text(4) != "MThd") throw new InvalidDataException("This is not a MIDI file.");
        int headerLength = r.Int32();
        int format = r.Int16();
        int trackCount = r.Int16();
        int division = r.Int16();
        r.Skip(headerLength - 6);
        if ((division & 0x8000) != 0) throw new InvalidDataException("This MIDI file keeps time in frames (SMPTE), which is not supported; it needs ticks to a quarter note.");
        if (division == 0) throw new InvalidDataException("This MIDI file has no time division.");

        var tracks = new List<MidiTrack>();
        var tempos = new List<TempoChange>();
        var times = new List<TimeSignatureChange>();
        var keys = new List<KeySignatureChange>();
        for (int t = 0; t < trackCount && r.Remaining >= 8; t++)
        {
            string tag = r.Text(4);
            int length = r.Int32();
            int end = Math.Min(data.Length, r.Position + length);
            if (tag != "MTrk") { r.Position = end; t--; continue; }
            tracks.Add(ReadTrack(r, end, tempos, times, keys));
            r.Position = end;
        }

        // Tempo, time and key events can sit in any track (format 0 has just one): the lists are for the whole piece.
        static List<T> Clean<T>(List<T> list, Func<T, long> time) =>
            list.GroupBy(time).Select(g => g.Last()).OrderBy(time).ToList();
        // A track that only carries the conductor's events (no notes) is the "tempo map": it is not offered as a part.
        var parts = tracks.Where(tr => tr.Notes.Count > 0 || tr.Lyrics.Count > 0).ToList();
        string title = tracks.FirstOrDefault(tr => tr.Notes.Count == 0 && tr.Name.Length > 0)?.Name ?? "";
        return new MidiFile
        {
            TicksPerQuarter = division,
            Tracks = parts,
            TextTracks = tracks.Where(tr => tr.Notes.Count == 0 && tr.Lyrics.Count == 0 && tr.Texts.Count > 0).ToList(),
            Tempos = Clean(tempos, x => x.Time),
            TimeSignatures = Clean(times, x => x.Time),
            KeySignatures = Clean(keys, x => x.Time),
            Title = title,
        };
    }

    private static MidiTrack ReadTrack(Reader r, int end, List<TempoChange> tempos, List<TimeSignatureChange> times, List<KeySignatureChange> keys)
    {
        long time = 0;
        int running = 0;
        string name = "", instrument = "";
        int program = -1;
        var notes = new List<MidiNote>();
        var lyrics = new List<MidiLyric>();
        var texts = new List<MidiLyric>();
        var channels = new SortedSet<int>();
        // Notes that have started and not yet stopped, by channel and key (a key can be struck again before it ends).
        var open = new Dictionary<(int Channel, int Pitch), Queue<(long Start, int Velocity)>>();

        while (r.Position < end)
        {
            time += r.VariableLength();
            int status = r.Byte();
            if (status < 0x80) { r.Position--; status = running; }          // running status: the status byte is left out
            else if (status < 0xF0) running = status;
            if (status < 0x80) break;                                          // damaged: nothing to run on from

            if (status == 0xFF)
            {
                int type = r.Byte();
                int length = (int)r.VariableLength();
                int next = Math.Min(end, r.Position + length);
                switch (type)
                {
                    case 0x03 when name.Length == 0: name = Clean(r.Text(length)); break;
                    case 0x04 when instrument.Length == 0: instrument = Clean(r.Text(length)); break;
                    case 0x01: texts.Add(new MidiLyric(time, r.Text(length))); break;
                    case 0x05: lyrics.Add(new MidiLyric(time, r.Text(length))); break;
                    case 0x51 when length == 3: tempos.Add(new TempoChange(time, (r.Byte() << 16) | (r.Byte() << 8) | r.Byte())); break;
                    case 0x58 when length >= 2: times.Add(new TimeSignatureChange(time, r.Byte(), 1 << r.Byte())); break;
                    case 0x59 when length == 2: keys.Add(new KeySignatureChange(time, (sbyte)r.Byte(), r.Byte() == 1)); break;
                }
                r.Position = next;
                if (type == 0x2F) break; // end of track
            }
            else if (status == 0xF0 || status == 0xF7) r.Skip((int)r.VariableLength());
            else
            {
                int kind = status & 0xF0, channel = status & 0x0F;
                int a = r.Byte();
                int b = kind is 0xC0 or 0xD0 ? 0 : r.Byte();
                if (kind == 0xC0 && program < 0) program = a;
                else if (kind == 0x90 && b > 0)
                {
                    if (!open.TryGetValue((channel, a), out var queue)) open[(channel, a)] = queue = new();
                    queue.Enqueue((time, b));
                    channels.Add(channel);
                }
                else if (kind == 0x80 || kind == 0x90 && b == 0)
                {
                    if (open.TryGetValue((channel, a), out var queue) && queue.Count > 0)
                    {
                        var (start, velocity) = queue.Dequeue();
                        notes.Add(new MidiNote(start, Math.Max(1, time - start), a, velocity, channel));
                    }
                }
            }
        }
        // Notes never stopped end where the track does.
        foreach (var ((channel, pitch), queue) in open)
            foreach (var (start, velocity) in queue) notes.Add(new MidiNote(start, Math.Max(1, time - start), pitch, velocity, channel));
        notes.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : x.Pitch.CompareTo(y.Pitch));

        return new MidiTrack
        {
            Name = name, Instrument = instrument, Program = program,
            Channels = [.. channels], Notes = notes, Lyrics = lyrics, Texts = texts,
            MostAtOnce = MostAtOnce(notes),
        };
    }

    private static int MostAtOnce(List<MidiNote> notes)
    {
        int most = notes.Count == 0 ? 0 : 1;
        var ends = new PriorityQueue<long, long>();
        foreach (var n in notes)
        {
            while (ends.Count > 0 && ends.Peek() <= n.Start) ends.Dequeue();
            ends.Enqueue(n.End, n.End);
            most = Math.Max(most, ends.Count);
        }
        return most;
    }

    private static string Clean(string text) => text.Replace('\0', ' ').Trim();

    private sealed class Reader(byte[] d)
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        public int Position { get; set; }
        public int Remaining => d.Length - Position;
        public int Byte() => Position < d.Length ? d[Position++] : throw new InvalidDataException("The MIDI file ends too soon.");
        public int Int16() => (Byte() << 8) | Byte();
        public int Int32() => (Byte() << 24) | (Byte() << 16) | (Byte() << 8) | Byte();
        public void Skip(int count) => Position = Math.Min(d.Length, Position + Math.Max(0, count));
        public string Text(int count)
        {
            count = Math.Max(0, Math.Min(count, Remaining));
            // Most files use Latin-1 (or plain ASCII); newer ones UTF-8, which only reads cleanly when it is UTF-8.
            string s;
            try { s = StrictUtf8.GetString(d, Position, count); }
            catch (DecoderFallbackException) { s = Encoding.Latin1.GetString(d, Position, count); }
            Position += count;
            return s;
        }
        public long VariableLength()
        {
            long v = 0;
            for (int i = 0; i < 4; i++)
            {
                int b = Byte();
                v = (v << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            return v;
        }
    }
}
