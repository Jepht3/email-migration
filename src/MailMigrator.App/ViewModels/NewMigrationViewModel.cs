using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.App.Views.Pages;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Folders;
using MailMigrator.Core.Imap;
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

    public string ProposedDestination { get; } = mapping.DestinationPath;

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
    private readonly IMailboxConnectionFactory _connections;
    private readonly AppSettings _settings;
    private int _nextColor;
    private string? _previewFor;

    public NewMigrationViewModel(IMigrationRunner runner, AppNavigation navigation, DialogService dialogs, AccountServices services,
        IMailboxConnectionFactory connections, AppSettings settings)
    {
        _settings = settings;
        _erasePasswords = settings.ErasePasswords;
        _runner = runner;
        _navigation = navigation;
        _dialogs = dialogs;
        _services = services;
        _connections = connections;
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
    public ObservableCollection<string> DestinationFolders { get; } = [];

    [ObservableProperty] private bool _isLoadingFolders;
    [ObservableProperty] private string? _folderPreviewError;
    [ObservableProperty] private string _folderPreviewCaption = "Aperçu pour la première boîte. Modifiez une destination si besoin ; les dossiers absents seront créés.";
    [ObservableProperty] private bool _isStarting;

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
    private async Task NextAsync()
    {
        if (Step == 0)
        {
            if (!ValidatePairs()) return;
            Step = 1;
            UpdateSteps();
            await LoadFolderPreviewAsync();
            return;
        }
        if (Step < 2) Step++;
        UpdateSteps();
    }

    private IEnumerable<AccountPairViewModel> FilledPairs =>
        Pairs.Where(p => p.Source.Email.Length > 0 || p.Destination.Email.Length > 0 || p.Source.IsLocalArchive || p.Destination.IsLocalArchive);

    /// <summary>Chaque boîte saisie doit avoir un serveur et un mot de passe ; sinon message précis.</summary>
    private bool ValidatePairs()
    {
        var pairs = FilledPairs.ToList();
        if (pairs.Count == 0)
        {
            _dialogs.Notify("Aucune boîte à migrer", "Saisissez au moins une adresse source et une adresse destination.", ControlAppearance.Caution, SymbolRegular.Warning24);
            return false;
        }
        foreach (var pair in pairs)
        {
            if (pair.Source.IsLocalArchive && pair.Destination.IsLocalArchive)
            {
                _dialogs.Notify($"Migration {pair.Number}", "Une sauvegarde se restaure vers une boîte email : choisissez « Boîte email » pour la destination.",
                    ControlAppearance.Caution, SymbolRegular.Warning24);
                return false;
            }
            foreach (var side in new[] { pair.Source, pair.Destination })
            {
                string? problem = side.IsLocalArchive
                    ? side.LocalPath.Trim().Length == 0 ? "dossier de sauvegarde non choisi"
                      : side.ArchiveStatusIsError ? "aucune sauvegarde trouvée dans ce dossier"
                      : null
                    : !side.Email.Contains('@') ? "adresse email invalide"
                    : side.Secret is null ? "connexion manquante : saisissez le mot de passe ou connectez le compte OAuth"
                    : side.IsDetecting ? "détection du serveur en cours, patientez une seconde"
                    : side.Endpoint is null ? "serveur IMAP non renseigné (bouton « Modifier »)"
                    : null;
                if (problem is null) continue;
                _dialogs.Notify($"Migration {pair.Number} — {side.Role}", $"{(side.Email.Length > 0 ? side.Email : "Boîte")} : {problem}.",
                    ControlAppearance.Caution, SymbolRegular.Warning24);
                return false;
            }
        }
        return true;
    }

    /// <summary>Aperçu réel : liste les dossiers des deux boîtes de la première paire et applique FolderMapper.</summary>
    private async Task LoadFolderPreviewAsync()
    {
        var pair = FilledPairs.FirstOrDefault();
        if (pair?.Source.Endpoint is not { } source || pair.Destination.Endpoint is not { } destination) return;
        var key = $"{source}|{destination}|{pair.Source.Secret?.GetHashCode()}|{pair.Destination.Secret?.GetHashCode()}";
        if (key == _previewFor && FolderMappings.Count > 0)
        {
            ApplyToggles();
            return;
        }

        IsLoadingFolders = true;
        FolderPreviewError = null;
        FolderMappings.Clear();
        DestinationFolders.Clear();
        FolderPreviewCaption = $"Dossiers de {pair.Source.DisplayEmail} → {pair.Destination.DisplayEmail}. Modifiez une destination si besoin ; les dossiers absents seront créés.";
        try
        {
            await using var src = _connections.Create(source, pair.Source.CreateAuth());
            await using var dst = _connections.Create(destination, pair.Destination.CreateAuth());
            await Task.WhenAll(src.ConnectAsync(default), dst.ConnectAsync(default));
            var srcFolders = await src.ListFoldersAsync(default);
            var dstFolders = await dst.ListFoldersAsync(default);
            var counts = new Dictionary<string, int>();
            foreach (var f in srcFolders.Where(f => f.CanSelect))
                counts[f.Path] = (await src.GetFolderStateAsync(f.Path, default)).Count;

            var mappings = FolderMapper.Map(
                new MailboxLayout(srcFolders, src.NamespacePrefix, src.DefaultSeparator),
                new MailboxLayout(dstFolders, dst.NamespacePrefix, dst.DefaultSeparator),
                BuildOptions(), counts);
            foreach (var path in dstFolders.Where(f => f.CanSelect).Select(f => f.Path).Union(mappings.Select(m => m.DestinationPath)).OrderBy(p => p))
                DestinationFolders.Add(path);
            foreach (var m in mappings.Where(m => !m.SourcePath.Equals("[Gmail]", StringComparison.Ordinal)))
                FolderMappings.Add(new FolderMappingViewModel(m));
            _previewFor = key;
        }
        catch (Exception ex)
        {
            var info = ConnectionErrorInfo.For(ImapErrorTranslator.Translate(ex, source.Host));
            FolderPreviewError = $"Impossible de lire les dossiers : {info.Title}. {info.Explanation}";
        }
        finally
        {
            IsLoadingFolders = false;
        }
    }

    private void ApplyToggles()
    {
        SetIncluded(SpecialFolder.Trash, IncludeTrash);
        SetIncluded(SpecialFolder.Junk, IncludeJunk);
    }

    [RelayCommand]
    private void GoToStep(StepViewModel? step)
    {
        if (step is null || step.Index > Step) return;
        Step = step.Index;
        UpdateSteps();
    }

    /// <summary>Écran Sauvegardes locales → « Mettre à jour » : boîte d'origine → même sauvegarde.</summary>
    public void PrepareBackupUpdate(string email, ImapEndpoint? server, string archivePath)
    {
        ResetWizard();
        Pairs[0].Source.SetMailbox(email, server);
        Pairs[0].Destination.SetArchive(archivePath);
    }

    /// <summary>Écran Sauvegardes locales → « Nouvelle sauvegarde » : boîte à saisir → ce PC.</summary>
    public void PrepareNewBackup()
    {
        ResetWizard();
        Pairs[0].Destination.Kind = MailboxKind.LocalArchive;
    }

    /// <summary>Écran Sauvegardes locales → « Restaurer » : sauvegarde → boîte à saisir.</summary>
    public void PrepareRestore(string archivePath)
    {
        ResetWizard();
        Pairs[0].Source.SetArchive(archivePath);
    }

    private void ResetWizard()
    {
        Pairs.Clear();
        FolderMappings.Clear();
        DestinationFolders.Clear();
        _previewFor = null;
        ErasePasswords = _settings.ErasePasswords;
        AddPair();
        ShowStep(0);
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

    /// <summary>Démo et captures : aperçu de correspondance fictif.</summary>
    private void LoadDemoFolderPreview()
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

    private MigrationOptions BuildOptions() => new()
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

    [RelayCommand]
    private void Start()
    {
        if (!ValidatePairs())
        {
            ShowStep(0);
            return;
        }

        var options = BuildOptions();
        var first = true;
        foreach (var pair in FilledPairs.ToList())
        {
            var jobOptions = options;
            if (first && _previewFor is not null)
            {
                // Les choix faits dans l'aperçu s'appliquent à la boîte qu'il montre (la première).
                jobOptions = options with
                {
                    IncludeTrash = options.IncludeTrash || FolderMappings.Any(m => m.Role == SpecialFolder.Trash && m.Included),
                    IncludeJunk = options.IncludeJunk || FolderMappings.Any(m => m.Role == SpecialFolder.Junk && m.Included),
                    SkippedSourceFolders = FolderMappings.Where(m => !m.Included && m.Role is not (SpecialFolder.Trash or SpecialFolder.Junk)).Select(m => m.Source).ToList(),
                    FolderOverrides = FolderMappings.Where(m => m.Destination != m.ProposedDestination && m.Destination.Trim().Length > 0)
                        .ToDictionary(m => m.Source, m => m.Destination.Trim()),
                };
            }
            first = false;
            _runner.Enqueue(new MigrationJobDefinition
            {
                ColorIndex = pair.ColorIndex,
                SourceEmail = pair.Source.DisplayEmail,
                Source = pair.Source.Endpoint,
                DestinationEmail = pair.Destination.DisplayEmail,
                // Nouvelle sauvegarde : elle porte le nom de la boîte sauvegardée.
                Destination = pair.Destination.IsLocalArchive && pair.Destination.ArchiveSummary is null
                    ? pair.Destination.Endpoint! with { UserName = pair.Source.DisplayEmail }
                    : pair.Destination.Endpoint,
                Options = jobOptions,
            }, pair.Source.Secret!, pair.Destination.Secret!);
        }

        // Les mots de passe ne restent pas dans l'assistant : seule la copie chiffrée est conservée.
        Pairs.Clear();
        FolderMappings.Clear();
        DestinationFolders.Clear();
        _previewFor = null;
        ErasePasswords = _settings.ErasePasswords;
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
        LoadDemoFolderPreview();
        foreach (var path in FolderMappings.Select(m => m.Destination).Distinct()) DestinationFolders.Add(path);
        UpdateSteps();
    }
}
