using System.Windows;
using System.Windows.Media;

namespace MailMigrator.App.Controls;

/// <summary>Courbe compacte du débit (60 dernières secondes).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Incrémenté par le ViewModel pour forcer le redessin quand la liste change.</summary>
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineColorProperty = DependencyProperty.Register(
        nameof(LineColor), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Colors.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public Color LineColor { get => (Color)GetValue(LineColorProperty); set => SetValue(LineColorProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var values = Values;
        if (values is null || values.Count < 2 || w <= 0 || h <= 0) return;

        var max = Math.Max(values.Max() * 1.15, 1e-9);
        var step = w / Math.Max(1, Capacity - 1);
        var offset = w - step * (values.Count - 1);
        var top = 4.0;
        Point At(int i) => new(offset + i * step, top + (h - top - 1) * (1 - values[i] / max));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(At(0), false, false);
            a.BeginFigure(new Point(At(0).X, h), true, true);
            a.LineTo(At(0), false, false);
            for (var i = 1; i < values.Count; i++)
            {
                // Courbe lissée : point de contrôle à mi-chemin horizontal.
                var p0 = At(i - 1);
                var p1 = At(i);
                var mid = (p0.X + p1.X) / 2;
                l.BezierTo(new Point(mid, p0.Y), new Point(mid, p1.Y), p1, true, true);
                a.BezierTo(new Point(mid, p0.Y), new Point(mid, p1.Y), p1, false, false);
            }
            a.LineTo(new Point(At(values.Count - 1).X, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        var c = LineColor;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 2) { LineJoin = PenLineJoin.Round }, line);

        var last = At(values.Count - 1);
        dc.DrawEllipse(new SolidColorBrush(c), new Pen(Brushes.White, 1.5), last, 3.5, 3.5);
    }
}
