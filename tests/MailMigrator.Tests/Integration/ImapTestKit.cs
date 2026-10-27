using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MailMigrator.Core.Models;
using MimeKit;

namespace MailMigrator.Tests.Integration;

/// <summary>Outils pour préparer de vraies boîtes IMAP (GreenMail ou boîtes de test réelles).</summary>
internal static class ImapTestKit
{
    public static async Task<ImapClient> ConnectAsync(ImapEndpoint endpoint, string password)
    {
        var client = new ImapClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Security switch
        {
            SecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SecurityMode.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        });
        await client.AuthenticateAsync(endpoint.UserName, password);
        return client;
    }

    /// <summary>Vide la boîte : supprime tous les dossiers sauf INBOX, et tous les messages d'INBOX.</summary>
    public static async Task ResetAsync(ImapEndpoint endpoint, string password)
    {
        using var client = await ConnectAsync(endpoint, password);
        var folders = await client.GetFoldersAsync(client.PersonalNamespaces[0]);
        foreach (var folder in folders.Where(f => !f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.FullName.Length))
        {
            try { await folder.DeleteAsync(); } catch (ImapCommandException) { /* déjà supprimé avec son parent */ }
        }
        await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
        if (client.Inbox.Count > 0)
        {
            await client.Inbox.StoreAsync(UniqueIdRange.All, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true });
            await client.Inbox.ExpungeAsync();
        }
        await client.DisconnectAsync(true);
    }

    public static MimeMessage Message(string? messageId, string subject, DateTimeOffset date, int attachmentBytes = 0, int seed = 0)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Client Dupont", "client@exemple.fr"));
        message.To.Add(new MailboxAddress("Contact", "contact@exemple.fr"));
        message.Subject = subject;
        message.Date = date;
        if (messageId is null) message.Headers.Remove(HeaderId.MessageId);
        else message.MessageId = messageId;

        var body = new BodyBuilder { TextBody = $"Bonjour,\n\nCeci est le message « {subject} ».\n\nCordialement" };
        if (attachmentBytes > 0) body.Attachments.Add("piece-jointe.bin", Payload(attachmentBytes, seed));
        message.Body = body.ToMessageBody();
        if (messageId is null) message.Headers.Remove(HeaderId.MessageId);
        return message;
    }

    /// <summary>Contenu pseudo-aléatoire reproductible (pour vérifier une pièce jointe octet par octet).</summary>
    public static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Crée les dossiers nécessaires puis y dépose les messages, avec drapeaux et date de réception.</summary>
    public static async Task SeedAsync(ImapEndpoint endpoint, string password, IEnumerable<(string Folder, MimeMessage Message, MessageFlags Flags)> messages)
    {
        using var client = await ConnectAsync(endpoint, password);
        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        foreach (var group in messages.GroupBy(m => m.Folder))
        {
            var folder = group.Key.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await EnsureFolderAsync(personal, group.Key, client.PersonalNamespaces[0].DirectorySeparator);
            foreach (var (_, message, flags) in group)
                await folder.AppendAsync(new AppendRequest(message, flags, message.Date));
        }
        await client.DisconnectAsync(true);
    }

    private static async Task<IMailFolder> EnsureFolderAsync(IMailFolder root, string path, char separator)
    {
        var parent = root;
        foreach (var name in path.Split('/'))
        {
            IMailFolder? next = null;
            try { next = await parent.GetSubfolderAsync(name); } catch (FolderNotFoundException) { }
            parent = next ?? await parent.CreateAsync(name, true) ?? throw new InvalidOperationException(name);
        }
        return parent;
    }

    /// <summary>Contenu d'une boîte : dossier → messages (Message-ID, sujet, drapeaux, date de réception).</summary>
    public static async Task<Dictionary<string, List<IMessageSummary>>> InventoryAsync(ImapEndpoint endpoint, string password)
    {
        using var client = await ConnectAsync(endpoint, password);
        var result = new Dictionary<string, List<IMessageSummary>>();
        var folders = (await client.GetFoldersAsync(client.PersonalNamespaces[0])).Prepend(client.Inbox).DistinctBy(f => f.FullName);
        foreach (var folder in folders)
        {
            if (folder.Attributes.HasFlag(FolderAttributes.NoSelect)) continue;
            await folder.OpenAsync(FolderAccess.ReadOnly);
            result[folder.FullName] = folder.Count == 0
                ? []
                : (await folder.FetchAsync(0, -1, MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.UniqueId)).ToList();
        }
        await client.DisconnectAsync(true);
        return result;
    }
}
