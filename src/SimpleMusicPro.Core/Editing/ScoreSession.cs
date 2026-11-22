using System.Text.Json;
using SimpleMusicPro.Midi;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.Editing;

/// <summary>
/// What is kept in a SimpleMusic Pro file (.smpro): which MIDI file the score is made from and which of its
/// tracks is the voice staff, the staves of the stack, and everything entered by hand (the strums). The notes
/// themselves stay in the MIDI file.
/// </summary>
public sealed class ScoreProject
{
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>The MIDI file: relative to the project file when it is beside it or below it, else in full.</summary>
    public string MidiFile { get; set; } = "";
    /// <summary>The track of the voice staff, counted from 0 among the file's tracks of notes.</summary>
    public int VoiceTrack { get; set; }
    public bool ShowVoice { get; set; } = true;
    public bool ShowRhythm { get; set; } = true;
    public string RhythmLabel { get; set; } = "";
    public List<RhythmHit> Rhythm { get; set; } = [];
    public bool ShowChords { get; set; } = true;
    public List<ChordMark> Chords { get; set; } = [];
    public bool ShowLyrics { get; set; } = true;
    public List<LyricMark> Lyrics { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <exception cref="InvalidDataException">The text is not a SimpleMusic Pro file.</exception>
    public static ScoreProject FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ScoreProject>(json) ?? throw new InvalidDataException("The file is empty."); }
        catch (JsonException e) { throw new InvalidDataException("This is not a SimpleMusic Pro file.", e); }
    }
}

/// <summary>
/// A score being worked on: the MIDI file it is made from, what has been entered by hand, undo and redo, and
/// the score as it now stands, ready to be drawn.
/// </summary>
public sealed class ScoreSession
{
    private readonly Stack<string> _undo = new(), _redo = new();
    private IReadOnlyList<Measure>? _bars;
    private int _barsTrack = -1;

