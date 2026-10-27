using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using MailMigrator.App.Services;
using MailMigrator.Core.Archive;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Models;

namespace MailMigrator.App.ViewModels;

public enum DetectionState
{
    Idle,
    Detecting,
    Detected,
    Failed,
}

/// <summary>Un côté d'une migration : une boîte email (IMAP) ou une sauvegarde sur ce PC.</summary>
public enum MailboxKind
{
    Mailbox,
    LocalArchive,
}

public enum TestState
{
    NotTested,
    Testing,
    Success,
    Failure,
}

/// <summary>Services du Core utilisés par l'assistant pour chaque boîte.</summary>
public sealed record AccountServices(ServerDiscoveryService Discovery, ConnectionTester Tester, DialogService Dialogs);

/// <summary>Une paire source → destination dans l'assistant.</summary>
public sealed partial class AccountPairViewModel : ObservableObject
{
    public AccountPairViewModel(int colorIndex, AccountServices services, Action<AccountPairViewModel> remove)
    {
        ColorIndex = colorIndex;
        Source = new AccountSideViewModel("Source", services);
        Destination = new AccountSideViewModel("Destination", services) { SuggestArchiveName = () => Source.Email.Trim() };
        Source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountSideViewModel.Email)) Destination.SuggestPath();
        };
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    [ObservableProperty] private int _number;

    public int ColorIndex { get; }
    public AccountSideViewModel Source { get; }
    public AccountSideViewModel Destination { get; }
    public IRelayCommand RemoveCommand { get; }

    public bool IsReady => Source.IsComplete && Destination.IsComplete;
}

/// <summary>Un côté d'une paire : adresse, mot de passe, serveur détecté, test de connexion.</summary>
public sealed partial class AccountSideViewModel(string role, AccountServices services) : ObservableObject
{
    private CancellationTokenSource? _discoveryCts;
    private bool _suppressDetection;
    private IReadOnlyList<DiscoveryAttempt> _attempts = [];

