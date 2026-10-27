using System.Net;
using System.Text;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Connectivity.OAuth;

namespace MailMigrator.Tests.Connectivity;

public class OAuthTests
{
    private static readonly OAuthProviderSettings Google = OAuthProviderSettings.Google("client-123.apps.googleusercontent.com", "secret-xyz");
    private static readonly OAuthProviderSettings Microsoft = OAuthProviderSettings.Microsoft("00000000-1111-2222-3333-444444444444");

    /// <summary>Serveur de jetons simulé : enregistre les formulaires reçus, renvoie les réponses programmées.</summary>
    private sealed class TokenServer : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Json)> Responses { get; } = new();
        public List<Dictionary<string, string>> Forms { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Forms.Add(body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' '))));
            var (status, json) = Responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static string IdToken(string email)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"email\":\"{email}\",\"sub\":\"42\"}}")}.signature";
    }

    [Fact]
    public void Pkce_challenge_matches_rfc7636_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthClient.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Assert.Equal(43, OAuthClient.CreateVerifier().Length);
    }

    [Fact]
    public void Authorization_url_carries_pkce_scopes_and_offline_access()
    {
        var url = OAuthClient.BuildAuthorizationUrl(Google, "http://127.0.0.1:5000/", "etat", "defi", "info@lewebmax.com");

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("code_challenge=defi", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("scope=https%3A%2F%2Fmail.google.com%2F%20openid%20email", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("login_hint=info%40lewebmax.com", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5000%2F", url);
    }

    [Fact]
    public async Task Code_exchange_returns_tokens_and_account_email()
    {
        var server = new TokenServer();
        server.Responses.Enqueue((HttpStatusCode.OK, $$"""{"access_token":"at-1","refresh_token":"rt-1","expires_in":3599,"id_token":"{{IdToken("info@lewebmax.com")}}"}"""));

        var tokens = await new OAuthClient(new HttpClient(server)).ExchangeCodeAsync(Google, "code-abc", "verif", "http://127.0.0.1:5000/", default);

        Assert.Equal(("at-1", "rt-1", "info@lewebmax.com"), (tokens.AccessToken, tokens.RefreshToken, tokens.Email));
        Assert.InRange(tokens.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        var form = Assert.Single(server.Forms);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("verif", form["code_verifier"]);
        Assert.Equal("secret-xyz", form["client_secret"]);
    }

    [Fact]
    public async Task Refused_refresh_raises_a_readable_error()
    {
        var server = new TokenServer();
        server.Responses.Enqueue((HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""));

        var error = await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(new HttpClient(server)).RefreshAsync(Microsoft, "rt-old", default));

        Assert.Contains("expired or revoked", error.Message);
        Assert.False(server.Forms[0].ContainsKey("client_secret")); // client public Microsoft
        Assert.Equal(ConnectionError.OAuthFailed, ImapErrorTranslator.Translate(error));
    }

    [Fact]
    public async Task Access_token_is_cached_and_rotated_refresh_token_kept()
    {
        var server = new TokenServer();
        server.Responses.Enqueue((HttpStatusCode.OK, """{"access_token":"at-1","refresh_token":"rt-2","expires_in":3600}"""));
        var provider = new OAuth2AuthProvider(Microsoft, new OAuthCredential("microsoft", "a@b.fr", "rt-1"), new OAuthClient(new HttpClient(server)));

        var first = await provider.GetAccessTokenAsync(default);
        var second = await provider.GetAccessTokenAsync(default);

        Assert.Equal(("at-1", "at-1"), (first, second));
        Assert.Single(server.Forms);
        Assert.Equal("rt-1", server.Forms[0]["refresh_token"]);
        Assert.Equal("rt-2", provider.CurrentRefreshToken);
    }

    [Fact]
    public void Credential_round_trips_and_plain_passwords_are_not_mistaken_for_it()
    {
        var credential = new OAuthCredential("google", "info@lewebmax.com", "1//0abc");

        Assert.True(OAuthCredential.TryParse(credential.Serialize(), out var parsed));
        Assert.Equal(credential, parsed);
        Assert.False(OAuthCredential.TryParse("MotDePasse-oauth2:", out _));
    }

    [Fact]
    public void Factory_builds_password_or_oauth_authentication()
    {
        var factory = new AuthProviderFactory(new OAuthRegistry([Google]), new OAuthClient(new HttpClient()));

        Assert.IsType<PasswordAuthProvider>(factory.Create("mot-de-passe"));
        Assert.IsType<OAuth2AuthProvider>(factory.Create(new OAuthCredential("google", "a@b.fr", "rt").Serialize()));
        Assert.Throws<InvalidOperationException>(() => factory.Create(new OAuthCredential("microsoft", "a@b.fr", "rt").Serialize()));
    }

    [Theory]
    [InlineData("imap.gmail.com", "google")]
    [InlineData("outlook.office365.com", "microsoft")]
    [InlineData("mail.jephte.xyz", null)]
    public void Provider_is_chosen_from_imap_host(string host, string? expected)
    {
        Assert.Equal(expected, OAuthRegistry.KeyForHost(host));
        Assert.Null(new OAuthRegistry([OAuthProviderSettings.Google("", null)]).ForHost("imap.gmail.com")); // non configuré
    }

    [Fact]
    public async Task Loopback_receiver_returns_code_and_ignores_other_requests()
    {
        using var receiver = new LoopbackReceiver("127.0.0.1");
        var wait = receiver.WaitForCodeAsync("etat-ok", default);
        using var browser = new HttpClient();

        await browser.GetAsync(receiver.RedirectUri + "favicon.ico");
        var page = await browser.GetStringAsync(receiver.RedirectUri + "?state=etat-ok&code=4%2F0Abc&scope=email");

        Assert.Equal("4/0Abc", await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Connexion réussie", page);
    }

    [Fact]
    public async Task Loopback_receiver_rejects_forged_state_and_reports_cancellation()
    {
        using (var receiver = new LoopbackReceiver("127.0.0.1"))
        {
            var wait = receiver.WaitForCodeAsync("attendu", default);
            await new HttpClient().GetAsync(receiver.RedirectUri + "?state=pirate&code=x");
            await Assert.ThrowsAsync<OAuthException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        using (var receiver = new LoopbackReceiver("127.0.0.1"))
        {
            var wait = receiver.WaitForCodeAsync("s", default);
            await new HttpClient().GetAsync(receiver.RedirectUri + "?error=access_denied&state=s");
            var error = await Assert.ThrowsAsync<OAuthException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("annulée", error.Message);
        }
    }
}
