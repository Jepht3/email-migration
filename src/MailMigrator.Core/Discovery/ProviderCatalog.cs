using MailMigrator.Core.Models;

namespace MailMigrator.Core.Discovery;

/// <summary>Fournisseur reconnu, affiché avec sa pastille sous l'adresse.</summary>
public sealed record MailProvider(string Key, string Name, string Monogram, string BrandColor, ImapEndpoint? KnownEndpoint);

/// <summary>
/// Fournisseurs connus. Sert à afficher une pastille reconnaissable et à court-circuiter
/// la détection pour les grands hébergeurs. La détection complète (autoconfig, ISPDB,
/// autodiscover, MX) est implémentée à l'étape 3a.
/// </summary>
public static class ProviderCatalog
{
    private static readonly (string[] Domains, MailProvider Provider)[] Known =
    [
        (["gmail.com", "googlemail.com"], new("gmail", "Gmail", "G", "#EA4335", Ep("imap.gmail.com"))),
        (["outlook.com", "outlook.fr", "hotmail.com", "hotmail.fr", "live.com", "live.fr", "msn.com"],
            new("outlook", "Outlook", "O", "#0078D4", Ep("outlook.office365.com"))),
        (["yahoo.com", "yahoo.fr", "ymail.com"], new("yahoo", "Yahoo", "Y", "#6001D2", Ep("imap.mail.yahoo.com"))),
        (["icloud.com", "me.com", "mac.com"], new("icloud", "iCloud", "i", "#3693F3", Ep("imap.mail.me.com"))),
        (["orange.fr", "wanadoo.fr"], new("orange", "Orange", "O", "#FF7900", Ep("imap.orange.fr"))),
        (["free.fr"], new("free", "Free", "F", "#CD1E25", Ep("imap.free.fr"))),
        (["sfr.fr", "neuf.fr"], new("sfr", "SFR", "S", "#E2001A", Ep("imap.sfr.fr"))),
        (["laposte.net"], new("laposte", "La Poste", "L", "#003DA5", Ep("imap.laposte.net"))),
        (["gmx.fr", "gmx.com", "gmx.net"], new("gmx", "GMX", "G", "#1C449B", Ep("imap.gmx.com"))),
    ];

    private static ImapEndpoint Ep(string host) => new(host, 993, SecurityMode.SslOnConnect, "");

    public static MailProvider? FromEmail(string email)
    {
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return null;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        foreach (var (domains, provider) in Known)
            if (domains.Contains(domain)) return provider;
        return null;
    }

    /// <summary>Reconnaît un hébergeur à partir du nom du serveur IMAP détecté.</summary>
    public static MailProvider? FromHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host.Contains("ovh")) return new("ovh", "OVHcloud", "O", "#123F6D", null);
        if (host.Contains("o2switch")) return new("o2switch", "o2switch", "o2", "#E4032E", null);
        if (host.Contains("ionos") || host.Contains("1and1")) return new("ionos", "IONOS", "I", "#003D8F", null);
        if (host.Contains("office365") || host.Contains("outlook")) return Known[1].Provider;
        if (host.Contains("gmail") || host.Contains("google")) return Known[0].Provider;
        if (host.StartsWith("mail.") || host.StartsWith("imap.")) return new("cpanel", "cPanel", "cP", "#FF6C2C", null);
        return null;
    }
}
