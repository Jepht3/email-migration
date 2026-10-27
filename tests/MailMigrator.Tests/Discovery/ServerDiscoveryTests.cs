using MailMigrator.Core.Discovery;
using MailMigrator.Core.Models;

namespace MailMigrator.Tests.Discovery;

public class AutoconfigParserTests
{
    [Fact]
    public void Prefers_ssl_imap_server_and_expands_username()
    {
        var endpoint = AutoconfigParser.Parse(Samples.AutoconfigXml, new DiscoveryRequest("jean.dupont@exemple.fr"));

        Assert.Equal(new ImapEndpoint("ssl.exemple.fr", 993, SecurityMode.SslOnConnect, "jean.dupont"), endpoint);
    }

    [Fact]
    public void Returns_null_without_imap_server_or_on_invalid_xml()
    {
        var request = new DiscoveryRequest("a@b.fr");
        Assert.Null(AutoconfigParser.Parse("<clientConfig><emailProvider/></clientConfig>", request));
        Assert.Null(AutoconfigParser.Parse("<html>pas du xml", request));
    }
}

public class AutodiscoverParserTests
{
    [Fact]
    public void Reads_imap_protocol_block()
    {
        var endpoint = AutodiscoverParser.Parse(Samples.AutodiscoverXml, new DiscoveryRequest("contact@societe.fr"));

        Assert.Equal(new ImapEndpoint("mail.societe.fr", 993, SecurityMode.SslOnConnect, "contact@societe.fr"), endpoint);
    }
}

public class ServerDiscoveryServiceTests
{
    private static ServerDiscoveryService Create(FakeHttpHandler http, FakeDns? dns = null, FakeProbe? probe = null)
    {
        var client = new HttpClient(http);
        return new ServerDiscoveryService(
        [
            new MozillaAutoconfigSource(client),
            new IspdbSource(client),
            new AutodiscoverSource(client),
            new MxSource(dns ?? new FakeDns(), probe ?? new FakeProbe()),
        ]);
    }

    [Fact]
    public async Task Domain_autoconfig_wins_first()
    {
        var http = new FakeHttpHandler()
            .Respond("https://autoconfig.exemple.fr/", Samples.AutoconfigXml)
            .Respond(IspdbSource.BaseUrl, Samples.GmailIspdbXml);

        var outcome = await Create(http).DiscoverAsync("jean@exemple.fr");

        Assert.Equal(DiscoverySource.MozillaAutoconfig, outcome.Server!.Source);
        Assert.Equal("ssl.exemple.fr", outcome.Server.Endpoint.Host);
        Assert.DoesNotContain(http.Requests, r => r.Contains("thunderbird"));
    }

    [Fact]
    public async Task Falls_back_to_ispdb_and_recognizes_provider()
    {
        var http = new FakeHttpHandler().Respond(IspdbSource.BaseUrl + "gmail.com", Samples.GmailIspdbXml);

        var outcome = await Create(http).DiscoverAsync("sophie.bernard@gmail.com");

        Assert.Equal(DiscoverySource.Ispdb, outcome.Server!.Source);
        Assert.Equal(new ImapEndpoint("imap.gmail.com", 993, SecurityMode.SslOnConnect, "sophie.bernard@gmail.com"), outcome.Server.Endpoint);
        Assert.Equal("Gmail", outcome.Server.Provider?.Name);
    }

    [Fact]
    public async Task Uses_autodiscover_when_autoconfig_and_ispdb_fail()
    {
        var http = new FakeHttpHandler().Respond("https://autodiscover.societe.fr/autodiscover/autodiscover.xml", Samples.AutodiscoverXml);

        var outcome = await Create(http).DiscoverAsync("contact@societe.fr");

        Assert.Equal(DiscoverySource.Autodiscover, outcome.Server!.Source);
        Assert.Contains(http.Requests, r => r.StartsWith("POST https://autodiscover.societe.fr"));
    }

    [Fact]
    public async Task Maps_known_mx_host_to_provider_imap_server()
    {
        var outcome = await Create(new FakeHttpHandler(), new FakeDns("cabinet-fr.mail.protection.outlook.com")).DiscoverAsync("compta@cabinet.fr");

        Assert.Equal(DiscoverySource.MxRecord, outcome.Server!.Source);
        Assert.Equal("outlook.office365.com", outcome.Server.Endpoint.Host);
        Assert.Equal("Outlook", outcome.Server.Provider?.Name);
    }

    [Fact]
    public async Task Probes_common_prefixes_then_mx_host()
    {
        var probe = new FakeProbe("mail.garage.fr:993");

        var outcome = await Create(new FakeHttpHandler(), new FakeDns("mx1.hebergeur.net"), probe).DiscoverAsync("compta@garage.fr");

        Assert.Equal(new ImapEndpoint("mail.garage.fr", 993, SecurityMode.SslOnConnect, "compta@garage.fr"), outcome.Server!.Endpoint);
        Assert.Equal(DiscoverySource.CommonPrefix, outcome.Server.Source);
        Assert.Equal(["imap.garage.fr:993", "imap.garage.fr:143", "mail.garage.fr:993"], probe.Probed);
    }

    [Fact]
    public async Task Mx_host_itself_is_tried_last()
    {
        var probe = new FakeProbe("srv12.hebergeur.net:143");

        var outcome = await Create(new FakeHttpHandler(), new FakeDns("srv12.hebergeur.net"), probe).DiscoverAsync("info@hotel.fr");

        Assert.Equal(new ImapEndpoint("srv12.hebergeur.net", 143, SecurityMode.StartTls, "info@hotel.fr"), outcome.Server!.Endpoint);
        Assert.Equal(DiscoverySource.MxRecord, outcome.Server.Source);
    }

    [Fact]
    public async Task Reports_every_attempt_when_nothing_is_found()
    {
        var outcome = await Create(new FakeHttpHandler()).DiscoverAsync("x@introuvable.fr");

        Assert.False(outcome.Found);
        Assert.Contains(outcome.Attempts, a => a.Step == "Base ISPDB Thunderbird" && a.Detail.Contains("404"));
        Assert.Contains(outcome.Attempts, a => a.Detail.Contains("imap.introuvable.fr:993"));
    }

    [Fact]
    public async Task Rejects_invalid_address_without_network()
    {
        var http = new FakeHttpHandler();

        var outcome = await Create(http).DiscoverAsync("pas-une-adresse");

        Assert.False(outcome.Found);
        Assert.Empty(http.Requests);
    }
}
