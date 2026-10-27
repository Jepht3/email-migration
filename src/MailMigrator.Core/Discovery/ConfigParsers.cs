using System.Xml.Linq;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Discovery;

/// <summary>
/// Format « clientConfig » de Mozilla (autoconfig.domaine, .well-known et ISPDB de Thunderbird).
/// https://wiki.mozilla.org/Thunderbird:Autoconfiguration:ConfigFileFormat
/// </summary>
public static class AutoconfigParser
{
    public static ImapEndpoint? Parse(string xml, DiscoveryRequest request)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var candidates = doc.Descendants()
            .Where(e => e.Name.LocalName == "incomingServer" && (string?)e.Attribute("type") == "imap")
            .Select(e => new
            {
                Host = Child(e, "hostname"),
                Port = int.TryParse(Child(e, "port"), out var p) ? p : 0,
                Socket = Child(e, "socketType")?.ToUpperInvariant(),
                User = Child(e, "username") ?? "%EMAILADDRESS%",
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.Host) && c.Port > 0)
            .Select(c => new ImapEndpoint(
                Expand(c.Host!, request),
                c.Port,
                c.Socket switch { "SSL" => SecurityMode.SslOnConnect, "STARTTLS" => SecurityMode.StartTls, _ => SecurityMode.None },
                Expand(c.User, request)))
            .ToList();

        // Préférence : SSL/TLS, puis STARTTLS ; jamais de non chiffré si mieux existe.
        return candidates.OrderBy(c => c.Security switch { SecurityMode.SslOnConnect => 0, SecurityMode.StartTls => 1, _ => 2 }).FirstOrDefault();
    }

    private static string? Child(XElement e, string name) =>
        e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();

    internal static string Expand(string value, DiscoveryRequest request) => value
        .Replace("%EMAILADDRESS%", request.Email, StringComparison.OrdinalIgnoreCase)
        .Replace("%EMAILLOCALPART%", request.LocalPart, StringComparison.OrdinalIgnoreCase)
        .Replace("%EMAILDOMAIN%", request.Domain, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Réponse Autodiscover « POX » (Exchange / Outlook, aussi servie par cPanel).</summary>
public static class AutodiscoverParser
{
    public static ImapEndpoint? Parse(string xml, DiscoveryRequest request)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var protocol = doc.Descendants()
            .Where(e => e.Name.LocalName == "Protocol")
            .FirstOrDefault(e => string.Equals(Child(e, "Type"), "IMAP", StringComparison.OrdinalIgnoreCase));
        if (protocol is null) return null;

        var host = Child(protocol, "Server");
        if (string.IsNullOrWhiteSpace(host)) return null;
        var ssl = !string.Equals(Child(protocol, "SSL"), "off", StringComparison.OrdinalIgnoreCase);
        var encryption = Child(protocol, "Encryption")?.ToUpperInvariant();
        var port = int.TryParse(Child(protocol, "Port"), out var p) ? p : ssl ? 993 : 143;
        var security = encryption switch
        {
            "TLS" => SecurityMode.StartTls,
            "SSL" => SecurityMode.SslOnConnect,
            "NONE" => SecurityMode.None,
            _ when !ssl => SecurityMode.None,
            _ => port == 143 ? SecurityMode.StartTls : SecurityMode.SslOnConnect,
        };
        return new ImapEndpoint(host, port, security, Child(protocol, "LoginName") ?? request.Email);
    }

    private static string? Child(XElement e, string name) =>
        e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();
}
