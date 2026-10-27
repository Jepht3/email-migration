namespace MailMigrator.Core.Connectivity;

public enum ConnectionError
{
    None,
    MissingPassword,
    AuthenticationFailed,
    AppPasswordRequired,
    HostUnreachable,
    CertificateInvalid,
    ImapDisabled,
    Timeout,
    Throttled,
    OAuthFailed,
    Unknown,
}

/// <summary>Message humain en français + action proposée, jamais une exception brute.</summary>
public sealed record ConnectionErrorInfo(ConnectionError Code, string Title, string Explanation, string ActionLabel)
{
    public static ConnectionErrorInfo For(ConnectionError code) => code switch
    {
        ConnectionError.MissingPassword => new(code, "Mot de passe manquant",
            "Saisissez le mot de passe de cette boîte avant de tester la connexion.", "Saisir le mot de passe"),
        ConnectionError.AuthenticationFailed => new(code, "Mot de passe refusé",
            "Le serveur a refusé l'identifiant ou le mot de passe. Vérifiez-les, en respectant les majuscules.", "Vérifier le mot de passe"),
        ConnectionError.AppPasswordRequired => new(code, "Mot de passe habituel refusé",
            "Ce fournisseur n'accepte pas le mot de passe du compte pour IMAP. Deux solutions : « Se connecter avec… » ci-dessus, "
            + "ou un mot de passe d'application de 16 caractères. Pour en créer un, activez d'abord la validation en deux étapes du compte ; "
            + "sur Google Workspace, l'administrateur doit aussi l'autoriser (console d'administration → Sécurité → Validation en deux étapes).",
            "Créer un mot de passe d'application"),
        ConnectionError.HostUnreachable => new(code, "Serveur injoignable",
            "Impossible de joindre le serveur IMAP. Vérifiez son adresse et votre connexion Internet.", "Réessayer"),
        ConnectionError.CertificateInvalid => new(code, "Certificat non valide",
            "Le certificat de sécurité du serveur n'est pas reconnu (expiré ou émis pour un autre nom).", "Voir le détail technique"),
        ConnectionError.ImapDisabled => new(code, "Accès IMAP désactivé",
            "L'accès IMAP est désactivé pour cette boîte. Activez-le dans les réglages du webmail, puis réessayez.", "Réessayer"),
        ConnectionError.Timeout => new(code, "Délai dépassé",
            "Le serveur met trop de temps à répondre. Il est peut-être surchargé.", "Réessayer"),
        ConnectionError.Throttled => new(code, "Trop de requêtes",
            "Le serveur limite temporairement le débit. La migration ralentit automatiquement puis reprend.", "Patienter"),
        ConnectionError.OAuthFailed => new(code, "Autorisation expirée ou refusée",
            "L'accès accordé via « Se connecter avec… » n'est plus valable (révoqué, mot de passe du compte changé, ou plus de 6 mois sans usage). Reconnectez la boîte.", "Se reconnecter"),
        ConnectionError.Unknown => new(code, "Connexion impossible",
            "Une erreur inattendue est survenue pendant la connexion.", "Voir le détail technique"),
        _ => new(code, "Connexion OK", "", ""),
    };
}
