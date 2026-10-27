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

    public SettingsViewModel(ThemeService theme, IMigrationRunner runner)
    {
        _theme = theme;
        _runner = runner;
        _themeChoice = theme.Choice;
        _maxConcurrency = runner.MaxConcurrency;
        Accents =
        [
            new("Bleu LEWEBMAX", theme.DefaultAccent),
            new("Indigo", Color.FromRgb(0x4F, 0x46, 0xE5)),
            new("Violet", Color.FromRgb(0x7C, 0x3A, 0xED)),
            new("Turquoise", Color.FromRgb(0x0D, 0x94, 0x88)),
            new("Orange", Color.FromRgb(0xEA, 0x58, 0x0C)),
        ];
        Accents[0].IsSelected = true;

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
    [ObservableProperty] private bool _erasePasswords = true;
    [ObservableProperty] private string _reportsFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LEWEBMAX", "Rapports de migration");
    [ObservableProperty] private string _language = "Français";

    public IReadOnlyList<string> Languages { get; } = ["Français"];

    public string Version => "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    public bool AnimationsDisabledBySystem => !SystemParameters.ClientAreaAnimation;

    partial void OnThemeChoiceChanged(ThemeChoice value) => _theme.Apply(value);

    partial void OnMaxConcurrencyChanged(int value) => _runner.MaxConcurrency = value;

    [RelayCommand]
    private void SelectAccent(AccentOption? option)
    {
        if (option is null) return;
        foreach (var a in Accents) a.IsSelected = a == option;
        _theme.SetAccent(option == Accents[0] ? null : option.Color);
    }

    [RelayCommand]
    private void BrowseReports()
    {
        var dialog = new OpenFolderDialog { Title = "Dossier des rapports", InitialDirectory = ReportsFolder };
        if (dialog.ShowDialog() == true) ReportsFolder = dialog.FolderName;
    }
}
