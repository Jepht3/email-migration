using System.Security.Cryptography;
using System.Text;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Archive;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Engine;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using MailMigrator.Tests.Engine;
using MimeKit;

namespace MailMigrator.Tests.Archive;

public sealed class LocalArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2024, 3, 12, 9, 30, 0, TimeSpan.FromHours(1));
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lwb-archive-{Guid.NewGuid():N}");
    private readonly FakeProtector _protector = new();

    private string Dir(string name) => Path.Combine(_root, name);

    private static MemoryStream Raw(string messageId, string subject, int bodyBytes = 100, int seed = 1)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Jean Dupont", "jean@exemple.fr"));
        message.To.Add(new MailboxAddress("", "contact@exemple.fr"));
        message.Subject = subject;
        message.Date = Day;
        message.MessageId = messageId;
        var bytes = new byte[bodyBytes];
        new Random(seed).NextBytes(bytes);
        message.Body = new BodyBuilder { TextBody = "Bonjour", Attachments = { { "piece.bin", bytes } } }.ToMessageBody();
        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return stream;
    }

    // ------------------------------------------------------------------ Chiffrement

    [Theory]
    [InlineData(10)]
    [InlineData(5 * 1024 * 1024 + 7)]
    public async Task Crypto_round_trip_restores_exact_bytes(int length)
    {
        Directory.CreateDirectory(_root);
        var crypto = new ArchiveCrypto(ArchiveCrypto.NewKey());
        var data = RandomNumberGenerator.GetBytes(length);
        var file = Dir("message.lwb");

        await crypto.EncryptToFileAsync(new MemoryStream(data), file, default);
        using var output = new MemoryStream();
        await crypto.DecryptFileAsync(file, output, default);

        Assert.Equal(data, output.ToArray());
        Assert.NotEqual(data.Take(10), File.ReadAllBytes(file).Skip(20).Take(10));
    }

    [Fact]
    public async Task Crypto_detects_tampering_and_wrong_key()
    {
        Directory.CreateDirectory(_root);
        var key = ArchiveCrypto.NewKey();
        var file = Dir("m.lwb");
        await new ArchiveCrypto(key).EncryptToFileAsync(new MemoryStream(Encoding.UTF8.GetBytes("Devis client Dupont")), file, default);

        await Assert.ThrowsAsync<ArchiveException>(() => new ArchiveCrypto(ArchiveCrypto.NewKey()).DecryptFileAsync(file, new MemoryStream(), default));

        var bytes = File.ReadAllBytes(file);
        bytes[25] ^= 0x01;
        File.WriteAllBytes(file, bytes);
        await Assert.ThrowsAsync<ArchiveException>(() => new ArchiveCrypto(key).DecryptFileAsync(file, new MemoryStream(), default));
    }

    // ------------------------------------------------------------------ Boîte « sauvegarde locale »

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Archive_stores_and_reads_back_messages_with_metadata(bool encrypted)
    {
        var endpoint = ImapEndpoint.LocalArchive(Dir("boite"), "contact@exemple.fr", encrypted);
        var original = Raw("<devis-42@exemple.fr>", "Devis client Dupont", 6 * 1024 * 1024);

        await using (var archive = new LocalArchiveMailbox(endpoint, _protector))
        {
            await archive.ConnectAsync(default);
            Assert.Empty(await archive.ListFoldersAsync(default)); // rien n'est créé avant la première écriture
            await archive.CreateFolderAsync("Clients/Dupont", SpecialFolder.None, default);
            await archive.CreateFolderAsync("Envoyés", SpecialFolder.Sent, default);
            await archive.AppendAsync("Envoyés", original, MailFlags.Seen | MailFlags.Answered, Day, default);
        }

        // Réouverture : tout est relu depuis le disque.
        await using var reopened = new LocalArchiveMailbox(endpoint, _protector);
        await reopened.ConnectAsync(default);
        var folders = await reopened.ListFoldersAsync(default);
        Assert.Contains(folders, f => f.Path == "Envoyés" && f.Role == SpecialFolder.Sent);
        Assert.Contains(folders, f => f.Path == "Clients/Dupont");

        var message = Assert.Single(await reopened.ListMessagesAsync("Envoyés", default));
        Assert.Equal(("<devis-42@exemple.fr>", "Devis client Dupont", "jean@exemple.fr"), (message.MessageId, message.Subject, message.FromAddress));
        Assert.Equal(MailFlags.Seen | MailFlags.Answered, message.Flags);
        Assert.Equal(Day, message.InternalDate);
        Assert.Equal(original.Length, message.Size);
        Assert.True(await reopened.ContainsMessageIdAsync("Envoyés", "devis-42@exemple.fr", default));

        await using var content = await reopened.DownloadAsync("Envoyés", message.Uid, message.Size, default);
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        Assert.Equal(original.ToArray(), copy.ToArray());

        // Sur le disque : lisible en EML, illisible si chiffré.
        var file = Directory.GetFiles(Dir("boite"), "*.eml*", SearchOption.AllDirectories).Single();
        var onDisk = File.ReadAllText(file, Encoding.Latin1);
        Assert.Equal(!encrypted, onDisk.Contains("Devis client Dupont"));
        Assert.EndsWith(encrypted ? ".eml.lwb" : ".eml", file);
        Assert.Contains(Path.Combine("Mail", "Envoyés"), file);
    }

    [Fact]
    public async Task Encrypted_archive_cannot_be_opened_by_another_windows_account()
    {
        var endpoint = ImapEndpoint.LocalArchive(Dir("boite"), "a@b.fr");
        await using (var archive = new LocalArchiveMailbox(endpoint, _protector))
        {
            await archive.ConnectAsync(default);
            await archive.AppendAsync("INBOX", Raw("<1@x>", "Test"), MailFlags.None, Day, default);
        }

        await using var other = new LocalArchiveMailbox(endpoint, new OtherUserProtector());
        var error = await Assert.ThrowsAsync<ArchiveException>(() => other.ConnectAsync(default));
        Assert.Contains("autre session Windows", error.Message);
    }

    [Fact]
    public async Task Summary_and_capacity_check()
    {
        var endpoint = ImapEndpoint.LocalArchive(Dir("boite"), "contact@exemple.fr");
        await using (var archive = new LocalArchiveMailbox(endpoint, _protector))
        {
            await archive.ConnectAsync(default);
            await archive.AppendAsync("INBOX", Raw("<1@x>", "Un"), MailFlags.None, Day, default);
            await archive.AppendAsync("INBOX", Raw("<2@x>", "Deux"), MailFlags.Seen, Day, default);

            var tooBig = await Assert.ThrowsAsync<ArchiveException>(() => archive.EnsureCapacityAsync(long.MaxValue / 4, default));
            Assert.Contains("Espace disque insuffisant", tooBig.Message);
            await archive.EnsureCapacityAsync(1024, default);
        }

        var summary = LocalArchiveMailbox.ReadSummary(Dir("boite"))!;
        Assert.Equal(("contact@exemple.fr", true, 1, 2), (summary.Label, summary.Encrypted, summary.Folders, summary.Messages));
        Assert.Null(LocalArchiveMailbox.ReadSummary(Dir("absent")));
    }

    [Theory]
    [InlineData("INBOX", "INBOX")]
    [InlineData("Clients/Dupont", "Clients\\Dupont")]
    [InlineData("Factures: 2024?", "Factures_ 2024_")]
    [InlineData("CON", "_CON")]
    [InlineData("Archives./Vieux ", "Archives\\Vieux")]
    public void Folder_names_become_safe_windows_directories(string imapPath, string expected)
    {
        Assert.Equal(expected.Replace('\\', Path.DirectorySeparatorChar), LocalArchiveMailbox.SafeDirectory(imapPath));
    }

    // ------------------------------------------------------------------ Parcours complet A → PC → B

    private sealed class Factory(FakeProtector protector, params FakeMailServer[] servers) : IMailboxConnectionFactory
    {
        public IMailboxConnection Create(ImapEndpoint endpoint, IAuthProvider auth) => endpoint.IsLocalArchive
            ? new LocalArchiveMailbox(endpoint, protector)
            : new FakeConnection(servers.Single(s => s.Host == endpoint.Host));
    }

    private sealed class OtherUserProtector : ISecretProtector
    {
        public byte[] Protect(string secret) => throw new CryptographicException();
        public string Unprotect(byte[] protectedSecret) => throw new CryptographicException("Clé introuvable.");
    }

    [Fact]
    public async Task Backup_then_restore_after_dns_switch_without_temporary_mailbox()
    {
        // Serveur A (ancien, espace de noms « INBOX. ») et serveur B (nouveau).
        var a = new FakeMailServer("imap.ancien.fr", "INBOX.", '.');
        a.AddFolder("INBOX.Sent", SpecialFolder.Sent);
        a.AddFolder("INBOX.Clients");
        a.AddFolder("INBOX.Clients.Dupont");
        for (var i = 1; i <= 4; i++) a.Add("INBOX", $"<in{i}@a>", $"Message {i}", Day.AddDays(-i), MailFlags.Seen);
        a.Add("INBOX.Sent", "<sent@a>", "Réponse", Day, MailFlags.Seen | MailFlags.Answered);
        a.Add("INBOX.Clients.Dupont", null, "Contrat sans Message-ID", Day.AddDays(-30), MailFlags.Flagged);

        var b = new FakeMailServer("imap.nouveau.fr");
        b.AddFolder("Envoyés", SpecialFolder.Sent);

        using var store = new SqliteMigrationStore(Dir("etat.db"));
        var service = new MigrationService(store, new Factory(_protector, a, b), _protector, new RetryPolicy { Delay = (_, _) => Task.CompletedTask });
        var serverA = new ImapEndpoint("imap.ancien.fr", 993, SecurityMode.SslOnConnect, "contact@client.fr");
        var serverB = new ImapEndpoint("imap.nouveau.fr", 993, SecurityMode.SslOnConnect, "contact@client.fr");
        var archive = ImapEndpoint.LocalArchive(Dir("Sauvegardes/contact@client.fr"), "contact@client.fr");
        var keep = new MigrationOptions { ErasePasswordsWhenDone = false };

        // 1. Sauvegarde A → PC.
        var backup = service.CreateJob(new MigrationJobDefinition { SourceEmail = "contact@client.fr", Source = serverA, DestinationEmail = "Ce PC", Destination = archive, Options = keep }, "mdp-a", "");
        Assert.Equal(JobStatus.Completed, await service.Open(backup).RunAsync());
        Assert.Equal(6, LocalArchiveMailbox.ReadSummary(archive.LocalArchivePath!)!.Messages);

        // 2. Pendant la propagation DNS, un email arrive encore sur A : mise à jour de la sauvegarde.
        a.Add("INBOX", "<tardif@a>", "Arrivé pendant la propagation", Day.AddDays(1));
        Assert.Equal(JobStatus.Completed, await service.Open(backup).RunAsync());
        Assert.Equal(7, LocalArchiveMailbox.ReadSummary(archive.LocalArchivePath!)!.Messages);

        // 3. Restauration PC → B, deux fois (aucun doublon).
        var restore = service.CreateJob(new MigrationJobDefinition { SourceEmail = "Ce PC", Source = archive, DestinationEmail = "contact@client.fr", Destination = serverB, Options = keep }, "", "mdp-b");
        Assert.Equal(JobStatus.Completed, await service.Open(restore).RunAsync());
        Assert.Equal(JobStatus.Completed, await service.Open(restore).RunAsync());

        Assert.Equal(7, b.AppendCount);
        Assert.Equal(5, b.Folders["INBOX"].Messages.Count);
        var sent = Assert.Single(b.Folders["Envoyés"].Messages).Meta; // rôle « Envoyés » conservé de A à B
        Assert.Equal(MailFlags.Seen | MailFlags.Answered, sent.Flags);
        Assert.Equal(Day, sent.InternalDate);
        Assert.Equal(MailFlags.Flagged, Assert.Single(b.Folders["Clients/Dupont"].Messages).Meta.Flags);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
