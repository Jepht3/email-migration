using System.Net;
using System.Text.Json;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;

namespace MailMigrator.Tests.Connectivity;

public sealed class OAuthTests
{
    private static OAuthCredentials Credential() => new()
    {
        Provider = OAuthProvider.Microsoft, ClientId = "public-client",
        AccessToken = "old-test-access", RefreshToken = "old-test-refresh",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => run(request);
    }

    [Fact]
    public async Task Concurrent_reconnections_refresh_once_and_persist_rotated_credentials()
    {
        var count = 0;
        string? saved = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            Interlocked.Increment(ref count);
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", request.RequestUri!.AbsoluteUri);
            var form = await request.Content!.ReadAsStringAsync();
            Assert.Contains("grant_type=refresh_token", form);
            Assert.Contains("refresh_token=old-test-refresh", form);
            await Task.Delay(20);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"access_token":"new-test-access","refresh_token":"rotated-test-refresh","expires_in":3600,"token_type":"Bearer","scope":"https://outlook.office.com/IMAP.AccessAsUser.All offline_access"}
                """) };
        }));
        var provider = new OAuthAuthProvider(Credential(), value => saved = value, http);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetAccessTokenAsync(default)));
        Assert.Equal(1, count);
        Assert.All(tokens, token => Assert.Equal("new-test-access", token));
        Assert.Equal("rotated-test-refresh", JsonSerializer.Deserialize<OAuthCredentials>(saved!)!.RefreshToken);
    }

    [Fact]
    public async Task Valid_token_does_not_call_token_endpoint()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected network call")));
        var credential = Credential();
        credential.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        Assert.Equal(credential.AccessToken, await new OAuthAuthProvider(credential, http: http).GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task Refresh_failure_does_not_expose_provider_response_or_overwrite_saved_credentials()
    {
        var saved = false;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("sensitive-provider-response") })));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new OAuthAuthProvider(Credential(), _ => saved = true, http).GetAccessTokenAsync(default));
        Assert.DoesNotContain("sensitive-provider-response", error.Message);
        Assert.False(saved);
    }

    [Fact]
    public async Task Refresh_preserves_previous_refresh_token_when_provider_does_not_rotate_it()
    {
        var credential = Credential();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"access_token":"new-test-access","expires_in":3600}""") };
        await OAuthEndpoints.ApplyResponseAsync(response, credential, default);
        Assert.Equal("old-test-refresh", credential.RefreshToken);
    }

    [Fact]
    public async Task Wrong_granted_scope_is_rejected_before_replacing_access_token()
    {
        var credential = Credential();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"access_token":"wrong-scope-test","scope":"User.Read"}""") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => OAuthEndpoints.ApplyResponseAsync(response, credential, default));
        Assert.Equal("old-test-access", credential.AccessToken);
    }

    [Theory]
    [InlineData("imap.attacker.example", SecurityMode.SslOnConnect)]
    [InlineData("imap.gmail.com", SecurityMode.None)]
    [InlineData("imap.gmail.com.attacker.example", SecurityMode.SslOnConnect)]
    public void OAuth_credentials_cannot_be_sent_to_untrusted_or_unencrypted_server(string host, SecurityMode security)
    {
        var endpoint = new ImapEndpoint(host, 993, security, "test@example.com") { OAuthProvider = OAuthProvider.Google };
        Assert.Throws<InvalidOperationException>(() => OAuthEndpoints.ValidateMailbox(endpoint));
    }

    [Fact]
    public void Passwords_that_look_like_json_remain_passwords_and_legacy_endpoints_default_to_password()
    {
        Assert.IsType<PasswordAuthProvider>(MailboxAuth.Create(OAuthProvider.None, "{not-oauth}"));
        var endpoint = JsonSerializer.Deserialize<ImapEndpoint>("""{"Host":"imap.example.com","Port":993,"Security":0,"UserName":"user"}""")!;
        Assert.Equal(OAuthProvider.None, endpoint.OAuthProvider);
    }

    [Fact]
    public void Rotation_is_persisted_and_does_not_resurrect_erased_credentials()
    {
        using var store = new SqliteMigrationStore(":memory:");
        var job = new MigrationJobDefinition { SourceEmail = "a@example.com", DestinationEmail = "b@example.com" };
        store.CreateJob(job, [1], [2]);
        store.UpdateSecret(job.Id, true, [3]);
        Assert.Equal(new byte[] { 3 }, store.GetSecrets(job.Id).Source);
        Assert.Equal(new byte[] { 2 }, store.GetSecrets(job.Id).Destination);
        store.ClearSecrets(job.Id);
        store.UpdateSecret(job.Id, true, [4]);
        Assert.Null(store.GetSecrets(job.Id).Source);
    }
}
