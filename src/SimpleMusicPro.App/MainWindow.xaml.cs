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
            RhythmCheck.IsChecked = _session.Project.ShowRhythm;
            RhythmLabelBox.Text = _session.Project.RhythmLabel;
            VoiceCheck.IsChecked = _session.Project.ShowVoice;
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
        bool rhythm = RhythmCheck.IsChecked == true, voice = VoiceCheck.IsChecked == true;
        if (!rhythm && !voice)
        {
            // A score needs at least one staff.
            ((CheckBox)sender).IsChecked = true;
            Show("A score needs at least one staff.");
            return;
        }
        _session.Change(p => { p.ShowRhythm = rhythm; p.ShowVoice = voice; });
        _selected = null;
        Refresh();
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
            foreach (var b in new[] { "16", "8", "4", "2" })
                if (FindRadio(b) is { IsChecked: true }) length = int.Parse(b);
            if (DotButton.IsChecked == true && length is 4 or 8) length = length * 3 / 2;
            return length;
        }
    }

    private RadioButton? FindRadio(string tag) =>
        LogicalTreeHelper.GetChildren((DependencyObject)QuarterButton.Parent).OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tag);

    private void Length_Checked(object sender, RoutedEventArgs e)
    {
        if (DotButton is null) return; // still loading
        // A dot only goes with a quarter or a half: a dotted eighth would need a sixteenth, a dotted whole would not fit.
        bool dottable = sender is RadioButton { Tag: "4" or "8" };
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
            16 => ("16", false), 12 => ("8", true), 8 => ("8", false), 6 => ("4", true), 4 => ("4", false), 2 => ("2", false), _ => (null, false),
        };
        _updating = true;
        try
        {
            if (tag is not null && FindRadio(tag) is { } radio)
            {
                radio.IsChecked = true;
                DotButton.IsEnabled = tag is "4" or "8";
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
            var added = _session.AddStrum(plus.Bar, plus.Position, length, RestButton.IsChecked == true, RestButton.IsChecked != true && AccentButton.IsChecked == true);
            Refresh();
            SelectStrum(added);   // selected, so a length chosen now changes it
            if (added.Length < length) Show($"Only a {LengthName(added.Length)} fits before the end of the bar, so the strum is that long.");
            else Show("Added. To change it, choose another length, Dot, Rest or Accent while it is selected.");
        }
        catch (ArgumentOutOfRangeException ex) { Show(ex.Message.Split(" (Parameter")[0]); }
    }

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
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
        else if (_selected is { Lane: LaneKind.Voice } v && _score!.Rhythm is not null)
            Show(Placeholders.ForRhythm(_score, (v.Bar, v.Position)).Count > 0
                ? $"Bar {_score.Bars[v.Bar].Number}, {BeatName(v.Position)}: click the + above it to add a strum there."
                : $"Bar {_score.Bars[v.Bar].Number}, {BeatName(v.Position)}: a strum sounds there already.");
        DrawOverlays();
    }

    private void Overlay_RightClick(object sender, MouseButtonEventArgs e)
    {
        Overlay_MouseDown(sender, e);
        if (_selected is not { Lane: LaneKind.Rhythm } strum || _session is null) return;
        var hit = _score!.Rhythm?.At(strum.Bar, strum.Position);
        if (hit is null) return;
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
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
        16 => "whole note", 12 => "dotted half", 8 => "half note", 6 => "dotted quarter", 4 => "quarter note", 2 => "eighth note", _ => $"{sixteenths} sixteenths",
    };

    private static string Article(string word) => word.StartsWith('e') ? "an" : "a";

    private static string BeatName(int position) =>
        position % 4 == 0 ? $"beat {position / 4 + 1}" : $"after beat {position / 4 + 1}";

    private void Show(string message) => StatusText.Text = message;
}
