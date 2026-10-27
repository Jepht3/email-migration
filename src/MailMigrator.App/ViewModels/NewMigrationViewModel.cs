using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.App.Views.Pages;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Folders;
using MailMigrator.Core.Import;
using MailMigrator.Core.Models;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace MailMigrator.App.ViewModels;

public sealed partial class StepViewModel(int index, string title, string subtitle) : ObservableObject
{
    public int Index { get; } = index;
    public int Number => Index + 1;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public bool IsLast => Index == 2;

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isDone;
}

public sealed partial class FolderMappingViewModel(FolderMapping mapping) : ObservableObject
{
    public string Source { get; } = mapping.SourcePath;
    public SpecialFolder Role { get; } = mapping.Role;
    public int MessageCount { get; } = mapping.MessageCount;
    public MappingOrigin Origin { get; } = mapping.Origin;

    public string OriginLabel => Origin switch
    {
        MappingOrigin.SpecialUse => "SPECIAL-USE",
        MappingOrigin.KnownName => "Nom reconnu",
        MappingOrigin.SamePath => "Identique",
        MappingOrigin.Created => "Sera créé",
        _ => "Manuel",
    };

    [ObservableProperty] private string _destination = mapping.DestinationPath;
    [ObservableProperty] private bool _included = mapping.Included;
}

/// <summary>Assistant en 3 étapes : Comptes → Options → Lancement.</summary>
public sealed partial class NewMigrationViewModel : ObservableObject
{
    public const int MaxPairs = 5;
    private readonly IMigrationRunner _runner;
    private readonly AppNavigation _navigation;
    private readonly DialogService _dialogs;
    private readonly AccountServices _services;
    private int _nextColor;

    public NewMigrationViewModel(IMigrationRunner runner, AppNavigation navigation, DialogService dialogs, AccountServices services)
    {
        _runner = runner;
        _navigation = navigation;
        _dialogs = dialogs;
        _services = services;
        Steps =
        [
            new(0, "Comptes", "Boîtes source et destination"),
            new(1, "Options", "Dossiers, dates, sécurité"),
            new(2, "Lancement", "Vérifier et démarrer"),
        ];
        Pairs.CollectionChanged += (_, _) => OnPairsChanged();
        AddPair();
        UpdateSteps();
    }

