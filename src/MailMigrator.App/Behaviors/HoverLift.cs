using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MailMigrator.App.Services;

namespace MailMigrator.App.Behaviors;

/// <summary>Survol : la carte s'élève légèrement ; clic : léger enfoncement immédiat.</summary>
public static class HoverLift
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(HoverLift), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        var translate = new TranslateTransform();
        element.RenderTransform = translate;
        element.MouseEnter += (_, _) => Lift(element, translate, -2, 28, 0.12);
        element.MouseLeave += (_, _) => Lift(element, translate, 0, 16, 0.06);
        element.PreviewMouseLeftButtonDown += (_, _) => Motion.Animate(translate, TranslateTransform.YProperty, 0, 80);
        element.PreviewMouseLeftButtonUp += (_, _) => Motion.Animate(translate, TranslateTransform.YProperty, -2, 120);
        element.KeyDown += (_, args) =>
        {
            // Entrée / Espace sur une carte cliquable = clic.
            if (args.Key is Key.Enter or Key.Space && element.InputBindings.OfType<MouseBinding>().FirstOrDefault() is { } binding
                && binding.Command?.CanExecute(binding.CommandParameter) == true)
            {
                binding.Command.Execute(binding.CommandParameter);
                args.Handled = true;
            }
        };
    }

    private static void Lift(FrameworkElement element, TranslateTransform translate, double y, double blur, double opacity)
    {
        Motion.Animate(translate, TranslateTransform.YProperty, y, 180);
        if (element.Effect is DropShadowEffect shadow)
        {
            if (shadow.IsFrozen) element.Effect = shadow = shadow.Clone();
            Motion.Animate(shadow, DropShadowEffect.BlurRadiusProperty, blur, 180);
            Motion.Animate(shadow, DropShadowEffect.OpacityProperty, opacity, 180);
        }
    }
}
