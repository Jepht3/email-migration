using System.Net;
using System.Text;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Discovery;

/// <summary>Base des sources HTTP : délai court, aucune exception pour un simple échec réseau.</summary>
public abstract class HttpDiscoverySource(HttpClient http) : IDiscoverySource
{
    protected static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    public abstract string Name { get; }

    public abstract Task<DiscoveredServer?> DiscoverAsync(DiscoveryRequest request, ICollection<DiscoveryAttempt> trace, CancellationToken cancellationToken);

    protected async Task<string?> GetStringAsync(HttpRequestMessage message, ICollection<DiscoveryAttempt> trace, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                trace.Add(new(Name, false, $"{message.RequestUri} → HTTP {(int)response.StatusCode}"));
                return null;
            }
            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            trace.Add(new(Name, false, $"{message.RequestUri} → {(ex is TaskCanceledException ? "délai dépassé" : ex.Message)}"));
            return null;
        }
    }

    protected DiscoveredServer? Found(ImapEndpoint? endpoint, DiscoverySource source, string url, ICollection<DiscoveryAttempt> trace)
    {
        if (endpoint is null)
        {
            trace.Add(new(Name, false, $"{url} → aucune configuration IMAP dans la réponse"));
            return null;
        }
        trace.Add(new(Name, true, $"{url} → {endpoint.Describe()}"));
        return new DiscoveredServer(endpoint, source, ProviderCatalog.FromHost(endpoint.Host));
    }
}

/// <summary>1. Autoconfig Mozilla publié par le domaine (autoconfig.domaine puis .well-known).</summary>
public sealed class MozillaAutoconfigSource(HttpClient http) : HttpDiscoverySource(http)
{
    public override string Name => "Autoconfig du domaine";

    public override async Task<DiscoveredServer?> DiscoverAsync(DiscoveryRequest request, ICollection<DiscoveryAttempt> trace, CancellationToken cancellationToken)
    {
        string[] urls =
        [
            $"https://autoconfig.{request.Domain}/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(request.Email)}",
            $"https://{request.Domain}/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(request.Email)}",
            $"http://autoconfig.{request.Domain}/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(request.Email)}",
        ];
        foreach (var url in urls)
        {
            var xml = await GetStringAsync(new HttpRequestMessage(HttpMethod.Get, url), trace, cancellationToken).ConfigureAwait(false);
            if (xml is null) continue;
            if (Found(AutoconfigParser.Parse(xml, request), DiscoverySource.MozillaAutoconfig, url, trace) is { } server) return server;
        }
        return null;
    }
}

/// <summary>1 bis. Base ISPDB de Thunderbird (grands fournisseurs et FAI).</summary>
public sealed class IspdbSource(HttpClient http) : HttpDiscoverySource(http)
{
    public const string BaseUrl = "https://autoconfig.thunderbird.net/v1.1/";

    public override string Name => "Base ISPDB Thunderbird";

    public override async Task<DiscoveredServer?> DiscoverAsync(DiscoveryRequest request, ICollection<DiscoveryAttempt> trace, CancellationToken cancellationToken)
    {
        var url = BaseUrl + request.Domain;
        var xml = await GetStringAsync(new HttpRequestMessage(HttpMethod.Get, url), trace, cancellationToken).ConfigureAwait(false);
        if (xml is null) return null;
        var server = Found(AutoconfigParser.Parse(xml, request), DiscoverySource.Ispdb, url, trace);
        return server is null ? null : server with { Provider = ProviderCatalog.FromEmail(request.Email) ?? server.Provider };
    }
}

/// <summary>2. Autodiscover (POX) : autodiscover.domaine puis domaine.</summary>
public sealed class AutodiscoverSource(HttpClient http) : HttpDiscoverySource(http)
{
    public override string Name => "Autodiscover";

