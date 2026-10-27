using System.Windows;
using System.Windows.Media.Animation;

namespace MailMigrator.App.Services;

/// <summary>
/// Point d'entrée unique des animations : durées de 150 à 250 ms, easing doux,
/// et respect du réglage Windows « Effets d'animation ».
/// </summary>
public static class Motion
{
    public static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static Duration Fast => TimeSpan.FromMilliseconds(150);
    public static Duration Normal => TimeSpan.FromMilliseconds(220);

    /// <summary>Faux si l'utilisateur a désactivé les effets d'animation dans Windows.</summary>
    public static bool IsEnabled => SystemParameters.ClientAreaAnimation && !ForceDisabled;

    /// <summary>Permet de figer les animations (captures d'écran reproductibles).</summary>
    public static bool ForceDisabled { get; set; }

    public static void Animate(IAnimatable target, DependencyProperty property, double to, double milliseconds = 220)
    {
        if (!IsEnabled)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Ease };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static void Animate(IAnimatable target, DependencyProperty property, double from, double to, double milliseconds)
    {
        if (!IsEnabled)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Ease };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
