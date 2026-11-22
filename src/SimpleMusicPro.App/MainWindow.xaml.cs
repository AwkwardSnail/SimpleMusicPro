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
/// print on the right, and the toolbar with the length of the next strum. Strums are added by clicking the + signs
/// laid over the pages; the + signs are never printed.
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
                ? $"Started a score from {System.IO.Path.GetFileName(path)}. Click a note in the voice staff, then the + above it to add a strum."
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
        _session.Change(p => p.VoiceTrack = track);
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
        else DrawOverlays();   // the + sign says what it will add
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
        if (ctrl && e.Key == Key.Z) { Undo_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { Save(saveAs: shift); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { Open_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Delete && _selected is { Lane: LaneKind.Rhythm } strum) { DeleteStrum(strum); e.Handled = true; }
        else if (e.Key == Key.Delete && _selected is { Lane: LaneKind.Chords } chord) { DeleteChord(chord); e.Handled = true; }
        else if (e.Key == Key.Delete && _selected is { Lane: LaneKind.Lyrics } word) { DeleteLyric(word); e.Handled = true; }
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

    /// <summary>Draws the score again from what the session holds, and lays the + signs over it.</summary>
    private void Refresh()
    {
        UpdateTitle();
        UndoButton.IsEnabled = _session?.CanUndo == true;
        RedoButton.IsEnabled = _session?.CanRedo == true;
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

    /// <summary>The selection, and the + signs where a strum can be added.</summary>
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

        (int, int)? note = _selected is { Lane: LaneKind.Voice } v ? (v.Bar, v.Position) : null;
        foreach (var plus in Placeholders.ForRhythm(_score, note))
        {
            var bar = _engraved.Bars.FirstOrDefault(b => b.Bar == plus.Bar);
            var band = _engraved.Bands.FirstOrDefault(b => b.Lane == LaneKind.Rhythm && b.FirstBar <= plus.Bar && plus.Bar <= b.LastBar);
            if (bar is null || band is null || bar.Page != band.Page) continue;
            // The + sits where the slash would go: on the slashes' line, at the moment's place.
            double x = bar.XOf(plus.Position) * Scale, y = (band.Bottom - 2.2 * 5.6) * Scale;
            var sign = PlusSign(plus);
            Canvas.SetLeft(sign, x - 2);
            Canvas.SetTop(sign, y - 9);
            _overlays[bar.Page].Children.Add(sign);
        }

        // The chord row's +: over a note or a strum, on the beat it falls in.
        (int, int)? moment = _selected is { Lane: LaneKind.Voice or LaneKind.Rhythm } m ? (m.Bar, m.Position) : null;
        foreach (var plus in Placeholders.ForChords(_score, moment))
        {
            var bar = _engraved.Bars.FirstOrDefault(b => b.Bar == plus.Bar);
            var band = _engraved.Bands.FirstOrDefault(b => b.Lane == LaneKind.Chords && b.FirstBar <= plus.Bar && plus.Bar <= b.LastBar);
            if (bar is null || band is null || bar.Page != band.Page) continue;
            // Where the diagram would stand: its middle over the note, halfway down the row.
            double x = (bar.XOf(plus.Position) + 0.6 * 5.6) * Scale, y = (band.Top + 5.5 * 5.6) * Scale;
            string what = $"Add a chord here (bar {_score.Bars[plus.Bar].Number}, {BeatName(plus.Position)})";
            var sign = new Button { Template = PlusTemplate, Width = 18, Height = 18, Cursor = Cursors.Hand, ToolTip = what };
            System.Windows.Automation.AutomationProperties.SetName(sign, what);
            var overlay = _overlays[bar.Page];
            var anchor = new Rect(x - 9, y - 9, 18, 18);
            sign.Click += (_, _) => OpenChordPicker(overlay, anchor, plus.Bar, plus.Position, null);
            Canvas.SetLeft(sign, x - 9);
            Canvas.SetTop(sign, y - 9);
            overlay.Children.Add(sign);
        }

        // The words' +: under the note, where its syllable would be written.
        foreach (var plus in Placeholders.ForLyrics(_score, note))
        {
            if (LyricPlace(plus.Bar, plus.Position) is not { } at) continue;
            string what = $"Add words here (bar {_score.Bars[plus.Bar].Number}, {BeatName(plus.Position)})";
            var sign = new Button { Template = PlusTemplate, Width = 18, Height = 18, Cursor = Cursors.Hand, ToolTip = what };
            System.Windows.Automation.AutomationProperties.SetName(sign, what);
            sign.Click += (_, _) => OpenLyricEditor(plus.Bar, plus.Position);
            Canvas.SetLeft(sign, at.X - 9);
            Canvas.SetTop(sign, at.Y - 9);
            _overlays[at.Page].Children.Add(sign);
        }
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

    private static readonly ControlTemplate PlusTemplate = MakePlusTemplate();

    private static ControlTemplate MakePlusTemplate()
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        ring.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)));
        ring.SetValue(Border.BorderBrushProperty, Green);
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, "+");
        text.SetValue(TextBlock.ForegroundProperty, Green);
        text.SetValue(TextBlock.FontSizeProperty, 15.0);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(0, -3, 0, 0));
        ring.AppendChild(text);
        return new ControlTemplate(typeof(Button)) { VisualTree = ring };
    }

    private FrameworkElement PlusSign(Placeholder plus)
    {
        // A button (drawn as a small ringed +), so it can also be reached with the keyboard and read out.
        string length = LengthName(StrumLength);
        string what = $"Add {Article(length)} {length}{(RestButton.IsChecked == true ? " rest" : " strum")} here (bar {_score!.Bars[plus.Bar].Number}, {BeatName(plus.Position)})";
        var sign = new Button { Template = PlusTemplate, Width = 18, Height = 18, Cursor = Cursors.Hand, ToolTip = what };
        System.Windows.Automation.AutomationProperties.SetName(sign, what);
        sign.Click += (_, _) => AddStrum(plus);
        return sign;
    }

    private void AddStrum(Placeholder plus)
    {
        if (_session is null || _score is null) return;
        int length = StrumLength;
        try
        {
            var cut = _score.Rhythm?.At(plus.Bar, plus.Position);   // a strum sounding on there is shortened by the new one
            var added = _session.AddStrum(plus.Bar, plus.Position, length, RestButton.IsChecked == true, RestButton.IsChecked != true && AccentButton.IsChecked == true);
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
            Show(where + (strum && chord ? "click a + above it to add a strum or a chord there."
                : strum ? "click the + above it to add a strum there." + noChord
                : chord ? "a strum starts there already (click it to change it); the + in the chord row adds a chord."
                : "a strum starts there already: click it to change it." + noChord)
                + (Placeholders.ForLyrics(_score, (v.Bar, v.Position)).Count > 0 ? " The + under it adds words." : ""));
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
