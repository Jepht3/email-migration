namespace MailMigrator.Core.Abstractions;

/// <summary>
/// Chiffrement des mots de passe stockés. Sous Windows : DPAPI, portée utilisateur.
/// Une version hébergée fournira sa propre implémentation (coffre de clés).
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(string secret);
    string Unprotect(byte[] protectedSecret);
}
