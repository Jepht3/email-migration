using System.Windows;
using System.Windows.Controls;

namespace MailMigrator.App.Controls;

/// <summary>
/// Grille fluide : autant de colonnes de largeur égale que possible (≥ MinItemWidth),
/// de la fenêtre minimale 1100 px jusqu'au plein écran 4K.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(380d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    private int Columns(double width)
    {
        if (double.IsInfinity(width)) return 1;
        var columns = (int)Math.Floor((width + Spacing) / (MinItemWidth + Spacing));
        return Math.Clamp(columns, 1, Math.Max(1, MaxColumns));
    }

    protected override Size MeasureOverride(Size available)
    {
        var columns = Columns(available.Width);
        var itemWidth = double.IsInfinity(available.Width) ? MinItemWidth : (available.Width - Spacing * (columns - 1)) / columns;
        double total = 0, rowHeight = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if ((i + 1) % columns == 0 || i == InternalChildren.Count - 1)
            {
                total += rowHeight + (i == InternalChildren.Count - 1 ? 0 : Spacing);
                rowHeight = 0;
            }
        }
        return new Size(double.IsInfinity(available.Width) ? itemWidth : available.Width, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width);
        var itemWidth = (final.Width - Spacing * (columns - 1)) / columns;
        double y = 0;
        for (var start = 0; start < InternalChildren.Count; start += columns)
        {
            var end = Math.Min(start + columns, InternalChildren.Count);
            double rowHeight = 0;
            for (var i = start; i < end; i++) rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);
            for (var i = start; i < end; i++)
                InternalChildren[i].Arrange(new Rect((i - start) * (itemWidth + Spacing), y, itemWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return final;
    }
}
