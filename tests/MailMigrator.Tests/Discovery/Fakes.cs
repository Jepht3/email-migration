using System.Net;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Models;

namespace MailMigrator.Tests.Discovery;

/// <summary>Serveur HTTP simulé : URL (préfixe) → réponse ; tout le reste répond 404.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = [];

    public List<string> Requests { get; } = [];

    public FakeHttpHandler Respond(string urlPrefix, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[urlPrefix] = (status, body);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add($"{request.Method} {url}");
        var route = _routes.FirstOrDefault(r => url.StartsWith(r.Key, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(route.Key is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(route.Value.Status) { Content = new StringContent(route.Value.Body) });
    }
}

internal sealed class FakeDns(params string[] mx) : IDnsResolver
{
    public Task<IReadOnlyList<string>> GetMxHostsAsync(string domain, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(mx);
}

/// <summary>Sonde IMAP simulée : seuls les couples hôte:port listés répondent.</summary>
internal sealed class FakeProbe(params string[] reachable) : IImapProbe
{
    public List<string> Probed { get; } = [];

    public Task<bool> ProbeAsync(string host, int port, SecurityMode security, CancellationToken cancellationToken)
    {
        Probed.Add($"{host}:{port}");
        return Task.FromResult(reachable.Contains($"{host}:{port}"));
    }
}

internal static class Samples
{
    public const string AutoconfigXml = """
        <?xml version="1.0"?>
        <clientConfig version="1.1">
          <emailProvider id="exemple.fr">
            <incomingServer type="pop3">
              <hostname>pop.exemple.fr</hostname><port>995</port><socketType>SSL</socketType>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>imap.exemple.fr</hostname><port>143</port><socketType>STARTTLS</socketType>
              <username>%EMAILADDRESS%</username>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>ssl.exemple.fr</hostname><port>993</port><socketType>SSL</socketType>
              <username>%EMAILLOCALPART%</username>
            </incomingServer>
          </emailProvider>
        </clientConfig>
        """;

    public const string GmailIspdbXml = """
        <clientConfig version="1.1">
          <emailProvider id="googlemail.com">
            <incomingServer type="imap">
              <hostname>imap.gmail.com</hostname><port>993</port><socketType>SSL</socketType>
              <username>%EMAILADDRESS%</username><authentication>OAuth2</authentication>
            </incomingServer>
          </emailProvider>
        </clientConfig>
        """;

    public const string AutodiscoverXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006">
          <Response xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a">
            <Account>
              <AccountType>email</AccountType>
              <Protocol>
                <Type>POP3</Type><Server>mail.societe.fr</Server><Port>995</Port><SSL>on</SSL>
              </Protocol>
              <Protocol>
                <Type>IMAP</Type><Server>mail.societe.fr</Server><Port>993</Port>
                <LoginName>contact@societe.fr</LoginName><SSL>on</SSL>
              </Protocol>
            </Account>
          </Response>
        </Autodiscover>
        """;
}
