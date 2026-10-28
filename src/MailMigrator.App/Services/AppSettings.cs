using System.IO;
using System.Text.Json;
using Serilog;

namespace MailMigrator.App.Services;

/// <summary>Paramètres de l'utilisateur, conservés d'un lancement à l'autre (settings.json).</summary>
public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>Couleur d'accent « #RRGGBB » ; null = charte LEWEBMAX.</summary>
    public string? Accent { get; set; }
    public int MaxConcurrency { get; set; } = 5;
    public bool ErasePasswords { get; set; } = true;
    public string ReportsFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LEWEBMAX", "Rapports de migration");

    private static string FilePath => Path.Combine(App.DataFolder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                settings.MaxConcurrency = Math.Clamp(settings.MaxConcurrency, 1, 5);
                return settings;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Paramètres illisibles, valeurs par défaut utilisées");
        }
        return new AppSettings();
    }

    public void Save()
    {
        // Le mode démonstration ne touche pas aux réglages réels.
        if (App.IsDemo) return;
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossible d'enregistrer les paramètres");
        }
    }
}
