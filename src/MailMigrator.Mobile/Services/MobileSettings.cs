namespace MailMigrator.Mobile.Services;

/// <summary>Réglages de l'utilisateur, conservés dans les préférences Android.</summary>
public static class MobileSettings
{
    /// <summary>0 = selon le téléphone, 1 = clair, 2 = sombre.</summary>
    public static int Theme
    {
        get => Preferences.Default.Get("theme", 0);
        set => Preferences.Default.Set("theme", value);
    }

    public static int MaxConcurrency
    {
        get => Math.Clamp(Preferences.Default.Get("maxConcurrency", 2), 1, 5);
        set => Preferences.Default.Set("maxConcurrency", Math.Clamp(value, 1, 5));
    }

    public static bool ErasePasswords
    {
        get => Preferences.Default.Get("erasePasswords", true);
        set => Preferences.Default.Set("erasePasswords", value);
    }

    public static AppTheme ToAppTheme(int theme) => theme switch
    {
        1 => AppTheme.Light,
        2 => AppTheme.Dark,
        _ => AppTheme.Unspecified,
    };
}
