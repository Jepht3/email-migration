using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Discovery;

namespace MailMigrator.Tests.Discovery;

public sealed class OAuthDetectionTests
{
    [Theory]
    [InlineData("imap.gmail.com", OAuthProvider.Google)]
    [InlineData(" IMAP.GMAIL.COM ", OAuthProvider.Google)]
    [InlineData("outlook.office365.com", OAuthProvider.Microsoft)]
    [InlineData("imap.gmail.com.example.org", OAuthProvider.None)]
    [InlineData("mail.google.example.org", OAuthProvider.None)]
    [InlineData("mail.example.org", OAuthProvider.None)]
    [InlineData(null, OAuthProvider.None)]
    public void Only_canonical_hosts_select_browser_sign_in(string? host, OAuthProvider expected)
        => Assert.Equal(expected, ProviderCatalog.OAuthForHost(host));

    [Theory]
    [InlineData("user@gmail.com", OAuthProvider.Google)]
    [InlineData("user@googlemail.com", OAuthProvider.Google)]
    [InlineData("user@outlook.fr", OAuthProvider.Microsoft)]
    [InlineData("user@hotmail.com", OAuthProvider.Microsoft)]
    [InlineData("user@live.fr", OAuthProvider.Microsoft)]
    [InlineData("user@orange.fr", OAuthProvider.None)]
    public void Known_addresses_suggest_the_matching_sign_in(string email, OAuthProvider expected)
        => Assert.Equal(expected, ProviderCatalog.OAuthForHost(ProviderCatalog.FromEmail(email)?.KnownEndpoint?.Host));
}
