using System.Text;
using MailKit.Net.Imap;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;

namespace MailMigrator.Tests.Engine;

/// <summary>Serveur IMAP en mémoire : dossiers, messages, UID, et pannes programmables.</summary>
internal sealed class FakeMailServer(string host, string namespacePrefix = "", char separator = '/')
{
    public sealed class Folder(string path, SpecialFolder role)
    {
        public string Path { get; } = path;
        public SpecialFolder Role { get; } = role;
        public uint UidValidity { get; set; } = 1;
        public uint NextUid { get; set; } = 1;
        public List<(RemoteMessage Meta, byte[] Raw)> Messages { get; } = [];
    }

    public string Host { get; } = host;
    public string NamespacePrefix { get; } = namespacePrefix;
    public char Separator { get; } = separator;
    public Dictionary<string, Folder> Folders { get; } = new() { ["INBOX"] = new Folder("INBOX", SpecialFolder.Inbox) };

    /// <summary>Panne réseau : après N APPEND réussis, l'APPEND suivant réussit puis la connexion tombe.</summary>
    public int? DropConnectionAfterAppends { get; set; }

    /// <summary>« Plantage » : après N APPEND, on annule la tâche (fermeture brutale de l'application).</summary>
    public (int After, CancellationTokenSource Cts)? CrashAfterAppends { get; set; }

    /// <summary>Sujets que le serveur refuse définitivement (NO).</summary>
    public HashSet<string> RejectedSubjects { get; } = [];

    public int AppendCount { get; private set; }
    public int ConnectCount { get; set; }

    public Folder AddFolder(string path, SpecialFolder role = SpecialFolder.None)
    {
        var folder = new Folder(path, role);
        Folders[path] = folder;
        return folder;
    }

    public RemoteMessage Add(string folder, string? messageId, string subject, DateTimeOffset internalDate, MailFlags flags = MailFlags.None,
        string from = "client@exemple.fr", int size = 0)
    {
        var f = Folders[folder];
        var raw = Encoding.UTF8.GetBytes($"Message-ID: {messageId}\r\nSubject: {subject}\r\n\r\n{new string('x', size)}");
        var meta = new RemoteMessage(f.NextUid++, messageId, internalDate, internalDate, "Client", from, subject, raw.Length, flags);
        f.Messages.Add((meta, raw));
        return meta;
    }

    public void OnAppend(Folder folder, byte[] raw, MailFlags flags, DateTimeOffset internalDate, FakeConnection connection)
    {
        var text = Encoding.UTF8.GetString(raw);
        var subject = text.Split("\r\n")[1]["Subject: ".Length..];
        if (RejectedSubjects.Contains(subject))
            throw new ImapCommandException(ImapCommandResponse.No, "[TOOBIG] Message too large");

        var id = text.Split("\r\n")[0]["Message-ID: ".Length..];
        folder.Messages.Add((new RemoteMessage(folder.NextUid++, id.Length == 0 ? null : id, internalDate, internalDate,
            "Client", "client@exemple.fr", subject, raw.Length, flags), raw));
        AppendCount++;

        if (CrashAfterAppends is { } crash && AppendCount >= crash.After) crash.Cts.Cancel();
        if (DropConnectionAfterAppends is { } drop && AppendCount == drop + 1)
        {
            connection.IsConnected = false;
            throw new IOException("Connexion réinitialisée par le serveur");
        }
    }
}

internal sealed class FakeConnection(FakeMailServer server) : IMailboxConnection
{
    public bool IsConnected { get; set; }
    public string NamespacePrefix => server.NamespacePrefix;
    public char DefaultSeparator => server.Separator;

    public Task ConnectAsync(CancellationToken ct)
    {
        server.ConnectCount++;
        IsConnected = true;
        return Task.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new MailKit.ServiceNotConnectedException("non connecté");
    }

    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct)
    {
        EnsureConnected();
        return Task.FromResult<IReadOnlyList<RemoteFolder>>(server.Folders.Values.Select(f => new RemoteFolder(f.Path, server.Separator, f.Role)).ToList());
    }

    public Task<FolderState> GetFolderStateAsync(string path, CancellationToken ct)
    {
        EnsureConnected();
        var f = server.Folders[path];
        return Task.FromResult(new FolderState(f.UidValidity, f.Messages.Count));
    }

    public Task<IReadOnlyList<RemoteMessage>> ListMessagesAsync(string path, CancellationToken ct)
    {
        EnsureConnected();
        return Task.FromResult<IReadOnlyList<RemoteMessage>>(server.Folders[path].Messages.Select(m => m.Meta).ToList());
    }

    public Task<Stream> DownloadAsync(string path, uint uid, long size, CancellationToken ct)
    {
        EnsureConnected();
        var raw = server.Folders[path].Messages.First(m => m.Meta.Uid == uid).Raw;
        return Task.FromResult<Stream>(new MemoryStream(raw));
    }

    public Task CreateFolderAsync(string path, SpecialFolder role, CancellationToken ct)
    {
        EnsureConnected();
        server.AddFolder(path, role);
        return Task.CompletedTask;
    }

    public async Task AppendAsync(string path, Stream message, MailFlags flags, DateTimeOffset internalDate, CancellationToken ct)
    {
        EnsureConnected();
        using var copy = new MemoryStream();
        await message.CopyToAsync(copy, ct);
        server.OnAppend(server.Folders[path], copy.ToArray(), flags, internalDate, this);
    }

    public Task<bool> ContainsMessageIdAsync(string path, string messageId, CancellationToken ct)
    {
        EnsureConnected();
        return Task.FromResult(server.Folders[path].Messages.Any(m => m.Meta.MessageId == messageId));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeConnectionFactory(params FakeMailServer[] servers) : IMailboxConnectionFactory
{
    public IMailboxConnection Create(ImapEndpoint endpoint, IAuthProvider auth) => new FakeConnection(servers.Single(s => s.Host == endpoint.Host));
}

/// <summary>Protection réversible sans DPAPI (les tests tournent aussi hors Windows).</summary>
internal sealed class FakeProtector : ISecretProtector
{
    public byte[] Protect(string secret) => Encoding.UTF8.GetBytes(new string(secret.Reverse().ToArray()));
    public string Unprotect(byte[] protectedSecret) => new(Encoding.UTF8.GetString(protectedSecret).Reverse().ToArray());
}
