using System.Windows;
using System.Windows.Media;

namespace MailMigrator.App.Services;

/// <summary>Accès aux couleurs d'identité des tâches définies dans Themes/JobPalette.xaml.</summary>
public static class JobPalette
{
    public const int Count = 5;

    private static readonly Dictionary<(int, string), Brush> Cache = [];

    public static Color Start(int index) => Lookup(index, "Start");
    public static Color End(int index) => Lookup(index, "End");

    public static Brush Solid(int index) => Cached(index, "solid", () => new SolidColorBrush(End(index)));

    public static Brush Soft(int index) => Cached(index, "soft", () =>
    {
        var c = End(index);
        return new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
    });

    public static Brush Gradient(int index) => Cached(index, "gradient", () =>
        new LinearGradientBrush(Start(index), End(index), new Point(0, 0), new Point(1, 1)));

    private static Color Lookup(int index, string part)
    {
        var key = $"Color.Job{((index % Count) + Count) % Count}.{part}";
        return Application.Current.TryFindResource(key) is Color c ? c : Colors.SteelBlue;
    }

    private static Brush Cached(int index, string kind, Func<Brush> factory)
    {
        index = ((index % Count) + Count) % Count;
        if (!Cache.TryGetValue((index, kind), out var brush))
        {
            brush = factory();
            brush.Freeze();
            Cache[(index, kind)] = brush;
        }
        return brush;
    }
}