    public string Role { get; } = role;

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetecting), nameof(IsDetected), nameof(IsDetectionFailed))]
    private DetectionState _discovery;

    [ObservableProperty] private MailProvider? _provider;
    [ObservableProperty] private string _serverLabel = "";
    [ObservableProperty] private string _sourceLabel = "";
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "993";
    [ObservableProperty] private string _userName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnencrypted))]
    private SecurityMode _security = SecurityMode.SslOnConnect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTesting), nameof(IsTestOk), nameof(IsTestFailed))]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private TestState _test;

    [ObservableProperty] private ConnectionErrorInfo? _error;
    [ObservableProperty] private string? _technicalDetail;
    [ObservableProperty] private string? _testSummary;

    public bool IsDetecting => Discovery == DetectionState.Detecting;
    public bool IsDetected => Discovery == DetectionState.Detected;
    public bool IsDetectionFailed => Discovery == DetectionState.Failed;
    public bool IsTesting => Test == TestState.Testing;
    public bool IsTestOk => Test == TestState.Success;
    public bool IsTestFailed => Test == TestState.Failure;
    public bool IsUnencrypted => Security == SecurityMode.None;
    public bool IsComplete => IsLocalArchive ? LocalPath.Trim().Length > 0 : Email.Contains('@') && Password.Length > 0;

    // ---------- Boîte email ou sauvegarde sur ce PC ----------

    public static string DefaultBackupRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LEWEBMAX", "Sauvegardes");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMailbox), nameof(IsLocalArchive), nameof(ArchiveStatus), nameof(ArchiveStatusIsError))]
    private MailboxKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveStatus), nameof(ArchiveStatusIsError))]
    private string _localPath = "";

    [ObservableProperty] private bool _encryptArchive = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveStatus), nameof(ArchiveStatusIsError), nameof(CanChooseFormat))]
    private LocalArchiveSummary? _archiveSummary;

    public bool IsMailbox => Kind == MailboxKind.Mailbox;
    public bool IsLocalArchive => Kind == MailboxKind.LocalArchive;
    public bool IsDestination => Role == "Destination";
    public string ArchiveKindLabel => IsDestination ? "Sauvegarde sur ce PC" : "Sauvegarde de ce PC";

    /// <summary>Le format ne se choisit qu'à la création d'une nouvelle sauvegarde.</summary>
    public bool CanChooseFormat => IsDestination && ArchiveSummary is null;

    /// <summary>Nom proposé pour le dossier d'une nouvelle sauvegarde (l'adresse source).</summary>
    public Func<string>? SuggestArchiveName { get; set; }

    public string ArchiveStatus => LocalPath.Trim().Length == 0
        ? (IsDestination ? "Choisissez le dossier où enregistrer la sauvegarde." : "Choisissez le dossier d'une sauvegarde existante.")
        : ArchiveSummary is { } s
            ? $"Sauvegarde de {s.Label} : {Core.Formatting.Format.Number(s.Messages)} emails, {Core.Formatting.Format.Bytes(s.Bytes)}, "
              + $"{(s.Encrypted ? "chiffrée" : "format lisible")}, mise à jour le {s.UpdatedAt.LocalDateTime:dd/MM/yyyy à HH:mm}."
              + (IsDestination ? " Seuls les nouveaux emails seront ajoutés." : "")
            : IsDestination ? "Une nouvelle sauvegarde sera créée dans ce dossier." : "Aucune sauvegarde trouvée dans ce dossier.";

    public bool ArchiveStatusIsError => IsLocalArchive && !IsDestination && LocalPath.Trim().Length > 0 && ArchiveSummary is null;

    /// <summary>Adresse affichée sur la carte de migration et dans l'historique.</summary>
    public string DisplayEmail => IsLocalArchive
        ? (IsDestination ? "Sauvegarde sur ce PC" : $"Sauvegarde · {ArchiveSummary?.Label}")
        : Email.Trim();

    /// <summary>Ce qui sera chiffré et stocké pour la migration (rien pour une sauvegarde locale).</summary>
    public string? Secret => IsLocalArchive ? "" : Password.Length > 0 ? Password : null;

    public IAuthProvider CreateAuth() => new PasswordAuthProvider(Secret ?? "");

    private bool _autoPath;
    private bool _settingAutoPath;

    partial void OnKindChanged(MailboxKind value)
    {
        Test = TestState.NotTested;
        if (value == MailboxKind.LocalArchive && LocalPath.Length == 0 && IsDestination) SuggestPath();
        RefreshArchive();
    }

    /// <summary>Dossier proposé : Documents\LEWEBMAX\Sauvegardes\&lt;adresse source&gt;, tant que l'utilisateur ne l'a pas changé.</summary>
    public void SuggestPath()
    {
        if (!IsLocalArchive || !IsDestination || (LocalPath.Length > 0 && !_autoPath)) return;
        _settingAutoPath = true;
        LocalPath = Path.Combine(DefaultBackupRoot, SafeName(SuggestArchiveName?.Invoke() is { Length: > 0 } name ? name : "boite"));
        _settingAutoPath = false;
        _autoPath = true;
    }

    partial void OnLocalPathChanged(string value)
    {
        if (!_settingAutoPath) _autoPath = false;
        RefreshArchive();
    }

    private void RefreshArchive()
    {
        try
        {
            ArchiveSummary = IsLocalArchive && LocalPath.Trim().Length > 0 ? LocalArchiveMailbox.ReadSummary(LocalPath.Trim()) : null;
        }
        catch (Exception)
        {
            ArchiveSummary = null;
        }
        OnPropertyChanged(nameof(ArchiveStatus));
        OnPropertyChanged(nameof(ArchiveStatusIsError));
    }

    [RelayCommand]
    private void BrowseArchive()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = IsDestination ? "Dossier de la sauvegarde" : "Dossier d'une sauvegarde existante",
            InitialDirectory = Directory.Exists(LocalPath) ? LocalPath : Directory.Exists(DefaultBackupRoot) ? DefaultBackupRoot : null,
        };
        if (dialog.ShowDialog() == true) LocalPath = dialog.FolderName;
    }

    /// <summary>Prérenseigne une boîte email déjà connue (écran Sauvegardes locales), sans relancer la détection.</summary>
    public void SetMailbox(string email, ImapEndpoint? endpoint)
    {
        Kind = MailboxKind.Mailbox;
        _suppressDetection = endpoint is not null;
        Email = email;
        _suppressDetection = false;
        if (endpoint is not null) Apply(endpoint, ProviderCatalog.FromHost(endpoint.Host), "dernière sauvegarde");
    }

    public void SetArchive(string path)
    {
        Kind = MailboxKind.LocalArchive;
        LocalPath = path;
    }

    private static string SafeName(string name) => string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();

    public IReadOnlyList<SecurityOption> SecurityOptions { get; } =
    [
        new(SecurityMode.SslOnConnect, "SSL/TLS (recommandé)"),
        new(SecurityMode.StartTls, "STARTTLS"),
        new(SecurityMode.None, "Aucun — non chiffré"),
    ];

    /// <summary>Paramètres effectifs, y compris les corrections manuelles.</summary>
    public ImapEndpoint? Endpoint => IsLocalArchive
        ? (LocalPath.Trim().Length == 0 ? null : ImapEndpoint.LocalArchive(LocalPath.Trim(), ArchiveSummary?.Label ?? "", EncryptArchive))
        : string.IsNullOrWhiteSpace(Host) || !int.TryParse(Port, out var port) || port is <= 0 or > 65535
        ? null
        : new ImapEndpoint(Host.Trim(), port, Security, string.IsNullOrWhiteSpace(UserName) ? Email.Trim() : UserName.Trim());

    partial void OnEmailChanged(string value)
    {
        Test = TestState.NotTested;
        if (!_suppressDetection) _ = DetectAsync(value);
    }

    partial void OnPasswordChanged(string value) => Test = TestState.NotTested;
    partial void OnHostChanged(string value) => Test = TestState.NotTested;
    partial void OnPortChanged(string value) => Test = TestState.NotTested;
    partial void OnSecurityChanged(SecurityMode value) => Test = TestState.NotTested;

    /// <summary>Détection réelle : autoconfig → ISPDB → autodiscover → MX + préfixes.</summary>
    private async Task DetectAsync(string email)
    {
        _discoveryCts?.Cancel();
        var cts = _discoveryCts = new CancellationTokenSource();
        if (!email.Contains('@') || email.EndsWith('@') || email.EndsWith('.'))
        {
            Discovery = DetectionState.Idle;
            return;
        }
        try
        {
            await Task.Delay(600, cts.Token); // laisse l'utilisateur finir de taper
            Discovery = DetectionState.Detecting;
            var outcome = await services.Discovery.DiscoverAsync(email, cts.Token);
            if (cts.IsCancellationRequested) return;
            _attempts = outcome.Attempts;

            if (outcome.Server is { } server)
            {
                Apply(server.Endpoint, server.Provider, SourceText(server.Source));
                return;
            }
            var domain = email.Split('@').Last();
            Provider = null;
            Host = $"mail.{domain}";
            Port = "993";
            Security = SecurityMode.SslOnConnect;
            UserName = email;
            ShowAdvanced = true;
            Discovery = DetectionState.Failed;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Apply(ImapEndpoint endpoint, MailProvider? provider, string source)
    {
        Provider = provider;
        Host = endpoint.Host;
        Port = endpoint.Port.ToString();
        Security = endpoint.Security;
        UserName = endpoint.UserName;
        ServerLabel = endpoint.Describe();
        SourceLabel = source;
        Discovery = DetectionState.Detected;
    }

    private static string SourceText(DiscoverySource source) => source switch
    {
        DiscoverySource.MozillaAutoconfig => "autoconfig du domaine",
        DiscoverySource.Ispdb => "base Thunderbird",
        DiscoverySource.Autodiscover => "autodiscover",
        DiscoverySource.MxRecord => "enregistrements MX",
        _ => "préfixe courant",
    };

    private bool CanTest() => Test != TestState.Testing;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (Password.Length == 0)
        {
            ApplyTestResult(ConnectionError.MissingPassword);
            return;
        }
        if (Endpoint is not { } endpoint)
        {
            ShowAdvanced = true;
            ApplyTestResult(ConnectionError.HostUnreachable, "Serveur ou port invalide.");
            return;
        }

        Test = TestState.Testing;
        var result = await services.Tester.TestAsync(endpoint, new PasswordAuthProvider(Password));
        ServerLabel = endpoint.Describe();
        TestSummary = result.Success
            ? $"{result.FolderCount} dossier(s){(result.SupportsSpecialUse ? " · SPECIAL-USE" : "")}"
            : null;
        ApplyTestResult(result.Error, result.TechnicalDetail);
    }

    public void ApplyTestResult(ConnectionError error, string? technicalDetail = null)
    {
        Error = error == ConnectionError.None ? null : ConnectionErrorInfo.For(error);
        TechnicalDetail = technicalDetail;
        Test = error == ConnectionError.None ? TestState.Success : TestState.Failure;
    }

    /// <summary>Action proposée sous le message d'erreur.</summary>
    [RelayCommand]
    private async Task ErrorActionAsync()
    {
        switch (Error?.Code)
        {
            case ConnectionError.AppPasswordRequired:
                OpenUrl(Host.Contains("gmail", StringComparison.OrdinalIgnoreCase) ? "https://myaccount.google.com/apppasswords"
                    : Host.Contains("yahoo", StringComparison.OrdinalIgnoreCase) ? "https://login.yahoo.com/account/security"
                    : Host.Contains("me.com", StringComparison.OrdinalIgnoreCase) ? "https://account.apple.com"
                    : "https://account.microsoft.com/security");
                break;
            case ConnectionError.CertificateInvalid or ConnectionError.Unknown:
                await ShowDetailsAsync();
                break;
            case ConnectionError.MissingPassword:
                break;
            default:
                await TestAsync();
                break;
        }
    }

    [RelayCommand]
    private Task ShowDetailsAsync()
    {
        var lines = new List<string>();
        if (TechnicalDetail is not null) lines.Add("Test de connexion :\n" + TechnicalDetail);
        if (_attempts.Count > 0)
            lines.Add("Détection du serveur :\n" + string.Join("\n", _attempts.Select(a => $"{(a.Success ? "✔" : "✘")} {a.Step} — {a.Detail}")));
        return services.Dialogs.ShowDetailsAsync("Détail technique", lines.Count == 0 ? "Aucun détail disponible." : string.Join("\n\n", lines));
    }

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Démo et captures : remplit le côté sans lancer la détection réseau.</summary>
    public void SeedDemo(string email, ConnectionError? result)
    {
        _suppressDetection = true;
        Email = email;
        _suppressDetection = false;
        Password = "••••••••••";
        var provider = ProviderCatalog.FromEmail(email);
        var endpoint = provider?.KnownEndpoint is { } known ? known with { UserName = email }
            : new ImapEndpoint($"mail.{email.Split('@').Last()}", 993, SecurityMode.SslOnConnect, email);
        Apply(endpoint, provider ?? ProviderCatalog.FromHost(endpoint.Host), provider is null ? "préfixe courant" : "base Thunderbird");
        if (result is { } r) ApplyTestResult(r);
    }
}

public sealed record SecurityOption(SecurityMode Mode, string Label);
