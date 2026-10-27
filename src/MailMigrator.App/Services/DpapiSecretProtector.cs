using System.Security.Cryptography;
using System.Text;
using MailMigrator.Core.Abstractions;

namespace MailMigrator.App.Services;

/// <summary>
/// Mots de passe chiffrés avec DPAPI, portée utilisateur Windows : seul ce compte Windows,
/// sur ce poste, peut les déchiffrer. L'entropie supplémentaire lie les données à l'application.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LEWEBMAX.MailMigrator.v1");

    public byte[] Protect(string secret) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] protectedSecret) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedSecret, Entropy, DataProtectionScope.CurrentUser));
}
