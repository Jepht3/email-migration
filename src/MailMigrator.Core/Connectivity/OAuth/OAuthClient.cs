using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MailMigrator.Core.Connectivity.OAuth;

/// <summary>
/// Client OAuth2 « code d'autorisation + PKCE » (RFC 7636) pour application installée,
/// sans dépendance aux bibliothèques Google / Microsoft.
/// </summary>
public sealed class OAuthClient(HttpClient http)
{
    // ---------- PKCE ----------

    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---------- Autorisation ----------

    public static string BuildAuthorizationUrl(OAuthProviderSettings provider, string redirectUri, string state, string codeChallenge, string? loginHint)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', provider.Scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        if (!string.IsNullOrWhiteSpace(loginHint)) query["login_hint"] = loginHint;
        foreach (var (k, v) in provider.ExtraAuthorizeParameters) query[k] = v;
        return provider.AuthorizationEndpoint + "?" + string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    public Task<OAuthTokens> ExchangeCodeAsync(OAuthProviderSettings provider, string code, string verifier, string redirectUri, CancellationToken ct) =>
        RequestAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        }, ct);

    public Task<OAuthTokens> RefreshAsync(OAuthProviderSettings provider, string refreshToken, CancellationToken ct) =>
        RequestAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = string.Join(' ', provider.Scopes),
        }, ct);

    private async Task<OAuthTokens> RequestAsync(OAuthProviderSettings provider, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = provider.ClientId;
        if (!string.IsNullOrEmpty(provider.ClientSecret)) form["client_secret"] = provider.ClientSecret;
        using var response = await http.PostAsync(provider.TokenEndpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var access))
        {
            var error = root.TryGetProperty("error_description", out var d) ? d.GetString()
                : root.TryGetProperty("error", out var e) ? e.GetString() : $"HTTP {(int)response.StatusCode}";
            throw new OAuthException($"{provider.DisplayName} a refusé l'autorisation : {error}");
        }
        var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) ? seconds : 3600;
        return new OAuthTokens(
            access.GetString()!,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            root.TryGetProperty("id_token", out var id) ? EmailFromIdToken(id.GetString()) : null);
    }

    /// <summary>Adresse du compte lue dans le jeton d'identité (charge utile JWT, sans vérification de signature : usage d'affichage).</summary>
    public static string? EmailFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var claim in new[] { "email", "preferred_username", "upn" })
                if (json.RootElement.TryGetProperty(claim, out var value) && value.GetString() is { Length: > 0 } email) return email;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
        }
        return null;
    }
}

public sealed class OAuthException(string message) : Exception(message);
