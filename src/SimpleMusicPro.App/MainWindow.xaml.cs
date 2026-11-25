using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using SimpleMusicPro.Editing;
using SimpleMusicPro.Engraving;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.App;

/// <summary>
/// The window: a control panel on the left (the score's name and the staves of the stack), the pages as they will
/// print on the right, and the toolbar with the length of the next strum. Strums, chords and words are added by
/// clicking the shapes (circles, squares and diamonds) laid over the pages; the shapes are never printed.
/// </summary>
public partial class MainWindow : Window
{
    private ScoreSession? _session;
    private EngravedScore? _engraved;
    private ScoreDocument? _score;
    private readonly List<Canvas> _overlays = [];
    private double _zoom = 1.3;               // screen size of a point of the page, before the screen's own scaling
    private ItemMap? _selected;               // the note or strum selected
    private bool _updating;

    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x2E, 0x9E, 0x4A));
    private static readonly SolidColorBrush SelectFill = new(Color.FromArgb(40, 0x2E, 0x9E, 0x4A));

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // A MIDI or score file given on the command line, or opened with the program, opens straight away.
            Show("Open a MIDI file to begin.");
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) OpenAny(args[1]);
        };
    }

    // ------------------------------------------------------------------ files

    private void OpenMidi_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "MIDI files|*.mid;*.midi|All files|*.*" };
        if (dialog.ShowDialog(this) == true) OpenAny(dialog.FileName);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "SimpleMusic Pro scores|*.smpro|MIDI files|*.mid;*.midi|All files|*.*" };
        if (dialog.ShowDialog(this) == true) OpenAny(dialog.FileName);
    }

    private void OpenAny(string path)
    {
        try
        {
            _session = path.EndsWith(".smpro", StringComparison.OrdinalIgnoreCase) ? ScoreSession.Open(path) : ScoreSession.FromMidi(path);
            _selected = null;
            FillPanel();
            // The page's width fills the preview, at first.
            double room = PreviewScroll.ActualWidth - 2 * 24 - SystemParameters.VerticalScrollBarWidth - 4;
            if (room > 200) _zoom = Math.Clamp(room / (595.28 * 96 / 72), 0.5, 3);
            Refresh();
            Show(_session.ProjectPath is null
                ? $"Started a score from {System.IO.Path.GetFileName(path)}. Click a note in the voice staff, then the shape above it to add a strum."
                : $"Opened {System.IO.Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"{System.IO.Path.GetFileName(path)} could not be opened.\n\n{ex.Message}", "SimpleMusic Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && ConfirmDiscard()) OpenAny(files[0]);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save(saveAs: false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => Save(saveAs: true);

    private bool Save(bool saveAs)
    {
        if (_session is null) return false;
        string? path = _session.ProjectPath;
        if (saveAs || path is null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "SimpleMusic Pro scores|*.smpro", DefaultExt = ".smpro",
                FileName = (_session.Project.Title.Length > 0 ? _session.Project.Title : "Score") + ".smpro",
                InitialDirectory = System.IO.Path.GetDirectoryName(_session.MidiPath) ?? "",
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            _session.Save(path);
            UpdateTitle();
            Show($"Saved {System.IO.Path.GetFileName(path)}. Keep the MIDI file with it: the notes come from there.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The score could not be saved.\n\n{ex.Message}", "SimpleMusic Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var dialog = new SaveFileDialog { Filter = "PDF files|*.pdf", DefaultExt = ".pdf", FileName = (_session.Project.Title.Length > 0 ? _session.Project.Title : "Score") + ".pdf" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // The finished score: empty bars in a row joined into one multi-bar rest.
            using var finished = ScoreEngraver.Engrave(_session.Build(), new ScoreOptions());
            finished.Pdf.Save(dialog.FileName);
            Show($"Saved {System.IO.Path.GetFileName(dialog.FileName)}: {finished.Pdf.PageCount} page{(finished.Pdf.PageCount == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The PDF could not be saved.\n\n{ex.Message}", "SimpleMusic Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool ConfirmDiscard()
    {
        if (_session is not { Modified: true }) return true;
        var answer = MessageBox.Show(this, "Save the changes to this score first?", "SimpleMusic Pro", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && Save(saveAs: false);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        else _engraved?.Dispose();
    }

    // ------------------------------------------------------------------ the control panel

    private void FillPanel()
    {
        if (_session?.Midi is null) return;
        _updating = true;
        try
        {
            Panel.IsEnabled = true;
            TitleBox.Text = _session.Project.Title;
            SubtitleBox.Text = _session.Project.Subtitle;
            ChordsCheck.IsChecked = _session.Project.ShowChords;
            RhythmCheck.IsChecked = _session.Project.ShowRhythm;
            RhythmLabelBox.Text = _session.Project.RhythmLabel;
            VoiceCheck.IsChecked = _session.Project.ShowVoice;
            LyricsCheck.IsChecked = _session.Project.ShowLyrics;
            LyricsCheck.IsEnabled = _session.Project.ShowVoice;
            TakeWordsButton.Visibility = _session.MidiHasWords ? Visibility.Visible : Visibility.Collapsed;
            TrackBox.Items.Clear();
            for (int i = 0; i < _session.Midi.Tracks.Count; i++)
            {
                var t = _session.Midi.Tracks[i];
                string kind = t.IsPercussion ? "drums" : t.MostAtOnce == 1 ? "one note at a time" : $"up to {t.MostAtOnce} at once";
                TrackBox.Items.Add($"{i + 1}. {t.Title(i + 1)} ({t.Notes.Count} notes, {kind})");
            }
            TrackBox.SelectedIndex = _session.Project.VoiceTrack;
        }
        finally { _updating = false; }
    }

    private void Heading_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _session is null) return;
        string title = TitleBox.Text.Trim(), subtitle = SubtitleBox.Text.Trim(), label = RhythmLabelBox.Text.Trim();
        if (title == _session.Project.Title && subtitle == _session.Project.Subtitle && label == _session.Project.RhythmLabel) return;
        _session.Change(p => { p.Title = title; p.Subtitle = subtitle; p.RhythmLabel = label; });
        Refresh();
    }

    private void Stack_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _session is null) return;
        bool rhythm = RhythmCheck.IsChecked == true, voice = VoiceCheck.IsChecked == true, chords = ChordsCheck.IsChecked == true, lyrics = LyricsCheck.IsChecked == true;
        if (!rhythm && !voice)
        {
            // A score needs at least one staff.
            ((CheckBox)sender).IsChecked = true;
            Show("A score needs at least one staff.");
            return;
        }
        _session.Change(p => { p.ShowRhythm = rhythm; p.ShowVoice = voice; p.ShowChords = chords; p.ShowLyrics = lyrics; });
        LyricsCheck.IsEnabled = voice;   // the words go under the voice staff
        _selected = null;
        Refresh();
    }

    private void TakeWords_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        if (_session.Project.Lyrics.Count > 0 && MessageBox.Show(this, "Put the words from the MIDI file on the notes again, in the place of the words there now? Undo brings these back.",
                "SimpleMusic Pro", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int count = _session.TakeLyricsFromMidi();
        _selected = null;
        Refresh();
        Show(count == 0 ? "No words from the MIDI file fall on the notes of this track." : $"{count} syllables from the MIDI file put on the notes.");
    }

    private void Track_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _session is null || TrackBox.SelectedIndex < 0) return;
        int track = TrackBox.SelectedIndex;
        if (track == _session.Project.VoiceTrack) return;
        // Notes changed by hand belong to the track they were made from: another track's notes take their place.
        if (_session.MelodyChanged && MessageBox.Show(this, "The notes changed by hand will be replaced by the notes of the track chosen. Undo brings them back. Go on?",
                "SimpleMusic Pro", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            _updating = true;
            TrackBox.SelectedIndex = _session.Project.VoiceTrack;
            _updating = false;
            return;
        }
        _session.Change(p => { p.VoiceTrack = track; p.Melody = null; });
        _selected = null;
        Refresh();
    }

    // ------------------------------------------------------------------ the toolbar

    /// <summary>The length of the next strum, in sixteenths.</summary>
    private int StrumLength
    {
        get
        {
            int length = 4;
            foreach (var b in new[] { "16", "8", "4", "2", "1" })
                if (FindRadio(b) is { IsChecked: true }) length = int.Parse(b);
            if (DotButton.IsChecked == true && length is 2 or 4 or 8) length = length * 3 / 2;
            return length;
        }
    }

    private RadioButton? FindRadio(string tag) =>
        LogicalTreeHelper.GetChildren((DependencyObject)QuarterButton.Parent).OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tag);

    private void Length_Checked(object sender, RoutedEventArgs e)
    {
        if (DotButton is null) return; // still loading
        // A dot goes with an eighth, a quarter or a half: a dotted sixteenth would need a thirty-second, a dotted whole would not fit.
        bool dottable = sender is RadioButton { Tag: "2" or "4" or "8" };
        DotButton.IsEnabled = dottable;
        if (!dottable) DotButton.IsChecked = false;
        ToolbarChanged();
    }

    private void Option_Click(object sender, RoutedEventArgs e) => ToolbarChanged();

    /// <summary>
    /// ♯, ♭ or ♮: on the note selected, it is put on that note at once; with none selected, it is kept for the next
    /// notes added (one of the three at a time).
    /// </summary>
    private void Accidental_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;
        int alter = int.Parse((string)button.Tag);
        if (_selected is { Lane: LaneKind.Voice } sel && _session is not null && _score is not null && _session.NoteAt(sel.Bar, sel.Position) is { } note)
        {
            button.IsChecked = false;
            int key = _score.Bars[sel.Bar].KeySharps;
            var (midi, flats) = MelodyEdit.WithAccidental(note.Pitches[0], alter, key, note.Flats);
            _session.SetNotePitch(sel.Bar, sel.Position, midi, flats);
            AfterNoteEdit(sel, $"Now {Pitch.FromMidi(midi, key, flats ? true : null).Name}.");
            return;
        }
        foreach (var other in new[] { SharpButton, FlatButton, NaturalButton })
            if (other != button) other.IsChecked = false;
        UpdateDots();
    }

    private void StepNote(ItemMap sel, int steps)
    {
        if (_session?.NoteAt(sel.Bar, sel.Position) is not { } note || _score is null) return;
        int key = _score.Bars[sel.Bar].KeySharps;
        int midi = MelodyEdit.StepBy(note.Pitches[0], steps, key, note.Flats);
        if (midi is < 0 or > 127) return;
        _session.SetNotePitch(sel.Bar, sel.Position, midi, false);
        AfterNoteEdit(sel, $"Now {Pitch.FromMidi(midi, key).Name}.");
    }

    private void DeleteNote(ItemMap sel)
    {
        if (_session is null) return;
        _session.RemoveNote(sel.Bar, sel.Position);
        _selected = null;
        Refresh();
        Show("Note deleted: a rest takes its place. Undo brings it back.");
    }

    private void SetNoteLength(ItemMap sel, int length)
    {
        if (_session?.SetNoteLength(sel.Bar, sel.Position, length) is not { } note) return;
        AfterNoteEdit(sel, note.Length < length ? $"Only {Article(LengthName(note.Length))} {LengthName(note.Length)} fits before the end of the bar." : $"Now {Article(LengthName(note.Length))} {LengthName(note.Length)}.");
    }

    /// <summary>Draws the score again after a change to a note, keeping it selected.</summary>
    private void AfterNoteEdit(ItemMap sel, string message)
    {
        Refresh();
        _selected = _engraved?.Items.Where(i => i.Lane == LaneKind.Voice && i.Bar == sel.Bar && i.Position <= sel.Position).OrderBy(i => i.Position).LastOrDefault();
        DrawOverlays();
        Show(message);
    }

    private void TakeNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { MelodyChanged: true }) return;
        if (MessageBox.Show(this, "Put back the voice track's notes as they are in the MIDI file, in the place of the notes changed by hand? Undo brings these back.",
                "SimpleMusic Pro", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _session.TakeNotesFromMidi();
        _selected = null;
        Refresh();
        Show("The notes are the MIDI file's again.");
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_engraved is null) return;
        BuildHoverDots();
        Show(AutoButton.IsChecked == true
            ? "Auto: point at a bar of the rhythm staff. A circle adds a beat, a square an eighth, a diamond a sixteenth."
            : "Auto off: strums take the length chosen in the toolbar.");
    }

    /// <summary>A length, Dot, Rest or Accent was chosen: it changes the strum selected, if any; else it is for the next one.</summary>
    private void ToolbarChanged()
    {
        if (_updating) return;
        if (_selected is { Lane: LaneKind.Rhythm } sel && _session is not null && _score?.Rhythm?.At(sel.Bar, sel.Position) is { } hit)
        {
            int length = StrumLength;
            var changed = _session.ChangeStrum(hit.Bar, hit.Position, length, RestButton.IsChecked == true, AccentButton.IsChecked == true);
            Refresh();
            if (changed is null) return;
            SelectStrum(changed);
            Show(changed.Length < length
                ? $"Only a {LengthName(changed.Length)} fits before the end of the bar, so the strum is that long."
                : $"Changed to {Article(LengthName(changed.Length))} {LengthName(changed.Length)}{(changed.Rest ? " rest" : "")}.");
        }
        else DrawOverlays();   // the shapes say what they will add
    }

    /// <summary>Selects a strum and sets the toolbar to its length, dot, rest and accent, so a click there changes it.</summary>
    private void SelectStrum(RhythmHit hit)
    {
        _selected = _engraved?.Items.Where(i => i.Lane == LaneKind.Rhythm && i.Bar == hit.Bar && hit.Position <= i.Position && i.Position < hit.End)
                                    .OrderBy(i => i.Position).FirstOrDefault();
        ShowOnToolbar(hit);
        DrawOverlays();
    }

    private void ShowOnToolbar(RhythmHit hit)
    {
        (string? tag, bool dot) = hit.Length switch
        {
            16 => ("16", false), 12 => ("8", true), 8 => ("8", false), 6 => ("4", true), 4 => ("4", false), 3 => ("2", true), 2 => ("2", false), 1 => ("1", false), _ => (null, false),
        };
        _updating = true;
        try
        {
            if (tag is not null && FindRadio(tag) is { } radio)
            {
                radio.IsChecked = true;
                DotButton.IsEnabled = tag is "2" or "4" or "8";
                DotButton.IsChecked = dot;
            }
            RestButton.IsChecked = hit.Rest;
            AccentButton.IsChecked = hit.Accent;
        }
        finally { _updating = false; }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { _session?.Undo(); AfterHistory(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { _session?.Redo(); AfterHistory(); }

    private void AfterHistory()
    {
        _selected = null;
        FillPanel();
        Refresh();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.OriginalSource is TextBox && !ctrl) return;
        if (e.Key == Key.Escape && _pinned) { Unpin(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z) { Undo_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { Save(saveAs: shift); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { Open_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Delete && _selected is { Lane: LaneKind.Rhythm } strum) { DeleteStrum(strum); e.Handled = true; }
        else if (e.Key == Key.Delete && _selected is { Lane: LaneKind.Chords } chord) { DeleteChord(chord); e.Handled = true; }
        else if (e.Key == Key.Delete && _selected is { Lane: LaneKind.Lyrics } word) { DeleteLyric(word); e.Handled = true; }
        else if (e.Key == Key.Delete && _selected is { Lane: LaneKind.Voice } gone) { DeleteNote(gone); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down && _selected is { Lane: LaneKind.Voice } moved) { StepNote(moved, e.Key == Key.Up ? 1 : -1); e.Handled = true; }
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.5, 3);
        Refresh();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ the preview

    private double Scale => _zoom * 96.0 / 72.0;

    /// <summary>Draws the score again from what the session holds, and lays the shapes over it.</summary>
    private void Refresh()
    {
        UpdateTitle();
        UndoButton.IsEnabled = _session?.CanUndo == true;
        RedoButton.IsEnabled = _session?.CanRedo == true;
        TakeNotesButton.Visibility = _session?.MelodyChanged == true ? Visibility.Visible : Visibility.Collapsed;
        _engraved?.Dispose();
        _engraved = null;
        Pages.Children.Clear();
        _overlays.Clear();
        EmptyHint.Visibility = _session is null ? Visibility.Visible : Visibility.Collapsed;
        if (_session is null) return;

        _score = _session.Build();
        // While editing, every bar is drawn, empty or not, so each can take strums.
        _engraved = ScoreEngraver.Engrave(_score, new ScoreOptions { JoinEmptyBars = false });
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int p = 0; p < _engraved.Pdf.PageCount; p++)
        {
            var size = _engraved.Pdf.GetPageSize(p);
            var picture = _engraved.Pdf.Render(p, Scale * dpi, false);
            var bitmap = BitmapSource.Create(picture.Width, picture.Height, 96 * dpi, 96 * dpi, PixelFormats.Bgr32, null, picture.Pixels, picture.Stride);
            bitmap.Freeze();
            var image = new Image { Source = bitmap, Width = size.Width * Scale, Height = size.Height * Scale };
            var overlay = new Canvas { Width = image.Width, Height = image.Height, Background = Brushes.Transparent, Tag = p };
            overlay.MouseLeftButtonDown += Overlay_MouseDown;
            overlay.MouseRightButtonDown += Overlay_RightClick;
            overlay.MouseMove += Overlay_MouseMove;
            overlay.MouseLeave += (_, _) => { _hoverBar = -1; SetHoverChord(null); BuildHoverDots(); };
            var page = new Grid { Margin = new Thickness(0, 0, 0, 24) };
            page.Children.Add(new Border { Child = image, BorderBrush = (Brush)FindResource("LineHoverBrush"), BorderThickness = new Thickness(1), Background = Brushes.White });
            page.Children.Add(overlay);
            Pages.Children.Add(page);
            _overlays.Add(overlay);
        }
        DrawOverlays();
    }

    private void UpdateTitle()
    {
        string name = _session?.ProjectPath is { } p ? System.IO.Path.GetFileNameWithoutExtension(p) : _session?.Project.Title is { Length: > 0 } t ? t : "";
        Title = (name.Length > 0 ? name + (_session!.Modified ? " *" : "") + " - " : "") + "SimpleMusic Pro";
    }

    /// <summary>The selection, and the shapes where a strum, a chord or words can be added.</summary>
    private void DrawOverlays()
    {
        if (_engraved is null || _score is null) return;
        foreach (var o in _overlays) o.Children.Clear();

        if (_selected is { } sel && sel.Page < _overlays.Count)
        {
            var r = new Rectangle { Width = sel.Bounds.Width * Scale, Height = sel.Bounds.Height * Scale, Stroke = Green, StrokeThickness = 1.5, Fill = SelectFill, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false };
            Canvas.SetLeft(r, sel.Bounds.X * Scale);
            Canvas.SetTop(r, sel.Bounds.Y * Scale);
            _overlays[sel.Page].Children.Add(r);
        }

        // A note selected puts the shape of its place (the longest place of the bar's tree that starts there) in the
        // rhythm staff and the chord row above it, and under it where its words go.
        (int, int)? note = _selected is { Lane: LaneKind.Voice } v ? (v.Bar, v.Position) : null;
        foreach (var plus in Placeholders.ForRhythm(_score, note))
        {
            var bar = _engraved.Bars.FirstOrDefault(b => b.Bar == plus.Bar);
            var band = _engraved.Bands.FirstOrDefault(b => b.Lane == LaneKind.Rhythm && b.FirstBar <= plus.Bar && plus.Bar <= b.LastBar);
            if (bar is null || band is null || bar.Page != band.Page) continue;
            // Where the slash would go: on the slashes' line, at the moment's place.
            var place = PlaceTree.At(PlaceTree.Of(_score.Bars[plus.Bar]), plus.Position);
            bool auto = AutoButton.IsChecked == true;
            int length = auto ? NodeLength(place) : StrumLength;
            var p = plus;
            AddShape(_overlays[bar.Page], (bar.XOf(plus.Position) + 0.55 * Space) * Scale, (band.Bottom - 2.2 * Space) * Scale, PlaceTree.Shape(place),
                auto ? StrumWhat(plus, length) : StrumWhat(plus), () => { if (auto) AddStrum(p, length); else AddStrum(p); });
        }

        // The chord row's place: over a note or a strum, at its very moment.
        (int, int)? moment = _selected is { Lane: LaneKind.Voice or LaneKind.Rhythm } m ? (m.Bar, m.Position) : null;
        foreach (var plus in Placeholders.ForChords(_score, moment)) AddChordPlace(plus.Bar, plus.Position);

        // The words' place: under the note, where its syllable would be written.
        foreach (var plus in Placeholders.ForLyrics(_score, note))
        {
            if (LyricPlace(plus.Bar, plus.Position) is not { } at) continue;
            var p = plus;
            AddShape(_overlays[at.Page], at.X, at.Y, ShapeOf(plus.Bar, plus.Position),
                $"Add words here (bar {_score.Bars[plus.Bar].Number}, {BeatName(plus.Position)})", () => OpenLyricEditor(p.Bar, p.Position));
        }

        BuildHoverDots();
    }

    /// <summary>The shape of a place in a bar: that of the longest place of the bar's tree starting there.</summary>
    private int ShapeOf(int bar, int position) => PlaceTree.Shape(PlaceTree.At(PlaceTree.Of(_score!.Bars[bar]), position));

    /// <summary>The length a place stands for (made dotted with Dot), as a length there is a symbol for.</summary>
    private int NodeLength(PlaceNode place)
    {
        int length = place.Length;
        if (DotButton.IsChecked == true && length is 2 or 4 or 8) length = length * 3 / 2;
        return RhythmLane.Lengths.Where(l => l <= length).DefaultIfEmpty(1).Max();
    }

    /// <summary>The sizes of the shapes: the whole bar's large circle, a half's triangle, a beat's square, an eighth's diamond, a sixteenth's dot.</summary>
    private static readonly double[] ShapeSizes = [16, 14, 12, 11, 8];

    /// <summary>
    /// A place's shape, as a button (so it can also be reached with the keyboard and read out), centred at (x, y):
    /// see <see cref="PlaceTree.Shape"/>.
    /// </summary>
    private static Button AddShape(Canvas overlay, double x, double y, int shapeIndex, string what, Action click)
    {
        double size = ShapeSizes[shapeIndex];
        var shape = new Button { Template = ShapeTemplates[shapeIndex], Width = size, Height = size, Cursor = Cursors.Hand, ToolTip = what };
        System.Windows.Automation.AutomationProperties.SetName(shape, what);
        shape.Click += (_, _) => click();
        Canvas.SetLeft(shape, x - size / 2);
        Canvas.SetTop(shape, y - size / 2);
        overlay.Children.Add(shape);
        return shape;
    }

    /// <summary>The chord row's place at a moment, where the diagram would stand: its middle over the note, halfway down the row.</summary>
    private Button? AddChordPlace(int barIndex, int position)
    {
        if (_engraved is null || _score is null) return null;
        var bar = _engraved.Bars.FirstOrDefault(b => b.Bar == barIndex);
        var band = _engraved.Bands.FirstOrDefault(b => b.Lane == LaneKind.Chords && b.FirstBar <= barIndex && barIndex <= b.LastBar);
        if (bar is null || band is null || bar.Page != band.Page || bar.Page >= _overlays.Count) return null;
        double x = (bar.XOf(position) + 0.6 * 5.6) * Scale, y = (band.Top + 5.5 * 5.6) * Scale;
        var overlay = _overlays[bar.Page];
        var anchor = new Rect(x - 9, y - 9, 18, 18);
        var sign = AddShape(overlay, x, y, ShapeOf(barIndex, position), $"Add a chord here (bar {_score.Bars[barIndex].Number}, {BeatName(position)})",
            () => OpenChordPicker(overlay, anchor, barIndex, position, null));
        return sign;
    }

    // ------------------------------------------------------------------ the places of a bar under the mouse

    private int _hoverPage = -1, _hoverBar = -1;   // the bar under the mouse (of the rhythm or the voice staff), or -1
    private LaneKind _hoverLane = LaneKind.Rhythm; // which staff that bar is in
    private List<PlaceNode> _hoverTree = [];       // that bar's tree of places
    private List<int> _hoverPath = [];             // the places opened up, from the whole bar down: their children show
    private int _hoverSteps;                       // in the voice staff: the line or space under the mouse, in steps above the bottom line
    private double _hoverStaffTop;                 // and the top line of that staff, in points
    private double _hoverBaseY;                    // in the rhythm staff: the y on the screen of the whole bar's shape
    private readonly List<(Button Dot, int Node)> _hoverDots = [];
    // The chord row's shape shown with the place last pointed at: it stays while the mouse goes up to it in that bar.
    private (int Page, int Bar, int Position)? _hoverChord;
    private Button? _hoverChordPlus;

    private const double Space = 5.6;               // the staff space the pages are drawn with, in points
    private const int BottomLine = 4 * 7 + 2;       // E4, the bottom line of the voice staff (as Pitch.Diatonic)
    private double RowGap => 1.9 * Space * Scale;   // between the rows of shapes, on the screen

    /// <summary>Finds the bar of the rhythm or voice staff under the mouse; a new one gets its whole-bar shape.</summary>
    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_engraved is null || _score is null || sender is not Canvas overlay) return;
        int page = (int)overlay.Tag;
        var at = e.GetPosition(overlay);
        double x = at.X / Scale, y = at.Y / Scale;
        // The chord row's shape goes once the mouse leaves the bar it belongs to.
        if (_hoverChord is { } hc && (hc.Page != page || _engraved.Bars.FirstOrDefault(m => m.Bar == hc.Bar && m.Page == page) is not { } cb || x < cb.X || x >= cb.X + cb.Width))
            SetHoverChord(null);
        // On a shape, or going from one row of shapes to the next: everything stays as it is (also the pitch).
        if (page == _hoverPage && NearDot(at)) return;
        // Going up to the higher rows (above the staff's own band), the bar's shapes stay open; and once pinned
        // in the voice staff, they stay where they are anywhere over the bar.
        if (page == _hoverPage && InOpenBar(at)) return;
        var band = _engraved.Bands.FirstOrDefault(b => b.Page == page && b.Lane == LaneKind.Rhythm && _score.Rhythm is not null && y >= b.Top && y <= b.Bottom)
                   ?? _engraved.Bands.FirstOrDefault(b => b.Page == page && b.Lane == LaneKind.Voice && y >= b.Top && y <= b.Bottom);
        var bar = band is null ? null : _engraved.Bars.FirstOrDefault(m => m.Page == page && m.Bar >= band.FirstBar && m.Bar <= band.LastBar && x >= m.X && x < m.X + m.Width);
        int index = bar?.Bar ?? -1;
        var lane = band?.Lane ?? LaneKind.Rhythm;
        int steps = 0;
        if (band is { Lane: LaneKind.Voice })
        {
            // The line or space under the mouse (the voice staff's band starts three spaces above its top line).
            _hoverStaffTop = band.Top + 3 * Space;
            steps = Math.Clamp((int)Math.Round((_hoverStaffTop + 4 * Space - y) / (Space / 2)), -8, 16);
        }
        if (page == _hoverPage && index == _hoverBar && lane == _hoverLane)
        {
            // In the voice staff the shapes follow the mouse up and down, from line to space.
            if (lane == LaneKind.Voice && steps != _hoverSteps) { _hoverSteps = steps; UpdateDots(); }
            return;
        }
        (_hoverPage, _hoverBar, _hoverLane, _hoverSteps, _hoverPath, _pinned) = (page, index, lane, steps, [], false);
        BuildHoverDots();
    }

    /// <summary>
    /// Whether the mouse is over the bar whose shapes are open, between its highest row and the foot of its staff:
    /// for a rhythm staff once its places are opened up, for a voice staff once pinned.
    /// </summary>
    private bool InOpenBar(Point at)
    {
        if (_hoverBar < 0 || _engraved is null || _hoverPath.Count == 0 || _hoverLane == LaneKind.Voice && !_pinned) return false;
        var bar = _engraved.Bars.FirstOrDefault(b => b.Bar == _hoverBar && b.Page == _hoverPage);
        var band = _engraved.Bands.FirstOrDefault(b => b.Page == _hoverPage && b.Lane == _hoverLane && b.FirstBar <= _hoverBar && _hoverBar <= b.LastBar);
        if (bar is null || band is null || at.X < bar.X * Scale || at.X > (bar.X + bar.Width) * Scale) return false;
        double top = _hoverDots.Where(d => d.Dot.Visibility == Visibility.Visible).Select(d => Canvas.GetTop(d.Dot)).DefaultIfEmpty(band.Top * Scale).Min() - RowGap / 2;
        return at.Y >= top && at.Y <= band.Bottom * Scale;
    }

    /// <summary>In the voice staff: the shapes held in place by a first click, at the pitch they stood on.</summary>
    private bool _pinned;

    /// <summary>Lets go of shapes pinned in the voice staff: they follow the mouse again.</summary>
    private void Unpin()
    {
        if (!_pinned) return;
        (_pinned, _hoverPath) = (false, []);
        UpdateDots();
        Show("Let go: point at a line or space of the voice staff and click the shape to pin it there.");
    }

    /// <summary>Whether the mouse is on a shape shown, or between it and the row above or below.</summary>
    private bool NearDot(Point at) =>
        _hoverDots.Any(d => d.Dot.Visibility == Visibility.Visible
            && Math.Abs(Canvas.GetLeft(d.Dot) + d.Dot.Width / 2 - at.X) <= d.Dot.Width / 2 + 3
            && Math.Abs(Canvas.GetTop(d.Dot) + d.Dot.Height / 2 - at.Y) <= RowGap / 2 + 2);

    /// <summary>The y on the screen of a line or space of the voice staff under the mouse.</summary>
    private double PitchY(int steps) => (_hoverStaffTop + 4 * Space - steps * Space / 2) * Scale;

    /// <summary>The note the voice staff's shapes add: the line or space under the mouse, with the key's sharp or flat or the one chosen.</summary>
    private (int Midi, bool Flats) HoverPitch(Measure measure)
    {
        int? alter = SharpButton.IsChecked == true ? 1 : FlatButton.IsChecked == true ? -1 : NaturalButton.IsChecked == true ? 0 : null;
        int midi = MelodyEdit.OnStaff(BottomLine + _hoverSteps, measure.KeySharps, alter);
        return (midi, alter < 0);
    }

    private void SetHoverChord((int Page, int Bar, int Position)? place)
    {
        if (place == _hoverChord && (_hoverChordPlus?.Parent is not null || place is null)) return;
        if (_hoverChordPlus?.Parent is Canvas c) c.Children.Remove(_hoverChordPlus);
        _hoverChordPlus = null;
        _hoverChord = place;
        // Not when the chord row's shape of a selected note stands there already, nor where no chord may go.
        if (place is not { } p || _score is null) return;
        (int, int)? moment = _selected is { Lane: LaneKind.Voice or LaneKind.Rhythm } m ? (m.Bar, m.Position) : null;
        if (Placeholders.ForChords(_score, moment).Any(x => x.Bar == p.Bar && x.Position == p.Position)) return;
        if (Placeholders.ForChords(_score, (p.Bar, p.Position)).Count == 0) return;
        _hoverChordPlus = AddChordPlace(p.Bar, p.Position);
    }

    /// <summary>
    /// Lays the shapes of the bar under the mouse (in the rhythm or the voice staff) over it, as a tree: one large
    /// circle for the whole bar; pointing at a shape shows the places it is cut into (halves as triangles, beats as
    /// squares, eighths as diamonds, sixteenths as small dots), each row a little higher than the one before. A click
    /// puts a strum, or in the voice staff a note of the line or space under the mouse, at the shape's place: with
    /// Auto on, as long as the shape stands for; else of the length chosen in the toolbar. What starts there already
    /// is replaced.
    /// </summary>
    private void BuildHoverDots()
    {
        foreach (var d in _hoverDots) (d.Dot.Parent as Canvas)?.Children.Remove(d.Dot);
        _hoverDots.Clear();
        if (_hoverChord is not null) SetHoverChord(_hoverChord);   // put back after the overlays were drawn again
        if (_hoverBar < 0 || _engraved is null || _score is null || _session is null || _hoverBar >= _score.Bars.Count || _hoverPage >= _overlays.Count) return;
        var bar = _engraved.Bars.FirstOrDefault(b => b.Bar == _hoverBar && b.Page == _hoverPage);
        if (bar is null) return;
        bool voice = _hoverLane == LaneKind.Voice;
        if (!voice)
        {
            var band = _engraved.Bands.FirstOrDefault(b => b.Page == _hoverPage && b.Lane == LaneKind.Rhythm && b.FirstBar <= _hoverBar && _hoverBar <= b.LastBar);
            if (band is null || _score.Rhythm is null) return;
            // The whole bar's shape a row above the slashes' line, so the slashes themselves can still be clicked.
            _hoverBaseY = (band.Bottom - 2.2 * Space) * Scale - RowGap;
        }
        var measure = _score.Bars[_hoverBar];
        _hoverTree = PlaceTree.Of(measure);
        int barIndex = _hoverBar, page = _hoverPage;
        bool auto = AutoButton.IsChecked == true, rest = RestButton.IsChecked == true;
        var overlay = _overlays[page];
        for (int i = 0; i < _hoverTree.Count; i++)
        {
            var node = _hoverTree[i];
            int shape = PlaceTree.Shape(node), length = auto ? NodeLength(node) : StrumLength, index = i;
            string name = LengthName(length);
            var placeholder = new Placeholder(voice ? LaneKind.Voice : LaneKind.Rhythm, barIndex, node.Start);
            string what = voice
                ? $"Add {Article(name)} {name}{(rest ? " rest" : "")} here (bar {measure.Number}, {BeatName(node.Start)}), on the line or space under the mouse"
                : StrumWhat(placeholder, length);
            var dot = new Button { Template = ShapeTemplates[shape], Width = ShapeSizes[shape], Height = ShapeSizes[shape], Cursor = Cursors.Hand, ToolTip = what };
            System.Windows.Automation.AutomationProperties.SetName(dot, what);
            dot.MouseEnter += (_, _) =>
            {
                // A shape opens up the places it is cut into (and closes the others'); in the voice staff only once
                // pinned, so that nothing moves while the mouse goes to them.
                if (voice && !_pinned) return;
                _hoverPath = PlaceTree.PathTo(_hoverTree, index);
                UpdateDots();
                if (!voice) SetHoverChord((page, barIndex, node.Start));
            };
            dot.Click += (_, _) =>
            {
                if (voice && !_pinned)
                {
                    // The first click pins the shapes at this pitch and opens the places this one is cut into.
                    _pinned = true;
                    _hoverPath = PlaceTree.PathTo(_hoverTree, index);
                    UpdateDots();
                    return;
                }
                if (voice)
                {
                    // The second click adds the note; then the shapes follow the mouse again, for the next one.
                    (_pinned, _hoverPath) = (false, []);
                    AddNoteAt(barIndex, node.Start, length);
                    return;
                }
                if (auto)
                {
                    // The next place of the same length stays open, for another of this length.
                    int next = _hoverTree.FindIndex(n => n.Start == node.Start + length && n.Length == node.Length);
                    if (next >= 0) _hoverPath = PlaceTree.PathTo(_hoverTree, next).SkipLast(1).ToList();
                }
                if (auto) AddStrum(placeholder, length);
                else AddStrum(placeholder);
            };
            Canvas.SetLeft(dot, (bar.XOf(node.Start) + (voice ? 0.6 : 0.55) * Space) * Scale - dot.Width / 2);
            overlay.Children.Add(dot);
            _hoverDots.Add((dot, i));
        }
        UpdateDots();
    }

    /// <summary>
    /// Shows the whole bar's shape and the shapes of the places opened up, each row above the one before; in the
    /// voice staff, on the line or space under the mouse (the whole bar's shape hidden where it would cover a note
    /// of that pitch at the start of the bar, so the note can be clicked).
    /// </summary>
    private void UpdateDots()
    {
        if (_hoverDots.Count == 0 || _score is null || _hoverBar < 0 || _hoverBar >= _score.Bars.Count) return;
        bool voice = _hoverLane == LaneKind.Voice;
        var measure = _score.Bars[_hoverBar];
        double baseY = voice ? PitchY(_hoverSteps) : _hoverBaseY;
        bool coverNote = false;
        if (voice && !_pinned && _session is not null)
        {
            int midi = HoverPitch(measure).Midi;
            coverNote = _session.Melody().Any(n => n.Start == measure.Start && n.Pitches.Contains(midi));
        }
        foreach (var (dot, i) in _hoverDots)
        {
            var node = _hoverTree[i];
            bool shown = node.Parent < 0 ? !(voice && coverNote) : _hoverPath.Contains(node.Parent);
            dot.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetTop(dot, baseY - node.Depth * RowGap - dot.Height / 2);
        }
        if (voice)
        {
            var (midi, flats) = HoverPitch(measure);
            string pitch = Pitch.FromMidi(midi, measure.KeySharps, flats ? true : null).Name.Replace("#", "♯").Replace("b", "♭");
            Show(_pinned
                ? $"Bar {measure.Number}, {pitch}, pinned: click a shape to put the note there; point at a shape for shorter places. Esc lets go."
                : $"Bar {measure.Number}: {pitch} — click the circle to pin the shapes at this note.");
        }
    }

    /// <summary>Puts the note under the mouse (or a rest, with Rest on) at a place of a bar, of a length.</summary>
    private void AddNoteAt(int bar, int position, int length)
    {
        if (_session is null || _score is null) return;
        var measure = _score.Bars[bar];
        try
        {
            if (RestButton.IsChecked == true)
            {
                _session.AddRest(bar, position, length);
                Refresh();
                Show($"A rest put in bar {measure.Number}, {BeatName(position)}.");
                return;
            }
            var (midi, flats) = HoverPitch(measure);
            var note = _session.AddNote(bar, position, length, midi, flats);
            Refresh();
            Show($"Added {Article(LengthName(note.Length))} {LengthName(note.Length)}, {Pitch.FromMidi(midi, measure.KeySharps, flats ? true : null).Name}. Click it to select it: ↑ ↓ move it, Delete takes it away.");
        }
        catch (ArgumentOutOfRangeException ex) { Show(ex.Message.Split(" (Parameter")[0]); }
    }

    /// <summary>The shapes, by <see cref="PlaceTree.Shape"/>: a large circle, a triangle, a square, a diamond, a small dot.</summary>
    private static readonly ControlTemplate[] ShapeTemplates =
    [
        MakeShapeTemplate(null), MakeShapeTemplate("M0.5,0 L1,1 L0,1 Z"), MakeShapeTemplate("M0,0 L1,0 L1,1 L0,1 Z"),
        MakeShapeTemplate("M0.5,0 L1,0.5 L0.5,1 L0,0.5 Z"), MakeShapeTemplate(null),
    ];

    private static ControlTemplate MakeShapeTemplate(string? outline)
    {
        FrameworkElementFactory ring;
        if (outline is null) ring = new FrameworkElementFactory(typeof(Ellipse), "Ring");
        else
        {
            ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path), "Ring");
            ring.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse(outline));
            ring.SetValue(Shape.StretchProperty, Stretch.Fill);
        }
        ring.SetValue(Shape.FillProperty, new SolidColorBrush(Color.FromArgb(55, 0x2E, 0x9E, 0x4A)));
        ring.SetValue(Shape.StrokeProperty, new SolidColorBrush(Color.FromArgb(150, 0x2E, 0x9E, 0x4A)));
        ring.SetValue(Shape.StrokeThicknessProperty, 1.0);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = ring };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Shape.FillProperty, new SolidColorBrush(Color.FromArgb(150, 0x2E, 0x9E, 0x4A)), "Ring"));
        template.Triggers.Add(hover);
        return template;
    }

    /// <summary>Where a syllable of a note is written on the screen: its page, and the middle of its place.</summary>
    private (int Page, double X, double Y)? LyricPlace(int barIndex, int position)
    {
        var bar = _engraved?.Bars.FirstOrDefault(b => b.Bar == barIndex);
        var band = _engraved?.Bands.FirstOrDefault(b => b.Lane == LaneKind.Lyrics && b.FirstBar <= barIndex && barIndex <= b.LastBar);
        if (bar is null || band is null || bar.Page != band.Page || bar.Page >= _overlays.Count) return null;
        return (bar.Page, (bar.XOf(position) + 0.6 * 5.6) * Scale, (band.Bottom - 1.6 * 5.6) * Scale);
    }

    // ------------------------------------------------------------------ lyrics

    private TextBox? _lyricBox;

    /// <summary>
    /// A box for typing the syllable of a note, where it is written. Space keeps it and goes on to the next note, a
    /// hyphen does the same and joins it to the next syllable, Enter keeps it and closes the box, Esc closes it as it was.
    /// </summary>
    private void OpenLyricEditor(int bar, int position)
    {
        if (_session is null || _score is null || LyricPlace(bar, position) is not { } at) return;
        CloseLyricEditor();
        var current = _score.Lyrics?.At(bar, position);
        var box = new TextBox
        {
            Text = current?.Text ?? "", Width = 110, FontFamily = new FontFamily("Times New Roman"), FontSize = Math.Max(13, 1.9 * 5.6 * Scale),
            TextAlignment = TextAlignment.Center, Padding = new Thickness(2, 0, 2, 0),
            Background = Brushes.White, Foreground = Brushes.Black, BorderBrush = Green, BorderThickness = new Thickness(1.5),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, $"Words of bar {_score.Bars[bar].Number}, {BeatName(position)}");
        bool done = false;
        void Finish(bool keep, bool? hyphen, bool next)
        {
            if (done) return;
            done = true;
            string text = box.Text.Trim();
            CloseLyricEditor();
            if (keep) _session.SetLyric(bar, position, text, hyphen ?? current?.Hyphen ?? false);
            var following = next ? Placeholders.NextSungNote(_session.Build(), bar, position) : null;
            _selected = null;
            Refresh();
            if (following is { } n)
            {
                _selected = _engraved?.Items.FirstOrDefault(i => i.Lane == LaneKind.Voice && i.Bar == n.Bar && i.Position == n.Position);
                DrawOverlays();
                OpenLyricEditor(n.Bar, n.Position);
            }
            else Show(keep ? "Words kept. Click a word to change it." : "Left as it was.");
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { Finish(true, false, true); e.Handled = true; }
            else if (e.Key is Key.OemMinus or Key.Subtract) { Finish(true, box.Text.Trim().Length > 0, true); e.Handled = true; }
            else if (e.Key == Key.Enter) { Finish(true, null, false); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false, null, false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => { if (!done && _lyricBox == box) Finish(true, null, false); };
        var overlay = _overlays[at.Page];
        Canvas.SetLeft(box, at.X - box.Width / 2);
        Canvas.SetTop(box, at.Y - box.FontSize * 0.75);
        overlay.Children.Add(box);
        _lyricBox = box;
        Show("Type the syllable. Space: next note. Hyphen (-): next syllable of the word. Enter: done. Esc: leave it.");
        box.Dispatcher.BeginInvoke(() => { Keyboard.Focus(box); box.SelectAll(); box.BringIntoView(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseLyricEditor()
    {
        var box = _lyricBox;
        _lyricBox = null;
        if (box?.Parent is Canvas c) c.Children.Remove(box);
    }

    private void DeleteLyric(ItemMap word)
    {
        if (_session is null) return;
        _session.RemoveLyric(word.Bar, word.Position);
        _selected = null;
        Refresh();
        Show("Words deleted. Undo brings them back.");
    }

    // ------------------------------------------------------------------ chords

    private Popup? _picker;

    /// <summary>
    /// The list of chords to pick from, under the + or the chord clicked: each chord's diagram and name, narrowed
    /// by what is typed in the box at its top. Enter takes the first chord shown (or, when none is, the name typed,
    /// written without a diagram); Esc closes it.
    /// </summary>
    private void OpenChordPicker(Canvas overlay, Rect anchor, int bar, int position, string? current)
    {
        if (_picker is not null) _picker.IsOpen = false;
        var filter = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(filter, "Chord name");
        var hint = new TextBlock
        {
            Text = "Type a name to narrow the list (Am, G7, D/F#). Enter takes the first.",
            Foreground = (Brush)FindResource("GreenDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontSize = 13.5,
        };
        var list = new WrapPanel { Width = 7 * 76 };
        var scroll = new ScrollViewer { Content = list, MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel();
        panel.Children.Add(filter);
        panel.Children.Add(hint);
        panel.Children.Add(scroll);
        var popup = new Popup
        {
            Child = new Border
            {
                Background = (Brush)FindResource("BarBrush"), BorderBrush = (Brush)FindResource("LineHoverBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = panel,
            },
            PlacementTarget = overlay, Placement = PlacementMode.Bottom, PlacementRectangle = anchor,
            StaysOpen = false, AllowsTransparency = true,
        };
        void Pick(string name)
        {
            popup.IsOpen = false;
            PlaceChord(bar, position, name);
        }
        void Fill()
        {
            list.Children.Clear();
            foreach (var shape in ChordLibrary.Search(filter.Text))
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = shape.Name, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold, FontSize = 14 });
                content.Children.Add(new ChordIcon(shape) { Margin = new Thickness(0, 2, 0, 0) });
                var button = new Button { Content = content, Width = 70, Margin = new Thickness(3), Padding = new Thickness(2, 4, 2, 4), ToolTip = shape.ToString() };
                if (shape.Name == current) button.BorderBrush = Green;
                System.Windows.Automation.AutomationProperties.SetName(button, shape.Name);
                string name = shape.Name;
                button.Click += (_, _) => Pick(name);
                list.Children.Add(button);
            }
            if (list.Children.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = $"There is no diagram for “{filter.Text.Trim()}”. Enter writes the name without one.",
                    TextWrapping = TextWrapping.Wrap, Width = list.Width - 10, Margin = new Thickness(4),
                });
        }
        filter.TextChanged += (_, _) => Fill();
        filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                string? name = ChordLibrary.Search(filter.Text).FirstOrDefault()?.Name ?? (filter.Text.Trim().Length > 0 ? filter.Text.Trim() : null);
                if (name is not null) Pick(name);
                e.Handled = true;
            }
        };
        popup.Opened += (_, _) => filter.Dispatcher.BeginInvoke(() => Keyboard.Focus(filter), System.Windows.Threading.DispatcherPriority.Input);
        Fill();
        _picker = popup;
        // Opened once the click that asked for it is over, so that click does not close it at once.
        Dispatcher.BeginInvoke(() => popup.IsOpen = true, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PlaceChord(int bar, int position, string name)
    {
        if (_session is null) return;
        try
        {
            var mark = _session.SetChord(bar, position, name);
            Refresh();
            _selected = _engraved?.Items.FirstOrDefault(i => i.Lane == LaneKind.Chords && i.Bar == mark.Bar && i.Position == mark.Position);
            DrawOverlays();
            Show(ChordLibrary.Find(mark.Name) is null
                ? $"{mark.Name} written without a diagram: there is none for it in the list."
                : $"{mark.Name} added. Click it to change it; Delete takes it away.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Show(ex.Message.Split(" (Parameter")[0]); }
    }

    private void DeleteChord(ItemMap chord)
    {
        if (_session is null) return;
        _session.RemoveChord(chord.Bar, chord.Position);
        _selected = null;
        Refresh();
        Show("Chord deleted. Undo brings it back.");
    }

    private void ChangeChord(ItemMap chord)
    {
        if (chord.Page >= _overlays.Count || _score?.Chords?.At(chord.Bar, chord.Position) is not { } mark) return;
        var b = chord.Bounds;
        OpenChordPicker(_overlays[chord.Page], new Rect(b.X * Scale, b.Y * Scale, b.Width * Scale, b.Height * Scale), chord.Bar, chord.Position, mark.Name);
    }

    /// <summary>What clicking a shape of the rhythm staff adds, for its tooltip.</summary>
    private string StrumWhat(Placeholder plus, int? sixteenths = null)
    {
        string length = LengthName(sixteenths ?? StrumLength);
        return $"Add {Article(length)} {length}{(RestButton.IsChecked == true ? " rest" : " strum")} here (bar {_score!.Bars[plus.Bar].Number}, {BeatName(plus.Position)})";
    }

    /// <param name="autoLength">With Auto on: the length the shape clicked stands for (else the toolbar's length).</param>
    private void AddStrum(Placeholder plus, int? autoLength = null)
    {
        if (_session is null || _score is null) return;
        int length = autoLength ?? StrumLength;
        try
        {
            var cut = _score.Rhythm?.At(plus.Bar, plus.Position);   // a strum sounding on there is shortened by the new one
            var added = _session.AddStrum(plus.Bar, plus.Position, length, RestButton.IsChecked == true, RestButton.IsChecked != true && AccentButton.IsChecked == true);
            if (autoLength is not null)
            {
                // Not selected, so the toolbar keeps its settings (a selected strum would set them to its own).
                _selected = null;
                Refresh();
                Show(cut is not null
                    ? $"Added {Article(LengthName(added.Length))} {LengthName(added.Length)}; the strum before it now ends where this one starts."
                    : $"Added {Article(LengthName(added.Length))} {LengthName(added.Length)}. The same shape after it adds another.");
                return;
            }
            Refresh();
            SelectStrum(added);   // selected, so a length chosen now changes it
            if (added.Length < length) Show($"Only a {LengthName(added.Length)} fits before the end of the bar, so the strum is that long.");
            else if (cut is not null) Show($"Added. The strum before it is now {Article(LengthName(added.Position - cut.Position))} {LengthName(added.Position - cut.Position)}, ending where this one starts.");
            else Show("Added. To change it, choose another length, Dot, Rest or Accent while it is selected.");
        }
        catch (ArgumentOutOfRangeException ex) { Show(ex.Message.Split(" (Parameter")[0]); }
    }

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SelectAt(sender, e);
        if (_selected is { Lane: LaneKind.Chords } chord) ChangeChord(chord);
        else if (_selected is { Lane: LaneKind.Lyrics } word) { OpenLyricEditor(word.Bar, word.Position); e.Handled = true; }
    }

    /// <summary>Selects what is under the mouse (the smallest thing there), and says what can be done with it.</summary>
    private void SelectAt(object sender, MouseButtonEventArgs e)
    {
        if (_engraved is null || sender is not Canvas overlay) return;
        int page = (int)overlay.Tag;
        var at = e.GetPosition(overlay);
        double x = at.X / Scale, y = at.Y / Scale;
        _selected = _engraved.Items.Where(i => i.Page == page && i.Bounds.Contains(x, y)).OrderBy(i => i.Bounds.Width * i.Bounds.Height).FirstOrDefault();
        if (_selected is { Lane: LaneKind.Rhythm } s && _score!.Rhythm?.At(s.Bar, s.Position) is { } hit)
        {
            ShowOnToolbar(hit);
            Show("A strum: choose a length, Dot, Rest or Accent to change it. Delete takes it away; right-click for more.");
        }
        else if (_selected is { Lane: LaneKind.Voice } v)
        {
            string where = $"Bar {_score!.Bars[v.Bar].Number}, {BeatName(v.Position)}: ";
            bool strum = Placeholders.ForRhythm(_score, (v.Bar, v.Position)).Count > 0, chord = Placeholders.ForChords(_score, (v.Bar, v.Position)).Count > 0;
            string noChord = _score.Chords is null ? "" : " (No chord here: one stands within a quarter note of it.)";
            Show(where + (strum && chord ? "click a shape above it to add a strum or a chord there."
                : strum ? "click the shape above it to add a strum there." + noChord
                : chord ? "a strum starts there already (click it to change it); the shape in the chord row adds a chord."
                : "a strum starts there already: click it to change it." + noChord)
                + (Placeholders.ForLyrics(_score, (v.Bar, v.Position)).Count > 0 ? " The shape under it adds words." : "")
                + (_session?.NoteAt(v.Bar, v.Position) is not null ? " ↑ ↓ move the note, ♯ ♭ ♮ change it, Delete takes it away; right-click for its length." : ""));
        }
        else if (_selected is { Lane: LaneKind.Chords }) Show("A chord: pick another from the list to change it; Delete takes it away.");
        else if (_selected is { Lane: LaneKind.Lyrics }) Show("Words: type to change them; Delete takes them away.");
        DrawOverlays();
    }

    private void Overlay_RightClick(object sender, MouseButtonEventArgs e)
    {
        SelectAt(sender, e);
        if (_session is null) return;
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (_selected is { Lane: LaneKind.Lyrics } word)
        {
            var mark = _score?.Lyrics?.At(word.Bar, word.Position);
            Add("Change the words…", () => OpenLyricEditor(word.Bar, word.Position));
            if (mark is not null)
                Add(mark.Hyphen ? "Take the hyphen away" : "Join to the next syllable (hyphen)", () =>
                {
                    _session.SetLyric(mark.Bar, mark.Position, mark.Text, !mark.Hyphen);
                    Refresh();
                });
            menu.Items.Add(new Separator());
            Add("Delete", () => DeleteLyric(word));
            menu.IsOpen = true;
            e.Handled = true;
            return;
        }
        if (_selected is { Lane: LaneKind.Chords } chord)
        {
            Add("Change the chord…", () => ChangeChord(chord));
            menu.Items.Add(new Separator());
            Add("Delete", () => DeleteChord(chord));
            menu.IsOpen = true;
            e.Handled = true;
            return;
        }
        if (_selected is { Lane: LaneKind.Voice } sel && _session.NoteAt(sel.Bar, sel.Position) is { } note)
        {
            // A note: its length, a sharp or flat, or away with it.
            foreach (var (length, name) in new[] { (16, "a whole note"), (12, "a dotted half"), (8, "a half note"), (6, "a dotted quarter"), (4, "a quarter note"), (3, "a dotted eighth"), (2, "an eighth note"), (1, "a sixteenth note") })
            {
                var item = new MenuItem { Header = "Make it " + name, IsChecked = note.Length == length };
                int l = length;
                item.Click += (_, _) => SetNoteLength(sel, l);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            Add("Sharp (♯)", () => Accidental_Click(SharpButton, new RoutedEventArgs()));
            Add("Flat (♭)", () => Accidental_Click(FlatButton, new RoutedEventArgs()));
            Add("Natural (♮)", () => Accidental_Click(NaturalButton, new RoutedEventArgs()));
            menu.Items.Add(new Separator());
            Add("Delete (a rest in its place)", () => DeleteNote(sel));
            menu.IsOpen = true;
            e.Handled = true;
            return;
        }
        if (_selected is not { Lane: LaneKind.Rhythm } strum) return;
        var hit = _score!.Rhythm?.At(strum.Bar, strum.Position);
        if (hit is null) return;
        if (!hit.Rest) Add(hit.Accent ? "Take the accent off" : "Accent it", () => { _session.EditStrums(l => l.ToggleAccent(strum.Bar, strum.Position)); Refresh(); });
        Add(hit.Rest ? "Make it a strum" : "Make it a rest", () =>
        {
            _session.EditStrums(l => { l.Remove(hit.Bar, hit.Position); l.Restore([hit with { Rest = !hit.Rest, Accent = false }]); });
            Refresh();
        });
        menu.Items.Add(new Separator());
        Add("Delete", () => DeleteStrum(strum));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void DeleteStrum(ItemMap strum)
    {
        if (_session is null) return;
        _session.EditStrums(l => l.Remove(strum.Bar, strum.Position));
        _selected = null;
        Refresh();
        Show("Deleted. Undo brings it back.");
    }

    // ------------------------------------------------------------------ words

    private static string LengthName(int sixteenths) => sixteenths switch
    {
        16 => "whole note", 12 => "dotted half", 8 => "half note", 6 => "dotted quarter", 4 => "quarter note", 3 => "dotted eighth", 2 => "eighth note", 1 => "sixteenth note", _ => $"{sixteenths} sixteenths",
    };

    private static string Article(string word) => word.StartsWith('e') ? "an" : "a";

    private static string BeatName(int position) =>
        position % 4 == 0 ? $"beat {position / 4 + 1}" : $"after beat {position / 4 + 1}";

    private void Show(string message) => StatusText.Text = message;
}