    public override async Task<DiscoveredServer?> DiscoverAsync(DiscoveryRequest request, ICollection<DiscoveryAttempt> trace, CancellationToken cancellationToken)
    {
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006">
              <Request>
                <EMailAddress>{WebUtility.HtmlEncode(request.Email)}</EMailAddress>
                <AcceptableResponseSchema>http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a</AcceptableResponseSchema>
              </Request>
            </Autodiscover>
            """;
        string[] urls =
        [
            $"https://autodiscover.{request.Domain}/autodiscover/autodiscover.xml",
            $"https://{request.Domain}/autodiscover/autodiscover.xml",
        ];
        foreach (var url in urls)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
            var xml = await GetStringAsync(message, trace, cancellationToken).ConfigureAwait(false);
            if (xml is null) continue;
            if (Found(AutodiscoverParser.Parse(xml, request), DiscoverySource.Autodiscover, url, trace) is { } server) return server;
        }
        return null;
    }
}

/// <summary>
/// 3. Enregistrements MX : hébergeurs reconnus (Google, Microsoft 365, OVH…), puis préfixes
/// courants (imap., mail., hôte MX) vérifiés par une vraie connexion IMAP.
/// </summary>
public sealed class MxSource(IDnsResolver dns, IImapProbe probe) : IDiscoverySource
{
    /// <summary>Suffixe d'hôte MX → serveur IMAP de l'hébergeur.</summary>
    private static readonly (string MxSuffix, string ImapHost)[] KnownMx =
    [
        ("google.com", "imap.gmail.com"),
        ("googlemail.com", "imap.gmail.com"),
        ("mail.protection.outlook.com", "outlook.office365.com"),
        ("mail.ovh.net", "ssl0.ovh.net"),
        ("ovh.net", "ssl0.ovh.net"),
        ("ionos.fr", "imap.ionos.fr"),
        ("ionos.com", "imap.ionos.com"),
        ("zoho.eu", "imap.zoho.eu"),
        ("zoho.com", "imap.zoho.com"),
        ("yandex.net", "imap.yandex.com"),
        ("icloud.com", "imap.mail.me.com"),
    ];

    public string Name => "Enregistrements MX";

    public async Task<DiscoveredServer?> DiscoverAsync(DiscoveryRequest request, ICollection<DiscoveryAttempt> trace, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> mx;
        try
        {
            mx = await dns.GetMxHostsAsync(request.Domain, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            trace.Add(new(Name, false, $"MX {request.Domain} → {ex.Message}"));
            mx = [];
        }
        trace.Add(new(Name, mx.Count > 0, mx.Count > 0 ? $"MX {request.Domain} → {string.Join(", ", mx)}" : $"MX {request.Domain} → aucun enregistrement"));

        foreach (var host in mx)
        {
            var known = KnownMx.FirstOrDefault(k => host.TrimEnd('.').EndsWith(k.MxSuffix, StringComparison.OrdinalIgnoreCase));
            if (known.ImapHost is null) continue;
            var endpoint = new ImapEndpoint(known.ImapHost, 993, SecurityMode.SslOnConnect, request.Email);
            trace.Add(new(Name, true, $"Hébergeur reconnu d'après le MX {host} → {endpoint.Describe()}"));
            return new DiscoveredServer(endpoint, DiscoverySource.MxRecord, ProviderCatalog.FromHost(known.ImapHost));
        }

        // Préfixes courants, puis l'hôte MX lui-même (fréquent sur les serveurs cPanel / Plesk).
        var hosts = new List<string> { $"imap.{request.Domain}", $"mail.{request.Domain}" };
        hosts.AddRange(mx.Select(h => h.TrimEnd('.')).Where(h => !hosts.Contains(h, StringComparer.OrdinalIgnoreCase)));
        foreach (var host in hosts)
        {
            foreach (var (port, security) in new[] { (993, SecurityMode.SslOnConnect), (143, SecurityMode.StartTls) })
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool ok;
                try
                {
                    ok = await probe.ProbeAsync(host, port, security, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    trace.Add(new(Name, false, $"{host}:{port} → {ex.Message}"));
                    continue;
                }
                trace.Add(new(Name, ok, $"{host}:{port} → {(ok ? "serveur IMAP trouvé" : "pas de réponse IMAP")}"));
                if (!ok) continue;
                var source = mx.Any(m => m.TrimEnd('.').Equals(host, StringComparison.OrdinalIgnoreCase)) ? DiscoverySource.MxRecord : DiscoverySource.CommonPrefix;
                return new DiscoveredServer(new ImapEndpoint(host, port, security, request.Email), source, ProviderCatalog.FromHost(host));
            }
        }
        return null;
    }
}
