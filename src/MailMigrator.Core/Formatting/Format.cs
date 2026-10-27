using System.Globalization;

namespace MailMigrator.Core.Formatting;

/// <summary>Mise en forme française des tailles, durées et nombres (interface et rapports).</summary>
public static class Format
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly string[] Units = ["octets", "Ko", "Mo", "Go", "To"];

    public static string Bytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes.ToString("0", French)} octets";
        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        var pattern = bytes >= 100 ? "0" : bytes >= 10 ? "0.#" : "0.##";
        return $"{bytes.ToString(pattern, French)} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) => $"{Bytes(bytesPerSecond)}/s";

    public static string Number(double value) => value.ToString("N0", French);

    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } d || d < TimeSpan.Zero) return "—";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours} h {d.Minutes:00} min";
        if (d.TotalMinutes >= 1) return $"{d.Minutes} min {d.Seconds:00} s";
        return $"{Math.Max(0, d.Seconds)} s";
    }

    public static string Percent(double fraction) => (fraction * 100).ToString("0", French) + " %";
}
