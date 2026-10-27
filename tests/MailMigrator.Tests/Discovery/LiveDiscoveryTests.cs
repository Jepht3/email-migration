using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Models;
using Xunit.Abstractions;

namespace MailMigrator.Tests.Discovery;

/// <summary>
/// Détection sur de vrais domaines (DNS + HTTP). Désactivé par défaut ;
/// à lancer avec MAILMIGRATOR_NETWORK_TESTS=1.
/// </summary>
[Trait("Category", "Network")]
public class LiveDiscoveryTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MAILMIGRATOR_NETWORK_TESTS") == "1";

    [Theory]
    [InlineData("exemple@gmail.com", "imap.gmail.com")]
    [InlineData("exemple@outlook.fr", "outlook.office365.com")]
    [InlineData("exemple@orange.fr", "imap.orange.fr")]
    [InlineData("exemple@free.fr", "imap.free.fr")]
    public async Task Finds_well_known_providers(string email, string expectedHost)
    {
        if (!Enabled) return;
        using var http = new HttpClient();

        var outcome = await ServerDiscoveryService.CreateDefault(http).DiscoverAsync(email);

        foreach (var attempt in outcome.Attempts) output.WriteLine($"{(attempt.Success ? "✔" : "✘")} {attempt.Step} : {attempt.Detail}");
        Assert.Equal(expectedHost, outcome.Server?.Endpoint.Host);
    }

    [Theory]
    [InlineData("imap.gmail.com", ConnectionError.AppPasswordRequired)]
    [InlineData("imap.introuvable-lewebmax-test.fr", ConnectionError.HostUnreachable)]
    public async Task Connection_errors_are_translated(string host, ConnectionError expected)
    {
        if (!Enabled) return;
        var endpoint = new ImapEndpoint(host, 993, SecurityMode.SslOnConnect, "compte.inexistant.lewebmax@gmail.com");

        var result = await new ConnectionTester().TestAsync(endpoint, new PasswordAuthProvider("mauvais-mot-de-passe"));

        output.WriteLine(result.TechnicalDetail);
        Assert.Equal(expected, result.Error);
    }
}
