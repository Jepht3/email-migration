using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MailMigrator.App.Controls;
using MailMigrator.App.Services;
using MailMigrator.Core.Formatting;
using MailMigrator.Core.Models;

namespace MailMigrator.App.Converters;

public abstract class OneWayConverter : IValueConverter
{
    public abstract object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Index de tâche → brosse : Solid (défaut), Soft, Gradient ; ou couleur : Start, End.</summary>
public sealed class JobColorConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var index = value is int i ? i : 0;
        return (parameter as string) switch
        {
            "Soft" => JobPalette.Soft(index),
            "Gradient" => JobPalette.Gradient(index),
            "Start" => JobPalette.Start(index),
            "End" => JobPalette.End(index),
            _ => JobPalette.Solid(index),
        };
    }
}

/// <summary>« jean.dupont@x.fr » → « JD ».</summary>
public sealed class InitialsConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var local = (value as string ?? "").Split('@')[0];
        var parts = local.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}",
        };
    }
}

public sealed class BytesConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? "—" : Format.Bytes(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
}

public sealed class NumberConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? "—" : Format.Number(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
}

public sealed class DurationConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Format.Duration(value as TimeSpan?);
}

/// <summary>Date courte française ; paramètre « long » pour inclure l'heure.</summary>
public sealed class DateConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset d when parameter as string == "long" => d.LocalDateTime.ToString("d MMM yyyy 'à' HH:mm", Format.French),
        DateTimeOffset d when parameter as string == "time" => d.LocalDateTime.ToString("HH:mm:ss", Format.French),
        DateTimeOffset d => d.LocalDateTime.ToString("dd/MM/yyyy", Format.French),
        _ => "—",
    };
}

/// <summary>bool → Visibility ; paramètre « invert » pour inverser.</summary>
public sealed class BoolToVisibilityConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Visible si la valeur est non nulle (et non vide pour une chaîne, non nulle pour un nombre).</summary>
public sealed class HasValueToVisibilityConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => s.Length > 0,
            int i => i != 0,
            _ => true,
        };
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Visible si la valeur est égale au paramètre (étape de l'assistant, filtre…).</summary>
public sealed class EqualsToVisibilityConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string s && targetType.IsEnum ? Enum.Parse(targetType, s) : Binding.DoNothing;
}

public sealed class StatusTextConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is JobStatus s ? StatusVisuals.For(s).Text : "";
}

public sealed class FolderSymbolConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        StatusVisuals.ForFolder(value is SpecialFolder f ? f : SpecialFolder.None);
}

/// <summary>Couleur hexadécimale (pastille fournisseur) → brosse.</summary>
public sealed class HexBrushConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex) return Brushes.Gray;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>bool → « Libellé : oui / non » (libellé en paramètre).</summary>
public sealed class YesNoConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        $"{parameter} : {(value is true ? "oui" : "non")}";
}

/// <summary>Fraction 0–1 → « 62 % ».</summary>
public sealed class PercentConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? Format.Percent(d) : "—";
}
