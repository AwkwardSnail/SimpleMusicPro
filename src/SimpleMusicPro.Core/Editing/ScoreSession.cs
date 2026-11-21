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
        int track = Math.Clamp(Project.VoiceTrack, 0, Midi.Tracks.Count - 1);
        if (_bars is null || _barsTrack != track) { _bars = Quantizer.Voice(Midi, Midi.Tracks[track]); _barsTrack = track; }
        RhythmLane? lane = null;
        if (Project.ShowRhythm)
        {
            lane = new RhythmLane { Label = Project.RhythmLabel };
            lane.Restore(Project.Rhythm.Where(h => h.Bar < _bars.Count && h.Position < _bars[h.Bar].Length).OrderBy(h => h.Bar).ThenBy(h => h.Position));
        }
        return new ScoreDocument
        {
            Title = Project.Title, Subtitle = Project.Subtitle, Tempo = Midi.TempoAt(0),
            Bars = _bars, ShowVoice = Project.ShowVoice, Rhythm = lane,
        };
    }

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
