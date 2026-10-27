using MailKit.Net.Imap;
using MailKit.Security;

namespace MailMigrator.Core.Connectivity.OAuth;

/// <summary>Résultat d'une connexion « Se connecter avec… ».</summary>
public sealed record OAuthSignInResult(OAuthCredential Credential, OAuthTokens Tokens);

/// <summary>Connexion interactive : ouvre le navigateur, attend le retour local, échange le code.</summary>
public sealed class OAuthSignIn(OAuthClient client)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <param name="openBrowser">Ouvre l'URL dans le navigateur par défaut (fourni par l'application).</param>
    public async Task<OAuthSignInResult> SignInAsync(OAuthProviderSettings provider, string? loginHint, Action<string> openBrowser, CancellationToken ct)
    {
        using var receiver = new LoopbackReceiver(provider.RedirectHost);
        var verifier = OAuthClient.CreateVerifier();
        var state = OAuthClient.CreateState();
        openBrowser(OAuthClient.BuildAuthorizationUrl(provider, receiver.RedirectUri, state, OAuthClient.Challenge(verifier), loginHint));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        string code;
        try
        {
            code = await receiver.WaitForCodeAsync(state, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OAuthException("Délai dépassé : la connexion n'a pas été terminée dans le navigateur.");
        }

        var tokens = await client.ExchangeCodeAsync(provider, code, verifier, receiver.RedirectUri, ct).ConfigureAwait(false);
        if (tokens.RefreshToken is null)
            throw new OAuthException($"{provider.DisplayName} n'a pas fourni de jeton de rafraîchissement : réessayez en acceptant toutes les autorisations.");
        var email = tokens.Email ?? loginHint ?? throw new OAuthException("Adresse du compte introuvable dans la réponse.");
        return new OAuthSignInResult(new OAuthCredential(provider.Key, email, tokens.RefreshToken), tokens);
    }
}

/// <summary>
/// Authentification IMAP XOAUTH2. Le jeton d'accès (valable ~1 h) est renouvelé automatiquement
/// à partir du jeton de rafraîchissement, y compris lors des reconnexions d'une longue migration.
/// </summary>
public sealed class OAuth2AuthProvider(OAuthProviderSettings provider, OAuthCredential credential, OAuthClient client, OAuthTokens? initial = null) : IAuthProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OAuthTokens? _tokens = initial;
    private string _refreshToken = credential.RefreshToken;

    public string Name => $"OAuth2 {provider.DisplayName}";

    /// <summary>Microsoft peut émettre un nouveau jeton de rafraîchissement : il est conservé ici.</summary>
    public string CurrentRefreshToken => _refreshToken;

    public async Task AuthenticateAsync(ImapClient imap, string userName, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        await imap.AuthenticateAsync(new SaslMechanismOAuth2(credential.Email, token), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens is null || _tokens.ExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(3))
            {
                _tokens = await client.RefreshAsync(provider, _refreshToken, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(_tokens.RefreshToken)) _refreshToken = _tokens.RefreshToken;
            }
            return _tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Construit le bon fournisseur d'authentification à partir du secret stocké (mot de passe ou OAuth2).</summary>
public sealed class AuthProviderFactory(OAuthRegistry registry, OAuthClient client)
{
    public static AuthProviderFactory PasswordOnly { get; } = new(OAuthRegistry.Empty, new OAuthClient(new HttpClient()));

    public IAuthProvider Create(string secret)
    {
        if (!OAuthCredential.TryParse(secret, out var credential)) return new PasswordAuthProvider(secret);
        var provider = registry.Get(credential!.Provider)
            ?? throw new InvalidOperationException($"La connexion {credential.Provider} n'est plus configurée dans l'application (identifiants OAuth absents).");
        return new OAuth2AuthProvider(provider, credential, client);
    }
}
