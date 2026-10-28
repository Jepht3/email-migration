using CommunityToolkit.Mvvm.ComponentModel;
using MailMigrator.Core.Abstractions;
using MailMigrator.Mobile.Services;

namespace MailMigrator.Mobile.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IMigrationRunner _runner;

    public SettingsViewModel(IMigrationRunner runner)
    {
        _runner = runner;
        _themeIndex = MobileSettings.Theme;
        _maxConcurrency = MobileSettings.MaxConcurrency;
        _erasePasswords = MobileSettings.ErasePasswords;
    }

    public IReadOnlyList<string> Themes { get; } = ["Selon le téléphone", "Clair", "Sombre"];

    [ObservableProperty] private int _themeIndex;
    // double : type de Stepper.Value.
    [ObservableProperty] private double _maxConcurrency;
    [ObservableProperty] private bool _erasePasswords;

    public string ConcurrencyLabel => MaxConcurrency == 1 ? "1 migration à la fois" : $"{MaxConcurrency} migrations en même temps";

    public string Version => $"Version {AppInfo.Current.VersionString}";

    partial void OnThemeIndexChanged(int value)
    {
        if (value < 0) return;
        MobileSettings.Theme = value;
        if (Application.Current is { } app) app.UserAppTheme = MobileSettings.ToAppTheme(value);
    }

    partial void OnMaxConcurrencyChanged(double value)
    {
        MobileSettings.MaxConcurrency = (int)Math.Round(value);
        _runner.MaxConcurrency = MobileSettings.MaxConcurrency;
        OnPropertyChanged(nameof(ConcurrencyLabel));
    }

    partial void OnErasePasswordsChanged(bool value) => MobileSettings.ErasePasswords = value;
}
