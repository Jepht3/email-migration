using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Archive;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Imap;

/// <summary>Serveur IMAP (MailKit) ou sauvegarde locale, selon le point de connexion.</summary>
public sealed class MailboxConnectionFactory(ISecretProtector protector) : IMailboxConnectionFactory
{
    private readonly MailKitConnectionFactory _imap = new();

    public IMailboxConnection Create(ImapEndpoint endpoint, IAuthProvider auth) =>
        endpoint.IsLocalArchive ? new LocalArchiveMailbox(endpoint, protector) : _imap.Create(endpoint, auth);
}
