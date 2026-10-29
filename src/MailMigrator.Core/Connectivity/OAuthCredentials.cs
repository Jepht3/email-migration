using System.Text.Json;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailMigrator.Core.Connectivity;

public enum OAuthProvider { None, Google, Microsoft }

// Stored only inside the existing encrypted secret column, never in job definitions.
public sealed class OAuthCredentials
{
    public OAuthProvider Provider { get; set; }
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public override string ToString() => "Identifiants OAuth protégés";
}

public static class OAuthEndpoints
{
    public static void ValidateMailbox(Models.ImapEndpoint endpoint)
    {
        if (endpoint.OAuthProvider == OAuthProvider.None || endpoint.IsLocalArchive) return;
        var expected = endpoint.OAuthProvider switch
        {
            OAuthProvider.Google => "imap.gmail.com",
            OAuthProvider.Microsoft => "outlook.office365.com",
            _ => throw new InvalidOperationException("Fournisseur OAuth invalide.")
        };
        if (endpoint.Security == Models.SecurityMode.None || !endpoint.Host.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Les identifiants OAuth ne peuvent être envoyés qu’au serveur sécurisé du fournisseur.");
    }

    public static string Token(OAuthProvider provider) => provider switch
    {
        OAuthProvider.Google => "https://oauth2.googleapis.com/token",
        OAuthProvider.Microsoft => "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        _ => throw new InvalidOperationException("Fournisseur OAuth invalide.")
    };

    public static string Scope(OAuthProvider provider) => provider switch
    {
        OAuthProvider.Google => "https://mail.google.com/",
        OAuthProvider.Microsoft => "https://outlook.office.com/IMAP.AccessAsUser.All offline_access",
        _ => throw new InvalidOperationException("Fournisseur OAuth invalide.")
    };

    public static async Task ApplyResponseAsync(HttpResponseMessage response, OAuthCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Connexion OAuth refusée ou expirée. Reconnectez la boîte auprès du fournisseur.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("access_token", out var access) || string.IsNullOrWhiteSpace(access.GetString()))
            throw new InvalidOperationException("Le fournisseur n’a pas retourné de jeton OAuth utilisable.");
        if (root.TryGetProperty("token_type", out var type) && !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Type de jeton OAuth non pris en charge.");
        if (root.TryGetProperty("scope", out var granted))
        {
            var required = Scope(credentials.Provider).Split(' ')[0];
            if (!(granted.GetString() ?? "").Split(' ').Contains(required, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("L’autorisation IMAP n’a pas été accordée par le fournisseur.");
        }
        credentials.AccessToken = access.GetString()!;
        credentials.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3600);
        if (root.TryGetProperty("refresh_token", out var refresh) && !string.IsNullOrEmpty(refresh.GetString()))
            credentials.RefreshToken = refresh.GetString()!;
    }
}

public sealed class OAuthAuthProvider(OAuthCredentials credentials, Action<string>? save = null, HttpClient? http = null) : IAuthProvider
{
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Name => "OAuth2";

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (credentials.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2) && credentials.AccessToken.Length > 0)
                return credentials.AccessToken;
            if (credentials.RefreshToken.Length == 0)
                throw new InvalidOperationException("La connexion OAuth a expiré. Reconnectez cette boîte.");
            var values = new Dictionary<string, string>
            {
                ["client_id"] = credentials.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credentials.RefreshToken
            };
            if (!string.IsNullOrEmpty(credentials.ClientSecret)) values["client_secret"] = credentials.ClientSecret;
            if (credentials.Provider == OAuthProvider.Microsoft) values["scope"] = OAuthEndpoints.Scope(credentials.Provider);
            using var body = new FormUrlEncodedContent(values);
            using var response = await (http ?? DefaultHttp).PostAsync(OAuthEndpoints.Token(credentials.Provider), body, cancellationToken).ConfigureAwait(false);
            await OAuthEndpoints.ApplyResponseAsync(response, credentials, cancellationToken).ConfigureAwait(false);
            save?.Invoke(JsonSerializer.Serialize(credentials));
            return credentials.AccessToken;
        }
        finally { _gate.Release(); }
    }

    public async Task AuthenticateAsync(ImapClient client, string userName, CancellationToken cancellationToken)
    {
        if (!client.IsSecure) throw new InvalidOperationException("OAuth exige une connexion IMAP chiffrée.");
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        await client.AuthenticateAsync(new SaslMechanismOAuth2(userName, token), cancellationToken).ConfigureAwait(false);
    }
}

public static class MailboxAuth
{
    public static IAuthProvider Create(OAuthProvider provider, string secret, Action<string>? save = null) => provider == OAuthProvider.None
        ? new PasswordAuthProvider(secret)
        : new OAuthAuthProvider(Read(provider, secret), save);

    private static OAuthCredentials Read(OAuthProvider provider, string secret)
    {
        OAuthCredentials? value;
        try { value = JsonSerializer.Deserialize<OAuthCredentials>(secret); }
        catch (JsonException) { throw new InvalidOperationException("Identifiants OAuth invalides. Reconnectez la boîte."); }
        if (value is null || value.Provider != provider || string.IsNullOrWhiteSpace(value.ClientId))
            throw new InvalidOperationException("Identifiants OAuth invalides. Reconnectez la boîte.");
        return value;
    }
}
