using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Imap;

/// <summary>Drapeaux conservés lors de la copie.</summary>
[Flags]
public enum MailFlags
{
    None = 0,
    Seen = 1,
    Answered = 2,
    Flagged = 4,
    Draft = 8,
}

/// <summary>Dossier tel qu'annoncé par le serveur.</summary>
/// <param name="Role">Rôle annoncé par SPECIAL-USE (ou XLIST) ; None si le serveur ne le dit pas.</param>
/// <param name="IsVirtual">Dossier virtuel (Gmail « Tous les messages », « Suivis ») : jamais copié.</param>
public sealed record RemoteFolder(string Path, char Separator, SpecialFolder Role, bool CanSelect = true, bool IsVirtual = false);

/// <summary>Résumé d'un message (sans son contenu).</summary>
public sealed record RemoteMessage(
    uint Uid,
    string? MessageId,
    DateTimeOffset InternalDate,
    DateTimeOffset? Date,
    string? FromName,
    string? FromAddress,
    string? Subject,
    long Size,
    MailFlags Flags)
{
    public string Sender => string.IsNullOrWhiteSpace(FromName) ? FromAddress ?? "" : FromName!;
}

public sealed record FolderState(uint UidValidity, int Count);

/// <summary>
/// Connexion IMAP vue par le moteur. Implémentée par MailKit ; remplacée par
/// une boîte en mémoire dans les tests.
/// </summary>
public interface IMailboxConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Préfixe de l'espace de noms personnel (« INBOX. » sur certains Courier/Dovecot, souvent vide).</summary>
    string NamespacePrefix { get; }

    char DefaultSeparator { get; }

    /// <summary>(Re)connexion et authentification.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken);

    Task<FolderState> GetFolderStateAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteMessage>> ListMessagesAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Télécharge le message brut. Au-delà d'un seuil, le contenu est écrit par morceaux dans
    /// un fichier temporaire : un message de 50 Mo ne passe jamais entièrement en mémoire.
    /// </summary>
    Task<Stream> DownloadAsync(string path, uint uid, long size, CancellationToken cancellationToken);

    /// <param name="role">Rôle du dossier source (envoyés, corbeille…) : conservé par les sauvegardes locales.</param>
    Task CreateFolderAsync(string path, SpecialFolder role, CancellationToken cancellationToken);

    Task AppendAsync(string path, Stream message, MailFlags flags, DateTimeOffset internalDate, CancellationToken cancellationToken);

    /// <summary>Vrai si un message portant ce Message-ID existe déjà dans le dossier.</summary>
    Task<bool> ContainsMessageIdAsync(string path, string messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Télécharge plusieurs petits messages en une seule commande (un aller-retour réseau au lieu d'un
    /// par message). Les messages introuvables sont absents du résultat. Par défaut : un par un.
    /// </summary>
    async Task<IReadOnlyDictionary<uint, Stream>> DownloadBatchAsync(string path, IReadOnlyList<(uint Uid, long Size)> messages, CancellationToken cancellationToken)
    {
        var result = new Dictionary<uint, Stream>();
        foreach (var (uid, size) in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[uid] = await DownloadAsync(path, uid, size, cancellationToken);
        }
        return result;
    }

    /// <summary>
    /// Dépose plusieurs messages en une seule commande (MULTIAPPEND quand le serveur le permet,
    /// tout ou rien). Par défaut : un par un.
    /// </summary>
    async Task AppendBatchAsync(string path, IReadOnlyList<AppendItem> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AppendAsync(path, item.Content, item.Flags, item.InternalDate, cancellationToken);
        }
    }
}

/// <summary>Un message à déposer, avec ses drapeaux et sa date de réception.</summary>
public sealed record AppendItem(Stream Content, MailFlags Flags, DateTimeOffset InternalDate);

/// <summary>Destination capable de vérifier la place disponible avant une copie (sauvegarde locale).</summary>
public interface ICapacityAware
{
    Task EnsureCapacityAsync(long bytes, CancellationToken cancellationToken);
}

public interface IMailboxConnectionFactory
{
    IMailboxConnection Create(ImapEndpoint endpoint, IAuthProvider auth);
}
