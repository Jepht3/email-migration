using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.Core.Abstractions;
using Microsoft.Win32;

namespace MailMigrator.App.ViewModels;

public sealed partial class AccentOption(string name, Color color) : ObservableObject
{
    public string Name { get; } = name;
    public Color Color { get; } = color;
    public SolidColorBrush Brush { get; } = new(color);

    [ObservableProperty] private bool _isSelected;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly IMigrationRunner _runner;

    private readonly AppSettings _settings;

    public SettingsViewModel(ThemeService theme, IMigrationRunner runner, AppSettings settings)
    {
        _theme = theme;
        _runner = runner;
        _settings = settings;
        Accents =
        [
            new("Bleu LEWEBMAX", theme.DefaultAccent),
            new("Indigo", Color.FromRgb(0x4F, 0x46, 0xE5)),
            new("Violet", Color.FromRgb(0x7C, 0x3A, 0xED)),
            new("Turquoise", Color.FromRgb(0x0D, 0x94, 0x88)),
            new("Orange", Color.FromRgb(0xEA, 0x58, 0x0C)),
        ];

        // Réglages enregistrés : appliqués dès le démarrage.
        _themeChoice = settings.Theme;
        _maxConcurrency = settings.MaxConcurrency;
        _erasePasswords = settings.ErasePasswords;
        _reportsFolder = settings.ReportsFolder;
        theme.Apply(settings.Theme);
        runner.MaxConcurrency = settings.MaxConcurrency;
        var accent = Accents.Skip(1).FirstOrDefault(a => a.Color.ToString() == settings.Accent) ?? Accents[0];
        accent.IsSelected = true;
        if (accent != Accents[0]) theme.SetAccent(accent.Color);

        // Reste synchronisé si le thème change ailleurs (Windows, mode capture).
        theme.ThemeChanged += (_, _) =>
        {
            if (_themeChoice == theme.Choice) return;
            _themeChoice = theme.Choice;
            OnPropertyChanged(nameof(ThemeChoice));
        };
    }

    public IReadOnlyList<AccentOption> Accents { get; }

    [ObservableProperty] private ThemeChoice _themeChoice;
    [ObservableProperty] private int _maxConcurrency;
    [ObservableProperty] private bool _erasePasswords;
    [ObservableProperty] private string _reportsFolder;
    [ObservableProperty] private string _language = "Français";

    public IReadOnlyList<string> Languages { get; } = ["Français"];

    public string Version => "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    public bool AnimationsDisabledBySystem => !SystemParameters.ClientAreaAnimation;

    partial void OnThemeChoiceChanged(ThemeChoice value)
    {
        _theme.Apply(value);
        _settings.Theme = value;
        _settings.Save();
    }

    partial void OnMaxConcurrencyChanged(int value)
    {
        _runner.MaxConcurrency = value;
        _settings.MaxConcurrency = value;
        _settings.Save();
    }

    partial void OnErasePasswordsChanged(bool value)
    {
        _settings.ErasePasswords = value;
        _settings.Save();
    }

    partial void OnReportsFolderChanged(string value)
    {
        _settings.ReportsFolder = value;
        _settings.Save();
    }

    [RelayCommand]
    private void SelectAccent(AccentOption? option)
    {
        if (option is null) return;
        foreach (var a in Accents) a.IsSelected = a == option;
        var custom = option == Accents[0] ? (Color?)null : option.Color;
        _theme.SetAccent(custom);
        _settings.Accent = custom?.ToString();
        _settings.Save();
    }

    [RelayCommand]
    private void BrowseReports()
    {
        var dialog = new OpenFolderDialog { Title = "Dossier des rapports", InitialDirectory = ReportsFolder };
        if (dialog.ShowDialog() == true) ReportsFolder = dialog.FolderName;
    }
}