    public ScoreProject Project { get; private set; } = new();
    public MidiFile? Midi { get; private set; }
    /// <summary>The MIDI file's full path.</summary>
    public string? MidiPath { get; private set; }
    /// <summary>Where the project was last saved or opened from; null for a new one.</summary>
    public string? ProjectPath { get; private set; }
    public bool Modified { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Starts a new score from a MIDI file, with the track that looks most like a melody as the voice staff.</summary>
    public static ScoreSession FromMidi(string path)
    {
        var midi = MidiFile.Read(path);
        var session = new ScoreSession { Midi = midi, MidiPath = Path.GetFullPath(path), Modified = true };
        session.Project.MidiFile = session.MidiPath;
        session.Project.Title = midi.Title.Length > 0 ? midi.Title : Path.GetFileNameWithoutExtension(path);
        session.Project.VoiceTrack = MelodyTrack(midi);
        // The song's words, when the file has them, are put on the voice staff's notes to start with.
        var bars = session.Bars();
        session.Project.Lyrics = LyricImport.FromMidi(midi, bars);
        return session;
    }

    /// <summary>Opens a saved score; its MIDI file must still be where it was (or beside the project file).</summary>
    /// <exception cref="FileNotFoundException">The MIDI file the score is made from cannot be found.</exception>
    public static ScoreSession Open(string projectPath)
    {
        var project = ScoreProject.FromJson(File.ReadAllText(projectPath));
        string folder = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        string midiPath = Path.IsPathRooted(project.MidiFile) ? project.MidiFile : Path.GetFullPath(Path.Combine(folder, project.MidiFile));
        if (!File.Exists(midiPath))
        {
            // Moved together with the project: look beside it.
            string beside = Path.Combine(folder, Path.GetFileName(project.MidiFile));
            if (!File.Exists(beside)) throw new FileNotFoundException($"The MIDI file this score is made from, {Path.GetFileName(project.MidiFile)}, could not be found.", midiPath);
            midiPath = beside;
        }
        var midi = MidiFile.Read(midiPath);
        project.VoiceTrack = Math.Clamp(project.VoiceTrack, 0, Math.Max(0, midi.Tracks.Count - 1));
        return new ScoreSession { Project = project, Midi = midi, MidiPath = midiPath, ProjectPath = Path.GetFullPath(projectPath) };
    }

    public void Save(string path)
    {
        string full = Path.GetFullPath(path), folder = Path.GetDirectoryName(full)!;
        // Kept relative when the MIDI file is beside the project or below it, so the two can be moved together.
        string relative = Path.GetRelativePath(folder, MidiPath ?? "");
        Project.MidiFile = !relative.StartsWith("..") && !Path.IsPathRooted(relative) ? relative : MidiPath ?? "";
        File.WriteAllText(full, Project.ToJson());
        ProjectPath = full;
        Modified = false;
    }

    /// <summary>The track of a MIDI file most like a melody: one note at a time, not drums, the most notes.</summary>
    public static int MelodyTrack(MidiFile midi)
    {
        var tracks = midi.Tracks.Select((t, i) => (Track: t, Index: i)).Where(x => !x.Track.IsPercussion && x.Track.Notes.Count > 0).ToList();
        var single = tracks.Where(x => x.Track.MostAtOnce == 1).OrderByDescending(x => x.Track.Notes.Count).FirstOrDefault();
        if (single.Track is not null) return single.Index;
        return tracks.Count > 0 ? tracks.OrderBy(x => x.Track.MostAtOnce).ThenByDescending(x => x.Track.Notes.Count).First().Index : 0;
    }

    /// <summary>The score as it now stands.</summary>
    public ScoreDocument Build()
    {
        if (Midi is null || Midi.Tracks.Count == 0) return new ScoreDocument();
        _bars = Bars();
        RhythmLane? lane = null;
        if (Project.ShowRhythm)
        {
            lane = new RhythmLane { Label = Project.RhythmLabel };
            lane.Restore(Project.Rhythm.Where(h => h.Bar < _bars.Count && h.Position < _bars[h.Bar].Length).OrderBy(h => h.Bar).ThenBy(h => h.Position));
        }
        ChordLane? chords = null;
        if (Project.ShowChords)
        {
            chords = new ChordLane();
            chords.Restore(Project.Chords.Where(c => c.Bar < _bars.Count && c.Position < _bars[c.Bar].Length));
        }
        LyricLane? lyrics = null;
        if (Project.ShowLyrics)
        {
            lyrics = new LyricLane();
            lyrics.Restore(Project.Lyrics.Where(l => l.Bar < _bars.Count && l.Position < _bars[l.Bar].Length));
        }
        return new ScoreDocument
        {
            Title = Project.Title, Subtitle = Project.Subtitle, Tempo = Midi.TempoAt(0),
            Bars = _bars, ShowVoice = Project.ShowVoice, Rhythm = lane, Chords = chords, Lyrics = lyrics,
        };
    }

    /// <summary>The bars of the voice track, made once for each track chosen.</summary>
    private IReadOnlyList<Measure> Bars()
    {
        if (Midi is null || Midi.Tracks.Count == 0) return [];
        int track = Math.Clamp(Project.VoiceTrack, 0, Midi.Tracks.Count - 1);
        if (_bars is null || _barsTrack != track) { _bars = Quantizer.Voice(Midi, Midi.Tracks[track]); _barsTrack = track; }
        return _bars;
    }

    /// <summary>Whether the MIDI file has words to take.</summary>
    public bool MidiHasWords => Midi is not null && LyricImport.Words(Midi).Count > 0;

    /// <summary>Takes the words from the MIDI file again, onto the notes of the voice staff as it now is, in the place of all the words there; one step.</summary>
    public int TakeLyricsFromMidi()
    {
        if (Midi is null) return 0;
        var marks = LyricImport.FromMidi(Midi, Bars());
        Change(p => p.Lyrics = marks);
        return marks.Count;
    }

    /// <summary>Writes a syllable at a place (empty text takes it away), as one step. Returns it, or null when taken away.</summary>
    public LyricMark? SetLyric(int bar, int position, string text, bool hyphen)
    {
        var lane = new LyricLane();
        lane.Restore(Project.Lyrics);
        LyricMark? mark = null;
        Change(p =>
        {
            mark = lane.Set(bar, position, text, hyphen);
            p.Lyrics = [.. lane.Marks.OrderBy(m => m.Bar).ThenBy(m => m.Position)];
        });
        return mark;
    }

    /// <summary>Takes away the syllable at a place, as one step.</summary>
    public void RemoveLyric(int bar, int position) =>
        Change(p => p.Lyrics.RemoveAll(m => m.Bar == bar && m.Position == position));

    /// <summary>Writes a chord at a place (in the place of the one there), as one step that can be undone.</summary>
    public ChordMark SetChord(int bar, int position, string name)
    {
        var score = Build();
        var lane = score.Chords ?? throw new InvalidOperationException("There is no chord row in the stack.");
        if (score.ChordTooClose(bar, position))
            throw new ArgumentOutOfRangeException(nameof(position), "Chords stand at least a quarter note apart.");
        ChordMark mark = null!;
        Change(p =>
        {
            mark = lane.Set(bar, position, name);
            p.Chords = [.. lane.Marks.OrderBy(m => m.Bar).ThenBy(m => m.Position)];
        });
        return mark;
    }

    /// <summary>Takes away the chord at a place, as one step.</summary>
    public void RemoveChord(int bar, int position) =>
        Change(p => p.Chords.RemoveAll(m => m.Bar == bar && m.Position == position));

    /// <summary>Makes a change as one step that can be undone.</summary>
    public void Change(Action<ScoreProject> change)
    {
        string before = Project.ToJson();
        change(Project);
        if (Project.ToJson() == before) return;
        _undo.Push(before);
        _redo.Clear();
        Modified = true;
    }

    /// <summary>Adds a strum (or rest) through a rhythm staff, which settles what it replaces, as one step.</summary>
    public RhythmHit AddStrum(int bar, int position, int length, bool rest, bool accent)
    {
        var score = Build();
        var lane = score.Rhythm ?? throw new InvalidOperationException("There is no rhythm staff in the stack.");
        RhythmHit added = null!;
        Change(p =>
        {
            added = lane.Add(bar, position, length, score.Bars[bar].Length, rest, accent);
            p.Rhythm = [.. lane.Hits.OrderBy(h => h.Bar).ThenBy(h => h.Position)];
        });
        return added;
    }

    /// <summary>
    /// Gives the strum sounding at a place a new length (and makes it a strum or a rest, with or without an accent),
    /// as one step. It keeps its start; a longer one takes the place of what follows it, cut to fit the bar.
    /// Returns the strum as it now is, or null when there was none.
    /// </summary>
    public RhythmHit? ChangeStrum(int bar, int position, int length, bool rest, bool accent)
    {
        var score = Build();
        var lane = score.Rhythm;
        if (lane?.At(bar, position) is not { } old) return null;
        RhythmHit changed = null!;
        Change(p =>
        {
            lane.Remove(old.Bar, old.Position);
            changed = lane.Add(old.Bar, old.Position, length, score.Bars[old.Bar].Length, rest, !rest && accent);
            p.Rhythm = [.. lane.Hits.OrderBy(h => h.Bar).ThenBy(h => h.Position)];
        });
        return changed;
    }

    /// <summary>Changes the strum sounding at a place with a function of the lane (remove it, accent it...), as one step.</summary>
    public void EditStrums(Action<RhythmLane> edit)
    {
        var lane = Build().Rhythm;
        if (lane is null) return;
        Change(p => { edit(lane); p.Rhythm = [.. lane.Hits.OrderBy(h => h.Bar).ThenBy(h => h.Position)]; });
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Project.ToJson());
        Project = ScoreProject.FromJson(_undo.Pop());
        Modified = true;
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Project.ToJson());
        Project = ScoreProject.FromJson(_redo.Pop());
        Modified = true;
    }
}
