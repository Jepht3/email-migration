using DnsClient;
using MailKit.Net.Imap;
using MailKit.Security;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Discovery;

/// <summary>
/// Détection automatique des paramètres IMAP, dans l'ordre :
/// autoconfig du domaine → ISPDB Thunderbird → autodiscover → MX + préfixes courants.
/// </summary>
public sealed class ServerDiscoveryService(IEnumerable<IDiscoverySource> sources)
{
    private readonly IReadOnlyList<IDiscoverySource> _sources = sources.ToList();

    /// <summary>Chaîne standard, avec les implémentations réseau réelles.</summary>
    public static ServerDiscoveryService CreateDefault(HttpClient http) => new(
    [
        new MozillaAutoconfigSource(http),
        new IspdbSource(http),
        new AutodiscoverSource(http),
        new MxSource(new DnsClientResolver(), new MailKitImapProbe()),
    ]);

    public async Task<DiscoveryOutcome> DiscoverAsync(string email, CancellationToken cancellationToken = default)
    {
        var trace = new List<DiscoveryAttempt>();
        if (!email.Contains('@') || email.EndsWith('@'))
        {
            trace.Add(new("Adresse", false, "Adresse email invalide"));
            return new(null, trace);
        }

        var request = new DiscoveryRequest(email.Trim());
        foreach (var source in _sources)
        {
            var server = await source.DiscoverAsync(request, trace, cancellationToken).ConfigureAwait(false);
            if (server is not null)
            {
                return new(server with { Provider = server.Provider ?? ProviderCatalog.FromEmail(email) }, trace);
            }
        }
        return new(null, trace);
    }
}

/// <summary>Résolution MX via DnsClient.NET.</summary>
public sealed class DnsClientResolver : IDnsResolver
{
    private readonly LookupClient _client = Create();

    /// <summary>
    /// Serveurs DNS du système ; à défaut (Android ne les expose pas toujours), DNS publics
    /// Cloudflare et Google.
    /// </summary>
    private static LookupClient Create()
    {
        try
        {
            var system = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(4), Retries = 1, UseCache = true });
            if (system.NameServers.Count > 0) return system;
        }
        catch (Exception)
        {
            // Pas d'accès à la configuration réseau du système.
        }
        return new LookupClient(new LookupClientOptions(NameServer.Cloudflare, NameServer.GooglePublicDns)
        {
            Timeout = TimeSpan.FromSeconds(4),
            Retries = 1,
            UseCache = true,
        });
    }

    public async Task<IReadOnlyList<string>> GetMxHostsAsync(string domain, CancellationToken cancellationToken)
    {
        var result = await _client.QueryAsync(domain, QueryType.MX, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Answers.MxRecords()
            .OrderBy(r => r.Preference)
            .Select(r => r.Exchange.Value.TrimEnd('.'))
            .ToList();
    }
}

/// <summary>Sonde IMAP : connexion + bannière, sans authentification, certificat non bloquant.</summary>
public sealed class MailKitImapProbe : IImapProbe
{
    public async Task<bool> ProbeAsync(string host, int port, SecurityMode security, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new ImapClient { Timeout = 5000 };
        // Le certificat est vérifié plus tard, au test de connexion, avec un message explicite.
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        try
        {
            await client.ConnectAsync(host, port, security.ToSocketOptions(), timeout.Token).ConfigureAwait(false);
            await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

public static class SecurityModeExtensions
{
    public static SecureSocketOptions ToSocketOptions(this SecurityMode mode) => mode switch
    {
        SecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SecurityMode.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };
}
