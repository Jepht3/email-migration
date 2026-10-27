using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MailMigrator.App.Services;

namespace MailMigrator.App.Controls;

/// <summary>Bloc de chargement qui « respire » pendant une détection ou une analyse.</summary>
public sealed class Skeleton : Border
{
    public Skeleton()
    {
        CornerRadius = new CornerRadius(6);
        Height = 12;
        SetResourceReference(BackgroundProperty, "Brush.Skeleton");
        IsVisibleChanged += (_, _) => Update();
        Loaded += (_, _) => Update();
    }

    private void Update()
    {
        if (!IsVisible || !Motion.IsEnabled)
        {
            BeginAnimation(OpacityProperty, null);
            return;
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(800))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }
}
