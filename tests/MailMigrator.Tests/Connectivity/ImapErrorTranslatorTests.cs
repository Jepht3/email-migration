using System.Net.Sockets;
using MailKit.Security;
using MailMigrator.Core.Connectivity;

namespace MailMigrator.Tests.Connectivity;

public class ImapErrorTranslatorTests
{
    [Fact]
    public void Wrong_password_on_generic_server()
    {
        var error = ImapErrorTranslator.Translate(new AuthenticationException("Authentication failed."), "mail.garage.fr");

        Assert.Equal(ConnectionError.AuthenticationFailed, error);
    }

    [Theory]
    [InlineData("imap.gmail.com")]
    [InlineData("outlook.office365.com")]
    public void Refused_password_on_big_providers_suggests_app_password(string host)
    {
        var error = ImapErrorTranslator.Translate(new AuthenticationException("[AUTHENTICATIONFAILED] Invalid credentials (Failure)"), host);

        Assert.Equal(ConnectionError.AppPasswordRequired, error);
    }

    [Fact]
    public void Explicit_application_specific_password_message()
    {
        var error = ImapErrorTranslator.Translate(
            new AuthenticationException("[ALERT] Application-specific password required: https://support.google.com/accounts/answer/185833"));

        Assert.Equal(ConnectionError.AppPasswordRequired, error);
    }

    [Fact]
    public void Imap_disabled_takes_precedence_over_authentication()
    {
        var error = ImapErrorTranslator.Translate(new AuthenticationException("[ALERT] Your account is not enabled for IMAP use."), "imap.gmail.com");

        Assert.Equal(ConnectionError.ImapDisabled, error);
    }

    [Fact]
    public void Tls_handshake_failure_is_certificate_problem()
    {
        var inner = new System.Security.Authentication.AuthenticationException("The remote certificate is invalid according to the validation procedure.");

        var error = ImapErrorTranslator.Translate(new SslHandshakeException("An error occurred while attempting to establish an SSL or TLS connection.", inner));

        Assert.Equal(ConnectionError.CertificateInvalid, error);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, ConnectionError.HostUnreachable)]
    [InlineData(SocketError.ConnectionRefused, ConnectionError.HostUnreachable)]
    [InlineData(SocketError.TimedOut, ConnectionError.Timeout)]
    public void Socket_errors(SocketError code, ConnectionError expected)
    {
        Assert.Equal(expected, ImapErrorTranslator.Translate(new SocketException((int)code)));
    }

    [Fact]
    public void Cancellation_from_internal_timeout()
    {
        Assert.Equal(ConnectionError.Timeout, ImapErrorTranslator.Translate(new OperationCanceledException()));
    }

    [Fact]
    public void Throttling_is_detected()
    {
        var error = ImapErrorTranslator.Translate(new IOException("NO [UNAVAILABLE] Account exceeded command or bandwidth limits."));

        Assert.Equal(ConnectionError.Throttled, error);
    }

    [Fact]
    public void Every_error_has_a_french_message_and_an_action()
    {
        foreach (var code in Enum.GetValues<ConnectionError>().Where(c => c != ConnectionError.None))
        {
            var info = ConnectionErrorInfo.For(code);
            Assert.False(string.IsNullOrWhiteSpace(info.Title));
            Assert.False(string.IsNullOrWhiteSpace(info.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(info.ActionLabel));
        }
    }

    [Fact]
    public void Technical_detail_includes_inner_exceptions()
    {
        var detail = ImapErrorTranslator.Flatten(new IOException("outer", new SocketException((int)SocketError.HostNotFound)));

        Assert.StartsWith("IOException: outer → SocketException:", detail);
    }
}
