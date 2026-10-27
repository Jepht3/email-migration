using System.Text.Json;

namespace MailMigrator.Core.Connectivity.OAuth;

/// <summary>Paramètres OAuth2 d'un fournisseur (Google, Microsoft).</summary>
public sealed record OAuthProviderSettings(
    string Key,
    string DisplayName,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string ClientId,
    string? ClientSecret,
    IReadOnlyList<string> Scopes,
    string RedirectHost,
    IReadOnlyDictionary<string, string> ExtraAuthorizeParameters)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>
    /// Google : client « Application de bureau ». La portée https://mail.google.com/ donne l'accès IMAP.
    /// access_type=offline + prompt=consent garantissent un jeton de rafraîchissement.
    /// </summary>
    public static OAuthProviderSettings Google(string clientId, string? clientSecret) => new(
        "google", "Google",
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        clientId, clientSecret,
        ["https://mail.google.com/", "openid", "email"],
        "127.0.0.1",
        new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" });

    /// <summary>Microsoft 365 et Outlook.com : application publique « multi-locataire + comptes personnels ».</summary>
    public static OAuthProviderSettings Microsoft(string clientId) => new(
        "microsoft", "Microsoft",
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        clientId, null,
        ["https://outlook.office.com/IMAP.AccessAsUser.All", "offline_access", "openid", "email"],
        "localhost",
        new Dictionary<string, string> { ["prompt"] = "select_account" });
}

/// <summary>Jetons renvoyés par le serveur d'autorisation.</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Email);

/// <summary>
/// Ce qui est conservé (chiffré DPAPI, comme un mot de passe) pour une boîte connectée en OAuth2 :
/// jamais le mot de passe du compte, seulement le jeton de rafraîchissement.
/// </summary>
public sealed record OAuthCredential(string Provider, string Email, string RefreshToken)
{
    private const string Prefix = "oauth2:";

    public string Serialize() => Prefix + JsonSerializer.Serialize(this);

    public static bool TryParse(string secret, out OAuthCredential? credential)
    {
        credential = null;
        if (!secret.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            credential = JsonSerializer.Deserialize<OAuthCredential>(secret[Prefix.Length..]);
        }
        catch (JsonException)
        {
        }
        return credential is not null;
    }
}

/// <summary>Fournisseurs OAuth disponibles (ceux dont l'identifiant client est configuré).</summary>
public sealed class OAuthRegistry(IEnumerable<OAuthProviderSettings> providers)
{
    private readonly Dictionary<string, OAuthProviderSettings> _providers =
        providers.Where(p => p.IsConfigured).ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static OAuthRegistry Empty { get; } = new([]);

    public OAuthProviderSettings? Get(string key) => _providers.GetValueOrDefault(key);

    /// <summary>Clé du fournisseur OAuth adapté à un serveur IMAP (configuré ou non).</summary>
    public static string? KeyForHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host.Contains("gmail") || host.Contains("google")) return "google";
        if (host.Contains("office365") || host.Contains("outlook") || host.Contains("hotmail")) return "microsoft";
        return null;
    }

    public OAuthProviderSettings? ForHost(string host) => KeyForHost(host) is { } key ? Get(key) : null;
}
