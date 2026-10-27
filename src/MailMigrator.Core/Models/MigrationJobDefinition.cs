namespace MailMigrator.Core.Models;

/// <summary>Paramètres de connexion à un serveur IMAP (jamais le mot de passe).</summary>
public sealed record ImapEndpoint(string Host, int Port, SecurityMode Security, string UserName)
{
    public string Describe() => $"{Host} · {Port} · {Security switch
    {
        SecurityMode.SslOnConnect => "SSL/TLS",
        SecurityMode.StartTls => "STARTTLS",
        _ => "Non chiffré",
    }}";
}

/// <summary>Options d'une tâche de migration.</summary>
public sealed record MigrationOptions
{
    public IReadOnlyList<string> ExcludedFolders { get; init; } = [];
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public bool IncludeTrash { get; init; }
    public bool IncludeJunk { get; init; }
    public bool AutoMapFolders { get; init; } = true;
    public bool VerifyAfterCopy { get; init; } = true;
    public bool ErasePasswordsWhenDone { get; init; } = true;

    /// <summary>Correspondances forcées par l'utilisateur : chemin source → chemin destination.</summary>
    public IReadOnlyDictionary<string, string> FolderOverrides { get; init; } = new Dictionary<string, string>();
}

/// <summary>Définition d'une tâche : une paire de boîtes source → destination.</summary>
public sealed record MigrationJobDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Couleur d'identité (0 à 4) reprise sur la carte, les graphiques et le rapport.</summary>
    public int ColorIndex { get; init; }

    public required string SourceEmail { get; init; }
    public ImapEndpoint? Source { get; init; }
    public required string DestinationEmail { get; init; }
    public ImapEndpoint? Destination { get; init; }
    public MigrationOptions Options { get; init; } = new();
}
