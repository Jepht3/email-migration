using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Models;

namespace MailMigrator.Mobile.ViewModels;

/// <summary>Une boîte (source ou destination) : adresse, mot de passe, serveur détecté, test.</summary>
public sealed partial class AccountSideViewModel(string role, ServerDiscoveryService discovery, ConnectionTester tester) : ObservableObject
{
    private CancellationTokenSource? _detection;

    public string Role { get; } = role;

    public IReadOnlyList<string> SecurityOptions { get; } = ["SSL/TLS (recommandé)", "STARTTLS", "Aucun — non chiffré"];

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private string _serverLabel = "";
    [ObservableProperty] private bool _showServer;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "993";
    [ObservableProperty] private int _securityIndex;

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testMessage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestDetail))]
    private string? _testDetail;

    public bool HasTestDetail => !string.IsNullOrEmpty(TestDetail);
    [ObservableProperty] private bool _testOk;
    [ObservableProperty] private bool _hasTestResult;

    public bool IsUnencrypted => SecurityIndex == 2;

    partial void OnSecurityIndexChanged(int value) => OnPropertyChanged(nameof(IsUnencrypted));

    public ImapEndpoint? Endpoint => string.IsNullOrWhiteSpace(Host) || !int.TryParse(Port, out var port) || port is <= 0 or > 65535
        ? null
        : new ImapEndpoint(Host.Trim(), port, (SecurityMode)SecurityIndex, Email.Trim());

    partial void OnEmailChanged(string value)
    {
        HasTestResult = false;
        _ = DetectAsync(value.Trim());
    }

    partial void OnPasswordChanged(string value) => HasTestResult = false;

    private async Task DetectAsync(string email)
    {
        _detection?.Cancel();
        var cts = _detection = new CancellationTokenSource();
        if (!email.Contains('@') || email.EndsWith('@') || email.EndsWith('.'))
        {
            ShowServer = false;
            return;
        }
        try
        {
            await Task.Delay(700, cts.Token);
            IsDetecting = true;
            var outcome = await discovery.DiscoverAsync(email, cts.Token);
            if (cts.IsCancellationRequested) return;
            var endpoint = outcome.Server?.Endpoint ?? new ImapEndpoint($"mail.{email.Split('@')[1]}", 993, SecurityMode.SslOnConnect, email);
            Host = endpoint.Host;
            Port = endpoint.Port.ToString();
            SecurityIndex = (int)endpoint.Security;
            ServerLabel = outcome.Found
                ? $"{outcome.Server!.Provider?.Name ?? "Serveur"} · {endpoint.Describe()}"
                : "Serveur non détecté : vérifiez les réglages ci-dessous.";
            ShowAdvanced = !outcome.Found;
            ShowServer = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsDetecting = false;
        }
    }

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Password.Length == 0) { Show(ConnectionError.MissingPassword, null); return; }
        if (Endpoint is not { } endpoint) { ShowAdvanced = true; Show(ConnectionError.HostUnreachable, "Serveur ou port invalide."); return; }
        IsTesting = true;
        HasTestResult = false;
        try
        {
            var result = await tester.TestAsync(endpoint, new PasswordAuthProvider(Password));
            Show(result.Error, result.TechnicalDetail, result.Success ? $"{result.FolderCount} dossier(s)" : null);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void Show(ConnectionError error, string? detail, string? summary = null)
    {
        var info = ConnectionErrorInfo.For(error);
        TestOk = error == ConnectionError.None;
        TestMessage = TestOk ? $"Connexion OK{(summary is null ? "" : " · " + summary)}" : $"{info.Title} — {info.Explanation}";
        TestDetail = TestOk ? null : detail;
        HasTestResult = true;
    }

    public void Clear()
    {
        Email = "";
        Password = "";
        ShowServer = false;
        ShowAdvanced = false;
        HasTestResult = false;
    }
}
