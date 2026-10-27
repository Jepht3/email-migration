using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MailMigrator.App.Services;

namespace MailMigrator.App.Controls;

/// <summary>Flèche animée source → destination : trois points glissent vers la droite.</summary>
public sealed class FlowArrow : Grid
{
    public FlowArrow()
    {
        Width = 56;
        Height = 40;
        IsHitTestVisible = false;

        var bubble = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20) };
        bubble.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        var arrow = new Path
        {
            Data = Geometry.Parse("M11,20 H29 M23,14 L29,20 L23,26"),
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        arrow.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        var arrowTransform = new TranslateTransform();
        arrow.RenderTransform = arrowTransform;
        bubble.Child = arrow;
        Children.Add(bubble);

        Loaded += (_, _) =>
        {
            if (!Motion.IsEnabled) return;
            var nudge = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1600) };
            nudge.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            nudge.KeyFrames.Add(new EasingDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800)), new SineEase()));
            nudge.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600)), new SineEase()));
            arrowTransform.BeginAnimation(TranslateTransform.XProperty, nudge);
        };
        Unloaded += (_, _) => arrowTransform.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
