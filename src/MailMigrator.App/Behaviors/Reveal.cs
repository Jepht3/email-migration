using System.Windows;
using System.Windows.Media;
using MailMigrator.App.Services;

namespace MailMigrator.App.Behaviors;

/// <summary>Élément qui ne doit apparaître en fondu qu'une seule fois (nouvelle ligne d'activité).</summary>
public interface IFreshItem
{
    /// <summary>Vrai la première fois seulement.</summary>
    bool ConsumeFresh();
}

/// <summary>
/// Apparition en fondu + léger glissement :
/// <c>OnShow</c> à chaque fois que l'élément devient visible (étapes de l'assistant),
/// <c>OnLoad</c> au chargement si l'élément de données est nouveau (flux d'activité).
/// </summary>
public static class Reveal
{
    public static readonly DependencyProperty OnShowProperty = DependencyProperty.RegisterAttached(
        "OnShow", typeof(bool), typeof(Reveal), new PropertyMetadata(false, OnShowChanged));

    public static readonly DependencyProperty OnLoadProperty = DependencyProperty.RegisterAttached(
        "OnLoad", typeof(bool), typeof(Reveal), new PropertyMetadata(false, OnLoadChanged));

    public static bool GetOnShow(DependencyObject d) => (bool)d.GetValue(OnShowProperty);
    public static void SetOnShow(DependencyObject d, bool value) => d.SetValue(OnShowProperty, value);
    public static bool GetOnLoad(DependencyObject d) => (bool)d.GetValue(OnLoadProperty);
    public static void SetOnLoad(DependencyObject d, bool value) => d.SetValue(OnLoadProperty, value);

    private static void OnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        element.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) Play(element, 12, 0, 220);
        };
    }

    private static void OnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        element.Loaded += (_, _) =>
        {
            if (element.DataContext is IFreshItem fresh && fresh.ConsumeFresh()) Play(element, 0, 6, 200);
        };
        element.DataContextChanged += (_, _) =>
        {
            // Conteneur recyclé : on ne rejoue l'apparition que pour un élément vraiment nouveau.
            if (element.IsLoaded && element.DataContext is IFreshItem fresh && fresh.ConsumeFresh()) Play(element, 0, 6, 200);
        };
    }

    private static void Play(FrameworkElement element, double fromX, double fromY, double ms)
    {
        if (!Motion.IsEnabled) return;
        var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = translate;
        Motion.Animate(element, UIElement.OpacityProperty, 0, 1, ms);
        if (fromX != 0) Motion.Animate(translate, TranslateTransform.XProperty, fromX, 0, ms);
        if (fromY != 0) Motion.Animate(translate, TranslateTransform.YProperty, fromY, 0, ms);
    }
}
