using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MailMigrator.App.Services;

namespace MailMigrator.App.Controls;

/// <summary>Grande coche de fin : le cercle puis la coche se dessinent.</summary>
public sealed class AnimatedCheck : Viewbox
{
    private readonly Ellipse _halo = new() { Width = 64, Height = 64 };
    private readonly Ellipse _ring = new() { Width = 56, Height = 56, StrokeThickness = 4 };
    private readonly Path _tick = new()
    {
        Data = Geometry.Parse("M20,33 L28.5,41.5 L45,24"),
        StrokeThickness = 4.5,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    public AnimatedCheck()
    {
        var canvas = new Canvas { Width = 64, Height = 64 };
        Canvas.SetLeft(_ring, 4);
        Canvas.SetTop(_ring, 4);
        canvas.Children.Add(_halo);
        canvas.Children.Add(_ring);
        canvas.Children.Add(_tick);
        Child = canvas;

        _halo.SetResourceReference(Shape.FillProperty, "Brush.Status.SuccessSoft");
        _ring.SetResourceReference(Shape.StrokeProperty, "Brush.Status.Success");
        _tick.SetResourceReference(Shape.StrokeProperty, "Brush.Status.Success");

        // Longueurs exprimées en épaisseurs de trait (unité des StrokeDashArray).
        _ring.StrokeDashArray = [Math.PI * 52 / 4, 100];
        _tick.StrokeDashArray = [40 / 4.5, 100];
        Loaded += (_, _) => Play();
    }

    public void Play()
    {
        if (!Motion.IsEnabled)
        {
            _ring.StrokeDashOffset = 0;
            _tick.StrokeDashOffset = 0;
            return;
        }
        var ease = Motion.Ease;
        _ring.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(Math.PI * 52 / 4, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
        _tick.StrokeDashOffset = 40 / 4.5;
        _tick.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(40 / 4.5, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(350), EasingFunction = ease });
        var scale = new ScaleTransform(0.6, 0.6, 32, 32);
        _halo.RenderTransform = scale;
        var grow = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }
}
