using MailKit.Net.Imap;

namespace MailMigrator.Core.Connectivity;

/// <summary>
/// Méthode d'authentification IMAP. V1 : mot de passe (ou mot de passe d'application).
/// OAuth2 Gmail / Microsoft 365 s'ajoutera par une autre implémentation (SaslMechanismOAuth2).
/// </summary>
public interface IAuthProvider
{
    string Name { get; }

    Task AuthenticateAsync(ImapClient client, string userName, CancellationToken cancellationToken);
}

public sealed class PasswordAuthProvider(string password) : IAuthProvider
{
    public string Name => "Mot de passe";

    public Task AuthenticateAsync(ImapClient client, string userName, CancellationToken cancellationToken)
    {
        // XOAUTH2 n'est jamais tenté avec un mot de passe.
        client.AuthenticationMechanisms.Remove("XOAUTH2");
        client.AuthenticationMechanisms.Remove("OAUTHBEARER");
        return client.AuthenticateAsync(userName, password, cancellationToken);
    }
}
