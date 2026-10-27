using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailMigrator.Core.Connectivity;

/// <summary>
/// Transforme une exception technique (MailKit, sockets, TLS) en erreur compréhensible.
/// Le texte brut est conservé à part pour « Voir le détail technique ».
/// </summary>
public static class ImapErrorTranslator
{
    private static readonly string[] AppPasswordHints =
    [
        "application-specific password", "app password", "app-specific password",
        "mot de passe d'application", "weblogin", "web login required", "please log in via your web browser",
    ];

    private static readonly string[] ImapDisabledHints =
    [
        "not enabled for imap", "imap access is disabled", "imap is disabled", "imap disabled",
        "imap access not allowed", "imap n'est pas activ", "acces imap desactive",
    ];

    private static readonly string[] ThrottleHints =
    [
        "[limit]", "[unavailable]", "throttl", "too many", "rate limit", "bandwidth limits",
        "try again later", "server busy", "request is throttled",
    ];

    /// <param name="host">Serveur concerné : permet d'adapter le message (Gmail, Microsoft 365…).</param>
    public static ConnectionError Translate(Exception exception, string? host = null)
    {
        var text = Flatten(exception).ToLowerInvariant();

        if (exception is OAuth.OAuthException) return ConnectionError.OAuthFailed;

        if (exception is OperationCanceledException or TimeoutException || text.Contains("timed out") || text.Contains("délai"))
            return ConnectionError.Timeout;

        if (exception is SslHandshakeException || FindInner<System.Security.Authentication.AuthenticationException>(exception) is not null
            || text.Contains("certificate"))
            return ConnectionError.CertificateInvalid;

        if (FindInner<SocketException>(exception) is { } socket)
            return socket.SocketErrorCode == SocketError.TimedOut ? ConnectionError.Timeout : ConnectionError.HostUnreachable;

        if (ImapDisabledHints.Any(text.Contains)) return ConnectionError.ImapDisabled;
        if (ThrottleHints.Any(text.Contains)) return ConnectionError.Throttled;
        if (AppPasswordHints.Any(text.Contains)) return ConnectionError.AppPasswordRequired;

        if (exception is MailKit.Security.AuthenticationException || text.Contains("authenticationfailed") || text.Contains("invalid credentials")
            || text.Contains("login failed") || text.Contains("authenticate failed"))
        {
            // Gmail et Microsoft 365 refusent le mot de passe principal quand la double
            // authentification est active : on oriente vers le mot de passe d'application.
            return IsAppPasswordProvider(host) ? ConnectionError.AppPasswordRequired : ConnectionError.AuthenticationFailed;
        }

        if (exception is ImapProtocolException or ServiceNotConnectedException or IOException)
            return ConnectionError.HostUnreachable;

        return ConnectionError.Unknown;
    }

    private static bool IsAppPasswordProvider(string? host) =>
        host is not null && (host.Contains("gmail", StringComparison.OrdinalIgnoreCase)
                             || host.Contains("office365", StringComparison.OrdinalIgnoreCase)
                             || host.Contains("outlook", StringComparison.OrdinalIgnoreCase)
                             || host.Contains("yahoo", StringComparison.OrdinalIgnoreCase)
                             || host.Contains("mail.me.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Détail technique lisible (types et messages de la chaîne d'exceptions), sans données sensibles.</summary>
    public static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (var e = exception; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" → ", parts);
    }

    private static T? FindInner<T>(Exception exception) where T : Exception
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is T match) return match;
        return null;
    }
}
