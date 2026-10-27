using MailMigrator.Core.Models;

namespace MailMigrator.Core.Discovery;

/// <summary>Origine des paramètres IMAP trouvés.</summary>
public enum DiscoverySource
{
    MozillaAutoconfig,
    Ispdb,
    Autodiscover,
    MxRecord,
    CommonPrefix,
}

/// <summary>Adresse email décomposée, point d'entrée de la détection.</summary>
public sealed record DiscoveryRequest(string Email)
{
    public string Domain => Email[(Email.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();
    public string LocalPart => Email[..Math.Max(0, Email.LastIndexOf('@'))];
}

/// <summary>Serveur IMAP détecté.</summary>
public sealed record DiscoveredServer(ImapEndpoint Endpoint, DiscoverySource Source, MailProvider? Provider);

/// <summary>Trace d'une tentative, pour « Voir le détail technique ».</summary>
public sealed record DiscoveryAttempt(string Step, bool Success, string Detail);

public sealed record DiscoveryOutcome(DiscoveredServer? Server, IReadOnlyList<DiscoveryAttempt> Attempts)
{
    public bool Found => Server is not null;
}

/// <summary>Une méthode de détection (autoconfig, ISPDB, autodiscover, MX…).</summary>
public interface IDiscoverySource
{
    string Name { get; }

    /// <summary>Renvoie le serveur trouvé, ou null ; ne lève pas d'exception pour un simple échec.</summary>
    Task<DiscoveredServer?> DiscoverAsync(DiscoveryRequest request, ICollection<DiscoveryAttempt> trace, CancellationToken cancellationToken);
}

/// <summary>Résolution DNS (abstraite pour les tests).</summary>
public interface IDnsResolver
{
    /// <summary>Enregistrements MX triés par préférence croissante.</summary>
    Task<IReadOnlyList<string>> GetMxHostsAsync(string domain, CancellationToken cancellationToken);
}

/// <summary>Vérifie qu'un serveur IMAP répond sur un port (sans s'authentifier).</summary>
public interface IImapProbe
{
    Task<bool> ProbeAsync(string host, int port, SecurityMode security, CancellationToken cancellationToken);
}
