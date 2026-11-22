using SimpleMusicPro.Midi;

namespace SimpleMusicPro.Score;

/// <summary>
/// A syllable sung on a note: its bar (index), its place in the bar (in sixteenths), the text, and whether a
/// hyphen joins it to the next syllable (the rest of the same word).
/// </summary>
public sealed record LyricMark(int Bar, int Position, string Text, bool Hyphen = false);

/// <summary>The line of words under the voice staff: at most one syllable at each place.</summary>
public sealed class LyricLane
{
    private readonly List<LyricMark> _marks = [];

    public IReadOnlyList<LyricMark> Marks => _marks;

    public IEnumerable<LyricMark> InBar(int bar) => _marks.Where(m => m.Bar == bar).OrderBy(m => m.Position);

    public LyricMark? At(int bar, int position) => _marks.FirstOrDefault(m => m.Bar == bar && m.Position == position);

    /// <summary>Writes a syllable at a place, in the place of the one there. Empty text takes it away (and returns null).</summary>
    public LyricMark? Set(int bar, int position, string text, bool hyphen)
    {
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position), "The place must be inside the bar.");
        Remove(bar, position);
        text = text.Trim();
        if (text.Length == 0) return null;
        var mark = new LyricMark(bar, position, text, hyphen);
        _marks.Add(mark);
        return mark;
    }

    public bool Remove(int bar, int position) => _marks.RemoveAll(m => m.Bar == bar && m.Position == position) > 0;

    /// <summary>Puts back syllables as they were saved.</summary>
    public void Restore(IEnumerable<LyricMark> marks)
    {
        foreach (var m in marks) if (m.Position >= 0 && m.Text.Length > 0 && At(m.Bar, m.Position) is null) _marks.Add(m);
    }
}

/// <summary>
/// Takes the words of a song from its MIDI file: lyric events, or (in karaoke files) text events, a syllable to an
/// event. Each syllable goes on the note of the voice staff it falls on (or the nearest one, a little before or
/// after); a syllable that does not start a word is joined to the one before with a hyphen.
/// </summary>
public static class LyricImport
{
    /// <summary>The syllables, placed on the notes of <paramref name="bars"/>; empty when the file has no words.</summary>
    public static List<LyricMark> FromMidi(MidiFile midi, IReadOnlyList<Measure> bars)
    {
        var events = Words(midi);
        var result = new List<LyricMark>();
        if (events.Count == 0 || bars.Count == 0) return result;
        double unit = midi.TicksPerQuarter / 4.0;
        // Where each note of the voice staff starts, in sixteenths from the start of the piece.
        // A note that only carries on the one before it (tied from it) has no syllable of its own.
        var notes = new List<(int Bar, int Position, long At)>();
        bool tiedOn = false;
        for (int b = 0; b < bars.Count; b++)
            foreach (var item in bars[b].Items)
            {
                if (!item.IsRest && !tiedOn) notes.Add((b, item.Start, bars[b].Start + item.Start));
                tiedOn = !item.IsRest && item.TiedToNext;
            }
        if (notes.Count == 0) return result;

        foreach (var (time, text, startsWord) in events)
        {
            long slot = (long)Math.Round(time / unit);
            // The nearest note, at most an eighth away; words before the first note (a title, a web address) are left out.
            var near = notes.MinBy(n => Math.Abs(n.At - slot));
            if (Math.Abs(near.At - slot) > 2) continue;
            int i = result.FindIndex(m => m.Bar == near.Bar && m.Position == near.Position);
            if (!startsWord && result.Count > 0) result[^1] = result[^1] with { Hyphen = true };
            if (i >= 0)
            {
                // Two syllables on one note: written together.
                var m = result[i];
                result[i] = m with { Text = m.Text + (startsWord ? " " : "") + text, Hyphen = false };
            }
            else result.Add(new LyricMark(near.Bar, near.Position, text));
        }
        return result;
    }

    /// <summary>
    /// The syllables of the file, in order, each with its time and whether it starts a word: from lyric events when
    /// there are any, else from text events when a track has enough of them to be the words of a song.
    /// </summary>
    public static List<(long Time, string Text, bool StartsWord)> Words(MidiFile midi)
    {
        var lyricTrack = midi.Tracks.OrderByDescending(t => t.Lyrics.Count).FirstOrDefault();
        IReadOnlyList<MidiLyric> raw = lyricTrack is { Lyrics.Count: > 0 } ? lyricTrack.Lyrics : [];
        if (raw.Count == 0)
        {
            // Text events are also used for other things (copyright, notes); karaoke words come many to a track.
            var textTrack = midi.Tracks.Concat(midi.TextTracks).OrderByDescending(t => t.Texts.Count).FirstOrDefault();
            if (textTrack is { Texts.Count: >= 16 }) raw = textTrack.Texts;
        }
        bool hyphenStyle = MarksSyllablesWithHyphens(raw);
        var words = new List<(long, string, bool)>();
        bool newWord = true, hyphenBefore = false;
        foreach (var e in raw.OrderBy(e => e.Time))
        {
            string t = e.Text.Replace('\0', ' ').Replace('\r', '/').Replace('\n', '/');
            if (t.StartsWith('@') || t.StartsWith('%')) continue;                          // karaoke headers
            if (t.Contains("http", StringComparison.OrdinalIgnoreCase) || t.Contains("www.", StringComparison.OrdinalIgnoreCase)) { newWord = true; continue; }
            bool breakBefore = t.StartsWith('/') || t.StartsWith('\\') || t.StartsWith(' ');
            string body = t.Replace("/", "").Replace("\\", "");
            string word = body.Trim();
            if (word.Length > 0 && word.All(c => char.IsPunctuation(c) || char.IsSymbol(c))) word = "";   // count-in dots
            if (word.Length == 0) { newWord = true; continue; }
            bool hyphenAfter = word.EndsWith('-') && word.Length > 1;
            if (hyphenAfter) word = word.TrimEnd('-');
            bool starts = newWord || breakBefore || hyphenStyle && !hyphenBefore;
            words.Add((e.Time, word, starts));
            // The next syllable starts a new word after a space, a line break or a syllable that is a word of its own.
            newWord = body.EndsWith(' ') || t.EndsWith('/') || t.EndsWith('\\');
            hyphenBefore = hyphenAfter;
        }
        return words;
    }

    /// <summary>
    /// Whether the file marks the words' syllables with hyphens ("mu-", "sic") rather than spaces before words (as
    /// karaoke files do): then a syllable with no hyphen before it starts a word even with no space.
    /// </summary>
    private static bool MarksSyllablesWithHyphens(IReadOnlyList<MidiLyric> raw) =>
        raw.Any(e => e.Text.TrimEnd().EndsWith('-')) && !raw.Any(e => e.Text.StartsWith(' ') || e.Text.EndsWith(' '));
}
