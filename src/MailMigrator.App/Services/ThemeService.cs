using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MailMigrator.App.Services;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Thème clair / sombre (automatique selon Windows ou forcé) et couleur d'accent.
/// Remplace à chaud le dictionnaire Colors.*.xaml : les vues n'utilisent que des DynamicResource.
/// </summary>
public sealed class ThemeService
{
    private Color? _customAccent;

    public ThemeChoice Choice { get; private set; } = ThemeChoice.System;
    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Initialize()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Choice == ThemeChoice.System)
                Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
        };
        Apply(ThemeChoice.System);
    }

    public void Apply(ThemeChoice choice)
    {
        Choice = choice;
        IsDark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark,
        };

        ApplicationThemeManager.Apply(IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, false);
        SwapColors(IsDark ? "Colors.Dark.xaml" : "Colors.Light.xaml");
        ApplyAccent();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Couleur d'accent choisie dans les paramètres (null = charte par défaut).</summary>
    public void SetAccent(Color? accent)
    {
        _customAccent = accent;
        ApplyAccent();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public Color DefaultAccent => (Color)Application.Current.Resources["Color.Accent.Light"];

    private void ApplyAccent()
    {
        var resources = Application.Current.Resources;
        var accent = _customAccent is { } custom
            ? (IsDark ? Lighten(custom, 0.18) : custom)
            : (Color)resources[IsDark ? "Color.Accent.Dark" : "Color.Accent.Light"];

        ApplicationAccentColorManager.Apply(accent, IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light);

        resources["Brush.Accent"] = Frozen(new SolidColorBrush(accent));
        resources["Brush.AccentSoft"] = Frozen(new SolidColorBrush(Color.FromArgb(IsDark ? (byte)0x33 : (byte)0x1F, accent.R, accent.G, accent.B)));
        resources["Color.AccentGradientStart"] = Lighten(accent, 0.35);
        resources["Color.AccentGradientEnd"] = accent;
    }

    private static void SwapColors(string fileName)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Colors.") == true);
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/LewebmaxMailMigrator;component/Themes/{fileName}") };
        if (current is null) dictionaries.Add(next);
        else dictionaries[dictionaries.IndexOf(current)] = next;
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
