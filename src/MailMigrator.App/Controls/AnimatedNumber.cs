using System.Windows;
using System.Windows.Controls;
using MailMigrator.App.Services;
using MailMigrator.Core.Formatting;

namespace MailMigrator.App.Controls;

public enum NumberFormat
{
    Integer,
    Bytes,
    Percent,
    Speed,
}

/// <summary>Compteur qui interpole vers sa nouvelle valeur au lieu de sauter.</summary>
public sealed class AnimatedNumber : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AnimatedNumber),
        new PropertyMetadata(0d, (d, e) => Motion.Animate((AnimatedNumber)d, DisplayValueProperty!, (double)e.NewValue, 350)));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(AnimatedNumber),
        new PropertyMetadata(0d, (d, _) => ((AnimatedNumber)d).Refresh()));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(NumberFormat), typeof(AnimatedNumber),
        new PropertyMetadata(NumberFormat.Integer, (d, _) => ((AnimatedNumber)d).Refresh()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }
    public NumberFormat Format { get => (NumberFormat)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    public AnimatedNumber() => Refresh();

    private void Refresh()
    {
        var v = DisplayValue;
        Text = Format switch
        {
            NumberFormat.Bytes => Core.Formatting.Format.Bytes(v),
            NumberFormat.Percent => Core.Formatting.Format.Percent(v),
            NumberFormat.Speed => Core.Formatting.Format.Speed(v),
            _ => Core.Formatting.Format.Number(Math.Round(v)),
        };
    }
}
