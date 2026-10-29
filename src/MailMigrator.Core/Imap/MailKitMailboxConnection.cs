using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Models;
using MimeKit;
using SpecialFolder = MailMigrator.Core.Models.SpecialFolder;

namespace MailMigrator.Core.Imap;

public sealed class MailKitConnectionFactory : IMailboxConnectionFactory
{
    public IMailboxConnection Create(ImapEndpoint endpoint, IAuthProvider auth) => new MailKitMailboxConnection(endpoint, auth);
}

/// <summary>Implémentation MailKit d'une connexion IMAP (une par côté et par tâche).</summary>
public sealed class MailKitMailboxConnection(ImapEndpoint endpoint, IAuthProvider auth) : IMailboxConnection
{
    /// <summary>Au-delà, le message est téléchargé par morceaux dans un fichier temporaire.</summary>
    public const long StreamingThreshold = 4 * 1024 * 1024;
    private const int ChunkSize = 1024 * 1024;

    private ImapClient? _client;
    private readonly Dictionary<string, IMailFolder> _folders = new(StringComparer.Ordinal);

    public bool IsConnected => _client is { IsConnected: true, IsAuthenticated: true };
    public string NamespacePrefix { get; private set; } = "";
    public char DefaultSeparator { get; private set; } = '/';

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        OAuthEndpoints.ValidateMailbox(endpoint);
        if (_client is not null)
        {
            try { await _client.DisconnectAsync(false, CancellationToken.None); } catch { /* connexion déjà perdue */ }
            _client.Dispose();
        }
        _folders.Clear();
        _client = new ImapClient { Timeout = 60_000 };
        TlsPolicy.Configure(_client);
        await _client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Security.ToSocketOptions(), cancellationToken);
        await auth.AuthenticateAsync(_client, endpoint.UserName, cancellationToken);

        if (_client.PersonalNamespaces.Count > 0)
        {
            var ns = _client.PersonalNamespaces[0];
            NamespacePrefix = ns.Path;
            DefaultSeparator = ns.DirectorySeparator;
        }
    }

    private ImapClient Client => _client is { IsConnected: true } c ? c : throw new ServiceNotConnectedException("La connexion IMAP a été perdue.");

    public async Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken)
    {
        var client = Client;
        var result = new List<RemoteFolder>();
        var inbox = client.Inbox;
        _folders[inbox.FullName] = inbox;
        result.Add(new RemoteFolder(inbox.FullName, inbox.DirectorySeparator, SpecialFolder.Inbox));

        foreach (var ns in client.PersonalNamespaces)
        {
            var folders = await client.GetFoldersAsync(ns, StatusItems.None, false, cancellationToken);
            foreach (var f in folders)
            {
                if (f.FullName.Equals(inbox.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                _folders[f.FullName] = f;
                var attrs = f.Attributes;
                var role = attrs.HasFlag(FolderAttributes.Sent) ? SpecialFolder.Sent
                    : attrs.HasFlag(FolderAttributes.Drafts) ? SpecialFolder.Drafts
                    : attrs.HasFlag(FolderAttributes.Trash) ? SpecialFolder.Trash
                    : attrs.HasFlag(FolderAttributes.Junk) ? SpecialFolder.Junk
                    : attrs.HasFlag(FolderAttributes.Archive) ? SpecialFolder.Archive
                    : SpecialFolder.None;
                var isVirtual = attrs.HasFlag(FolderAttributes.All) || attrs.HasFlag(FolderAttributes.Flagged);
                var canSelect = !attrs.HasFlag(FolderAttributes.NoSelect) && !attrs.HasFlag(FolderAttributes.NonExistent);
                result.Add(new RemoteFolder(f.FullName, f.DirectorySeparator, role, canSelect, isVirtual));
            }
        }
        return result;
    }

    private async Task<IMailFolder> OpenAsync(string path, FolderAccess access, CancellationToken cancellationToken)
    {
        if (!_folders.TryGetValue(path, out var folder))
        {
            folder = await Client.GetFolderAsync(path, cancellationToken);
            _folders[path] = folder;
        }
        if (!folder.IsOpen || folder.Access < access) await folder.OpenAsync(access, cancellationToken);
        return folder;
    }

    public async Task<FolderState> GetFolderStateAsync(string path, CancellationToken cancellationToken)
    {
        var folder = await OpenAsync(path, FolderAccess.ReadOnly, cancellationToken);
        return new FolderState(folder.UidValidity, folder.Count);
    }

    public async Task<IReadOnlyList<RemoteMessage>> ListMessagesAsync(string path, CancellationToken cancellationToken)
    {
        var folder = await OpenAsync(path, FolderAccess.ReadOnly, cancellationToken);
        if (folder.Count == 0) return [];
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Size
                    | MessageSummaryItems.InternalDate | MessageSummaryItems.Flags;
        var result = new List<RemoteMessage>(folder.Count);
        // Par lots : les très grosses boîtes ne bloquent pas le serveur sur une seule commande.
        for (var start = 0; start < folder.Count; start += 1000)
        {
            var end = Math.Min(folder.Count - 1, start + 999);
            foreach (var s in await folder.FetchAsync(start, end, items, cancellationToken))
            {
                var from = s.Envelope?.From.Mailboxes.FirstOrDefault();
                result.Add(new RemoteMessage(
                    s.UniqueId.Id,
                    s.Envelope?.MessageId,
                    s.InternalDate ?? s.Envelope?.Date ?? DateTimeOffset.MinValue,
                    s.Envelope?.Date,
                    from?.Name,
                    from?.Address,
                    s.Envelope?.Subject,
                    (long)(s.Size ?? 0),
                    ToFlags(s.Flags ?? MessageFlags.None)));
            }
        }
        return result;
    }

    public async Task<Stream> DownloadAsync(string path, uint uid, long size, CancellationToken cancellationToken)
    {
        var folder = await OpenAsync(path, FolderAccess.ReadOnly, cancellationToken);
        var id = new UniqueId(uid);
        if (size <= StreamingThreshold)
        {
            var memory = new MemoryStream((int)Math.Max(size, 1024));
            using (var stream = await folder.GetStreamAsync(id, cancellationToken)) await stream.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            return memory;
        }

        // Message volumineux : BODY.PEEK[]<offset.count> par morceaux de 1 Mo vers un fichier temporaire.
        var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            for (var offset = 0; ; offset += ChunkSize)
            {
                using var chunk = await folder.GetStreamAsync(id, offset, ChunkSize, cancellationToken);
                var before = file.Length;
                await chunk.CopyToAsync(file, cancellationToken);
                if (file.Length - before < ChunkSize) break;
            }
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    public async Task CreateFolderAsync(string path, SpecialFolder role, CancellationToken cancellationToken)
    {
        var client = Client;
        var separator = DefaultSeparator;
        var root = client.GetFolder(client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace(separator, ""));
        var relative = path.StartsWith(NamespacePrefix, StringComparison.Ordinal) ? path[NamespacePrefix.Length..] : path;

        // Crée chaque niveau manquant (« Clients » puis « Clients/Dupont »).
        IMailFolder parent = root!;
        foreach (var name in relative.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            IMailFolder? next = null;
            try
            {
                next = await parent.GetSubfolderAsync(name, cancellationToken);
            }
            catch (FolderNotFoundException)
            {
            }
            next ??= await parent.CreateAsync(name, true, cancellationToken);
            parent = next ?? throw new FolderNotFoundException(name);
        }
        _folders[parent.FullName] = parent;
    }

    public async Task AppendAsync(string path, Stream message, MailFlags flags, DateTimeOffset internalDate, CancellationToken cancellationToken)
    {
        if (!_folders.TryGetValue(path, out var folder))
        {
            folder = await Client.GetFolderAsync(path, cancellationToken);
            _folders[path] = folder;
        }
        // persistent : le contenu reste dans le flux (fichier temporaire) au lieu d'être recopié en mémoire.
        var mime = await MimeMessage.LoadAsync(message, persistent: message is FileStream, cancellationToken);
        await folder.AppendAsync(new AppendRequest(mime, ToMessageFlags(flags), internalDate), cancellationToken);
    }

    /// <summary>Un seul FETCH BODY.PEEK[] pour tout le lot.</summary>
    public async Task<IReadOnlyDictionary<uint, Stream>> DownloadBatchAsync(string path, IReadOnlyList<(uint Uid, long Size)> messages, CancellationToken cancellationToken)
    {
        var folder = (ImapFolder)await OpenAsync(path, FolderAccess.ReadOnly, cancellationToken);
        var result = new Dictionary<uint, Stream>();
        await folder.GetStreamsAsync(messages.Select(m => new UniqueId(m.Uid)).ToList(), async (_, _, uid, stream, ct) =>
        {
            var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct);
            copy.Position = 0;
            result[uid.Id] = copy;
        }, cancellationToken);
        return result;
    }

    /// <summary>MULTIAPPEND (RFC 3502) si disponible : tout le lot en une commande, atomique.</summary>
    public async Task AppendBatchAsync(string path, IReadOnlyList<AppendItem> items, CancellationToken cancellationToken)
    {
        if (!_folders.TryGetValue(path, out var folder))
        {
            folder = await Client.GetFolderAsync(path, cancellationToken);
            _folders[path] = folder;
        }
        var requests = new List<IAppendRequest>(items.Count);
        foreach (var item in items)
        {
            var mime = await MimeMessage.LoadAsync(item.Content, persistent: item.Content is FileStream, cancellationToken);
            requests.Add(new AppendRequest(mime, ToMessageFlags(item.Flags), item.InternalDate));
        }
        await folder.AppendAsync(requests, cancellationToken);
    }

    public async Task<bool> ContainsMessageIdAsync(string path, string messageId, CancellationToken cancellationToken)
    {
        var folder = await OpenAsync(path, FolderAccess.ReadOnly, cancellationToken);
        var found = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId.Trim('<', '>')), cancellationToken);
        return found.Count > 0;
    }

    private static MailFlags ToFlags(MessageFlags f) =>
        (f.HasFlag(MessageFlags.Seen) ? MailFlags.Seen : 0)
        | (f.HasFlag(MessageFlags.Answered) ? MailFlags.Answered : 0)
        | (f.HasFlag(MessageFlags.Flagged) ? MailFlags.Flagged : 0)
        | (f.HasFlag(MessageFlags.Draft) ? MailFlags.Draft : 0);

    private static MessageFlags ToMessageFlags(MailFlags f) =>
        (f.HasFlag(MailFlags.Seen) ? MessageFlags.Seen : 0)
        | (f.HasFlag(MailFlags.Answered) ? MessageFlags.Answered : 0)
        | (f.HasFlag(MailFlags.Flagged) ? MessageFlags.Flagged : 0)
        | (f.HasFlag(MailFlags.Draft) ? MessageFlags.Draft : 0);

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        try
        {
            if (_client.IsConnected) await _client.DisconnectAsync(true, CancellationToken.None);
        }
        catch
        {
            // Fermeture best effort.
        }
        _client.Dispose();
    }
}
