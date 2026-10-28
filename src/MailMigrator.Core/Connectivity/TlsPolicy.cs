using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Imap;

namespace MailMigrator.Core.Connectivity;

/// <summary>
/// Vérification des certificats TLS, identique sur Windows et Android : autorité de confiance,
/// date de validité et nom du serveur sont toujours exigés.
/// Seule exception : un statut de révocation « inconnu » (Android ne permet pas à .NET d'interroger
/// les listes de révocation / OCSP) n'est pas bloquant ; un certificat réellement révoqué l'est.
/// </summary>
public static class TlsPolicy
{
    private static readonly X509ChainStatusFlags RevocationUnknown =
        X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    /// <summary>Applique la politique au client ; <paramref name="onRejected"/> reçoit le motif d'un refus.</summary>
    public static void Configure(ImapClient client, Action<string>? onRejected = null)
    {
        // Android : la vérification de révocation en ligne n'est pas prise en charge par la plateforme.
        client.CheckCertificateRevocation = !OperatingSystem.IsAndroid();
        client.ServerCertificateValidationCallback = (_, certificate, chain, errors) =>
        {
            if (IsAcceptable(errors, chain)) return true;
            onRejected?.Invoke(Describe(certificate, chain, errors));
            return false;
        };
    }

    public static bool IsAcceptable(SslPolicyErrors errors, X509Chain? chain)
    {
        if (errors == SslPolicyErrors.None) return true;
        // Nom ou certificat absent : toujours refusé.
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || chain is null) return false;
        // Chaîne invalide uniquement parce que la révocation n'a pas pu être vérifiée : accepté.
        return chain.ChainStatus.Length > 0 && chain.ChainStatus.All(s => (s.Status & ~RevocationUnknown) == X509ChainStatusFlags.NoError);
    }

    /// <summary>Motif lisible du refus, pour « Voir le détail technique ».</summary>
    public static string Describe(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var parts = new List<string> { $"Erreurs TLS : {errors}" };
        if (certificate is not null)
        {
            var c = new X509Certificate2(certificate);
            parts.Add($"Certificat : {c.Subject} — émis par {c.Issuer} — valide du {c.NotBefore:dd/MM/yyyy} au {c.NotAfter:dd/MM/yyyy}");
            var names = c.GetNameInfo(X509NameType.DnsName, false);
            if (!string.IsNullOrEmpty(names)) parts.Add($"Nom principal : {names}");
        }
        if (chain?.ChainStatus.Length > 0)
            parts.Add("Chaîne : " + string.Join(", ", chain.ChainStatus.Select(s => $"{s.Status} ({s.StatusInformation.Trim()})")));
        return string.Join(" | ", parts);
    }
}
