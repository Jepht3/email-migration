using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Connectivity.OAuth;
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

public enum TestState
{
    NotTested,
    Testing,
    Success,
    Failure,
}

/// <summary>Services du Core utilisés par l'assistant pour chaque boîte.</summary>
public sealed record AccountServices(
    ServerDiscoveryService Discovery,
    ConnectionTester Tester,
    DialogService Dialogs,
    OAuthRegistry OAuth,
    OAuthSignIn SignIn,
    OAuthClient OAuthClient);

/// <summary>Une paire source → destination dans l'assistant.</summary>
public sealed partial class AccountPairViewModel : ObservableObject
{
    public AccountPairViewModel(int colorIndex, AccountServices services, Action<AccountPairViewModel> remove)
    {
        ColorIndex = colorIndex;
        Source = new AccountSideViewModel("Source", services);
        Destination = new AccountSideViewModel("Destination", services);
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
    public bool IsComplete => Email.Contains('@') && Secret is not null;

    // ---------- OAuth2 « Se connecter avec… » ----------

    private OAuthTokens? _oauthTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOAuth), nameof(UsesPassword), nameof(OAuthAccountLabel))]
    private OAuthCredential? _oauthCredential;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool _isSigningIn;

    /// <summary>Fournisseur OAuth configuré pour le serveur détecté (Google, Microsoft), sinon null.</summary>
    public OAuthProviderSettings? OAuthProvider => string.IsNullOrWhiteSpace(Host) ? null : services.OAuth.ForHost(Host);

    public bool CanUseOAuth => OAuthProvider is not null && !IsOAuth;
    public bool IsOAuth => OauthCredential is not null;
    public bool UsesPassword => !IsOAuth;
    public string SignInLabel => $"Se connecter avec {OAuthProvider?.DisplayName}";
    public string OAuthAccountLabel => OauthCredential is { } c ? $"Connecté avec {services.OAuth.Get(c.Provider)?.DisplayName ?? c.Provider} : {c.Email}" : "";

    /// <summary>Fournisseur reconnu mais connexion OAuth non configurée dans l'application.</summary>
    public bool OAuthNotConfigured => OAuthProvider is null && !string.IsNullOrWhiteSpace(Host) && OAuthRegistry.KeyForHost(Host) is not null;

    /// <summary>Ce qui sera chiffré et stocké pour la migration : jeton OAuth2 ou mot de passe.</summary>
    public string? Secret => OauthCredential?.Serialize() ?? (Password.Length > 0 ? Password : null);

    /// <summary>Authentification à utiliser pour cette boîte (test, aperçu des dossiers).</summary>
    public IAuthProvider? CreateAuth() => OauthCredential is { } credential && OAuthProvider is { } provider
        ? new OAuth2AuthProvider(provider, credential, services.OAuthClient, _oauthTokens)
        : Password.Length > 0 ? new PasswordAuthProvider(Password) : null;

    partial void OnHostChanged(string value)
    {
        Test = TestState.NotTested;
        OnPropertyChanged(nameof(OAuthProvider));
        OnPropertyChanged(nameof(CanUseOAuth));
        OnPropertyChanged(nameof(SignInLabel));
        OnPropertyChanged(nameof(OAuthNotConfigured));
    }

    partial void OnOauthCredentialChanged(OAuthCredential? value) => OnPropertyChanged(nameof(CanUseOAuth));

    private bool CanSignIn() => !IsSigningIn;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        if (OAuthProvider is not { } provider) return;
        IsSigningIn = true;
        try
        {
            var result = await services.SignIn.SignInAsync(provider, Email.Contains('@') ? Email.Trim() : null, OpenUrl, CancellationToken.None);
            if (Email.Contains('@') && !result.Credential.Email.Equals(Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                services.Dialogs.Notify("Mauvais compte", $"Vous vous êtes connecté avec {result.Credential.Email} au lieu de {Email.Trim()}. Recommencez avec le bon compte.",
                    Wpf.Ui.Controls.ControlAppearance.Caution, Wpf.Ui.Controls.SymbolRegular.Warning24);
                return;
            }
            _oauthTokens = result.Tokens;
            OauthCredential = result.Credential;
            await TestAsync();
        }
        catch (OAuthException ex)
        {
            Error = ConnectionErrorInfo.For(ConnectionError.OAuthFailed) with { Explanation = ex.Message };
            TechnicalDetail = ex.Message;
            Test = TestState.Failure;
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    [RelayCommand]
    private void UsePassword()
    {
        OauthCredential = null;
        _oauthTokens = null;
        Test = TestState.NotTested;
    }

    public IReadOnlyList<SecurityOption> SecurityOptions { get; } =
    [
        new(SecurityMode.SslOnConnect, "SSL/TLS (recommandé)"),
        new(SecurityMode.StartTls, "STARTTLS"),
        new(SecurityMode.None, "Aucun — non chiffré"),
    ];

    /// <summary>Paramètres effectifs, y compris les corrections manuelles.</summary>
    public ImapEndpoint? Endpoint => string.IsNullOrWhiteSpace(Host) || !int.TryParse(Port, out var port) || port is <= 0 or > 65535
        ? null
        : new ImapEndpoint(Host.Trim(), port, Security, string.IsNullOrWhiteSpace(UserName) ? Email.Trim() : UserName.Trim());

    partial void OnEmailChanged(string value)
    {
        Test = TestState.NotTested;
        // Autre adresse = autre compte : l'autorisation OAuth précédente ne s'applique plus.
        if (OauthCredential is not null && !OauthCredential.Email.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) UsePassword();
        if (!_suppressDetection) _ = DetectAsync(value);
    }

    partial void OnPasswordChanged(string value) => Test = TestState.NotTested;
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
        if (CreateAuth() is not { } auth)
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
        var result = await services.Tester.TestAsync(endpoint, auth);
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
            case ConnectionError.OAuthFailed:
                UsePassword();
                await SignInAsync();
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
