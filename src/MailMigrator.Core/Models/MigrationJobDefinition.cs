namespace MailMigrator.Core.Models;

/// <summary>
/// Paramètres de connexion à une boîte (jamais le mot de passe) : un serveur IMAP,
/// ou une sauvegarde locale sur ce PC quand <see cref="LocalArchivePath"/> est renseigné.
/// </summary>
public sealed record ImapEndpoint(string Host, int Port, SecurityMode Security, string UserName)
{
    /// <summary>Dossier de la sauvegarde locale ; null pour un serveur IMAP.</summary>
    public string? LocalArchivePath { get; init; }

    /// <summary>Format d'une NOUVELLE sauvegarde : chiffrée (défaut) ou fichiers .eml lisibles.</summary>
    public bool ArchiveEncrypted { get; init; } = true;

    public bool IsLocalArchive => LocalArchivePath is not null;

    /// <summary>Sauvegarde locale ; <paramref name="label"/> = adresse de la boîte sauvegardée.</summary>
    public static ImapEndpoint LocalArchive(string path, string label, bool encrypted = true) =>
        new("sauvegarde-locale", 0, SecurityMode.None, label) { LocalArchivePath = path, ArchiveEncrypted = encrypted };

    public string Describe() => IsLocalArchive
        ? $"Sauvegarde sur ce PC · {LocalArchivePath}"
        : $"{Host} · {Port} · {Security switch
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

    /// <summary>Dossiers source décochés dans l'aperçu (chemins exacts, côté serveur source).</summary>
    public IReadOnlyList<string> SkippedSourceFolders { get; init; } = [];

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
