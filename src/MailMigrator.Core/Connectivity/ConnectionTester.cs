using MailKit;
using MailKit.Net.Imap;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Connectivity;

/// <summary>Résultat d'un test de connexion.</summary>
public sealed record ConnectionTestResult(
    ConnectionError Error,
    string? TechnicalDetail,
    bool SupportsSpecialUse = false,
    int FolderCount = 0)
{
    public bool Success => Error == ConnectionError.None;
    public ConnectionErrorInfo Info => ConnectionErrorInfo.For(Error);
}

/// <summary>
/// Test de connexion complet : connexion TLS (certificat vérifié), authentification,
/// puis lecture de la liste des dossiers pour s'assurer que l'accès IMAP fonctionne vraiment.
/// </summary>
public sealed class ConnectionTester
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public async Task<ConnectionTestResult> TestAsync(ImapEndpoint endpoint, IAuthProvider auth, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        using var client = new ImapClient { Timeout = (int)DefaultTimeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Security.ToSocketOptions(), timeout.Token).ConfigureAwait(false);
            await auth.AuthenticateAsync(client, endpoint.UserName, timeout.Token).ConfigureAwait(false);

            var specialUse = (client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0;
            var folders = client.PersonalNamespaces.Count > 0
                ? await client.GetFoldersAsync(client.PersonalNamespaces[0], false, timeout.Token).ConfigureAwait(false)
                : [];
            await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
            return new ConnectionTestResult(ConnectionError.None, null, specialUse, folders.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(ImapErrorTranslator.Translate(ex, endpoint.Host), ImapErrorTranslator.Flatten(ex));
        }
    }
}
