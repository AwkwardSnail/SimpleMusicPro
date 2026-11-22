using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SimpleMusicPro.Score;

namespace SimpleMusicPro.App;

/// <summary>
/// A small chord diagram for the list of chords, drawn the way the printed one is: strings upright with the low E
/// at the left, the nut (or the first fret's number), a dot for each finger, a bar for a finger across strings,
/// o over an open string and × over one not played.
/// </summary>
public sealed class ChordIcon : FrameworkElement
{
    private const double Gap = 9, Fret = 10, Top = 12, Left = 8;
    private readonly ChordShape _shape;
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x7F, 0xDC, 0x8A));
    private static readonly Pen Thin = new(Ink, 1);
    private static readonly Typeface Small = new("Segoe UI");

    public ChordIcon(ChordShape shape)
    {
        _shape = shape;
        Width = Left + 5 * Gap + 20;
        Height = Top + 5 * Fret + 4;
    }

    protected override void OnRender(DrawingContext dc)
    {
        int frets = _shape.FretsShown, first = _shape.FirstFret;
        double w = 5 * Gap;
        for (int i = 0; i < 6; i++) dc.DrawLine(Thin, new Point(Left + i * Gap, Top), new Point(Left + i * Gap, Top + frets * Fret));
        for (int j = 0; j <= frets; j++) dc.DrawLine(Thin, new Point(Left, Top + j * Fret), new Point(Left + w, Top + j * Fret));
        if (first == 1) dc.DrawRectangle(Ink, null, new Rect(Left - 0.5, Top - 3, w + 1, 3));
        else
        {
            var label = new FormattedText($"{first}fr", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Small, 9, Ink, 1.0);
            dc.DrawText(label, new Point(Left + w + 3, Top + Fret / 2 - label.Height / 2));
        }
        double FretY(int f) => Top + (f - first + 0.5) * Fret;
        if (_shape.Barre is { } barre)
            dc.DrawRectangle(Ink, null, new Rect(Left + barre.FromString * Gap, FretY(barre.Fret) - 2.5, (5 - barre.FromString) * Gap, 5));
        for (int i = 0; i < 6; i++)
        {
            double x = Left + i * Gap;
            int f = _shape.Frets[i];
            if (f < 0)
            {
                dc.DrawLine(Thin, new Point(x - 2.5, Top - 9), new Point(x + 2.5, Top - 4));
                dc.DrawLine(Thin, new Point(x - 2.5, Top - 4), new Point(x + 2.5, Top - 9));
            }
            else if (f == 0) dc.DrawEllipse(null, Thin, new Point(x, Top - 6.5), 2.6, 2.6);
            else dc.DrawEllipse(Ink, null, new Point(x, FretY(f)), 3.3, 3.3);
        }
    }
}