    public IReadOnlyList<StepViewModel> Steps { get; }
    public ObservableCollection<AccountPairViewModel> Pairs { get; } = [];
    public ObservableCollection<FolderMappingViewModel> FolderMappings { get; } = [];
    public IReadOnlyList<string> DestinationFolders { get; } =
        ["INBOX", "Envoyés", "Brouillons", "Corbeille", "Spam", "Archives", "Archives/2023", "Clients/Dupont", "Factures"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep), nameof(IsLastStep), nameof(NextLabel))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private int _step;

    public bool IsFirstStep => Step == 0;
    public bool IsLastStep => Step == 2;
    public string NextLabel => Step == 1 ? "Vérifier et lancer" : "Continuer";

    // Options
    [ObservableProperty] private bool _includeTrash;
    [ObservableProperty] private bool _includeJunk;
    [ObservableProperty] private bool _autoMapFolders = true;
    [ObservableProperty] private bool _useDateRange;
    [ObservableProperty] private DateTime? _dateFrom = DateTime.Today.AddYears(-3);
    [ObservableProperty] private DateTime? _dateTo = DateTime.Today;
    [ObservableProperty] private bool _verifyAfterCopy = true;
    [ObservableProperty] private bool _erasePasswords = true;
    [ObservableProperty] private string _excludedFolders = "";

    public bool CanAddPair => Pairs.Count < MaxPairs;
    public int PairCount => Pairs.Count;
    public string PairCountLabel => $"{Pairs.Count} / {MaxPairs}";
    public int TestedOkCount => Pairs.Count(p => p.Source.IsTestOk && p.Destination.IsTestOk);

    private void OnPairsChanged()
    {
        for (var i = 0; i < Pairs.Count; i++) Pairs[i].Number = i + 1;
        OnPropertyChanged(nameof(CanAddPair));
        OnPropertyChanged(nameof(PairCount));
        OnPropertyChanged(nameof(PairCountLabel));
        AddPairCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddPair))]
    private void AddPair()
    {
        var used = Pairs.Select(p => p.ColorIndex).ToHashSet();
        var color = Enumerable.Range(0, JobPalette.Count).Select(i => (_nextColor + i) % JobPalette.Count).First(i => !used.Contains(i));
        _nextColor = (color + 1) % JobPalette.Count;
        Pairs.Add(new AccountPairViewModel(color, _services, p => Pairs.Remove(p)));
    }

    [RelayCommand]
    private void ImportCsv()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importer des boîtes depuis un fichier CSV",
            Filter = "Fichiers CSV (*.csv)|*.csv|Tous les fichiers (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        using var reader = new StreamReader(dialog.FileName);
        var result = CsvJobImporter.Parse(reader);
        var empty = Pairs.Where(p => p.Source.Email.Length == 0 && p.Destination.Email.Length == 0).ToList();
        foreach (var p in empty) Pairs.Remove(p);

        var added = 0;
        foreach (var pair in result.Pairs)
        {
            if (!CanAddPair) break;
            AddPair();
            var vm = Pairs[^1];
            vm.Source.Email = pair.SourceEmail;
            vm.Source.Password = pair.SourcePassword;
            vm.Destination.Email = pair.DestinationEmail;
            vm.Destination.Password = pair.DestinationPassword;
            added++;
        }
        var skipped = result.Pairs.Count - added;
        var message = $"{added} paire(s) importée(s).";
        if (skipped > 0) message += $" {skipped} ignorée(s) : 5 boîtes maximum par lot.";
        if (result.Errors.Count > 0) message += $" {result.Errors.Count} ligne(s) invalide(s) : {result.Errors[0]}";
        _dialogs.Notify("Import CSV", message, result.Errors.Count > 0 ? ControlAppearance.Caution : ControlAppearance.Success,
            result.Errors.Count > 0 ? SymbolRegular.Warning24 : SymbolRegular.CheckmarkCircle24);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (Step > 0) Step--;
        UpdateSteps();
    }

    private bool CanGoBack() => Step > 0;

    [RelayCommand]
    private void Next()
    {
        if (Step == 0 && FolderMappings.Count == 0) LoadFolderPreview();
        if (Step < 2) Step++;
        UpdateSteps();
    }

    [RelayCommand]
    private void GoToStep(StepViewModel? step)
    {
        if (step is null || step.Index > Step) return;
        Step = step.Index;
        UpdateSteps();
    }

    public void ShowStep(int step)
    {
        Step = Math.Clamp(step, 0, 2);
        UpdateSteps();
    }

    private void UpdateSteps()
    {
        foreach (var s in Steps)
        {
            s.IsCurrent = s.Index == Step;
            s.IsDone = s.Index < Step;
        }
        OnPropertyChanged(nameof(TestedOkCount));
    }

    /// <summary>Démo : aperçu de correspondance tel que le produira FolderMapper (étape 3b).</summary>
    private void LoadFolderPreview()
    {
        FolderMappings.Clear();
        FolderMapping[] preview =
        [
            new("INBOX", "INBOX", SpecialFolder.Inbox, MappingOrigin.SpecialUse, 1840),
            new("Sent", "Envoyés", SpecialFolder.Sent, MappingOrigin.SpecialUse, 620),
            new("Drafts", "Brouillons", SpecialFolder.Drafts, MappingOrigin.KnownName, 14),
            new("Trash", "Corbeille", SpecialFolder.Trash, MappingOrigin.SpecialUse, 140, Included: false),
            new("Junk", "Spam", SpecialFolder.Junk, MappingOrigin.KnownName, 32, Included: false),
            new("Archives/2023", "Archives/2023", SpecialFolder.Archive, MappingOrigin.SamePath, 940),
            new("Clients/Dupont", "Clients/Dupont", SpecialFolder.None, MappingOrigin.Created, 85),
            new("Factures", "Factures", SpecialFolder.None, MappingOrigin.Created, 210),
        ];
        foreach (var m in preview) FolderMappings.Add(new FolderMappingViewModel(m));
    }

    partial void OnIncludeTrashChanged(bool value) => SetIncluded(SpecialFolder.Trash, value);
    partial void OnIncludeJunkChanged(bool value) => SetIncluded(SpecialFolder.Junk, value);

    private void SetIncluded(SpecialFolder role, bool value)
    {
        foreach (var m in FolderMappings.Where(m => m.Role == role)) m.Included = value;
    }

    public string DateSummary => UseDateRange ? $"du {DateFrom:dd/MM/yyyy} au {DateTo:dd/MM/yyyy}" : "Tous les messages";

    partial void OnUseDateRangeChanged(bool value) => OnPropertyChanged(nameof(DateSummary));
    partial void OnDateFromChanged(DateTime? value) => OnPropertyChanged(nameof(DateSummary));
    partial void OnDateToChanged(DateTime? value) => OnPropertyChanged(nameof(DateSummary));

    [RelayCommand]
    private void Start()
    {
        var options = new MigrationOptions
        {
            IncludeTrash = IncludeTrash,
            IncludeJunk = IncludeJunk,
            AutoMapFolders = AutoMapFolders,
            VerifyAfterCopy = VerifyAfterCopy,
            ErasePasswordsWhenDone = ErasePasswords,
            From = UseDateRange && DateFrom is { } f ? DateOnly.FromDateTime(f) : null,
            To = UseDateRange && DateTo is { } t ? DateOnly.FromDateTime(t) : null,
            ExcludedFolders = ExcludedFolders.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        foreach (var pair in Pairs.Where(p => p.Source.Email.Contains('@') && p.Destination.Email.Contains('@')))
        {
            _runner.Enqueue(new MigrationJobDefinition
            {
                ColorIndex = pair.ColorIndex,
                SourceEmail = pair.Source.Email,
                DestinationEmail = pair.Destination.Email,
                Options = options,
            });
        }
        Pairs.Clear();
        FolderMappings.Clear();
        AddPair();
        Step = 0;
        UpdateSteps();
        _navigation.Navigate(typeof(RunningPage));
    }

    /// <summary>Remplit l'assistant avec des comptes d'exemple dans différents états (démo et captures).</summary>
    public void SeedDemo()
    {
        Pairs.Clear();
        (string Src, string Dst, ConnectionError? SrcResult, ConnectionError? DstResult)[] demo =
        [
            ("contact@boulangerie-martin.fr", "contact@boulangerie-martin.com", ConnectionError.None, ConnectionError.None),
            ("sophie.bernard@gmail.com", "sophie@atelier-bernard.fr", ConnectionError.AppPasswordRequired, ConnectionError.None),
            ("direction@sarl-moreau.fr", "direction@moreau-groupe.fr", null, null),
        ];
        foreach (var (src, dst, srcResult, dstResult) in demo)
        {
            AddPair();
            var pair = Pairs[^1];
            pair.Source.SeedDemo(src, srcResult);
            pair.Destination.SeedDemo(dst, dstResult);
        }
        LoadFolderPreview();
        UpdateSteps();
    }
}
