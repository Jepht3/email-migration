using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailMigrator.Core.Connectivity;

namespace MailMigrator.Tests.Connectivity;

public class TlsPolicyTests
{
    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
    }

    [Fact]
    public void Valid_certificate_is_accepted()
    {
        Assert.True(TlsPolicy.IsAcceptable(SslPolicyErrors.None, null));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors)]
    public void Wrong_name_or_missing_certificate_is_always_rejected(SslPolicyErrors errors)
    {
        Assert.False(TlsPolicy.IsAcceptable(errors, null));
    }

    [Fact]
    public void Untrusted_self_signed_certificate_is_rejected_with_a_readable_reason()
    {
        using var certificate = SelfSigned("mail.exemple.fr");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(certificate);

        Assert.False(TlsPolicy.IsAcceptable(SslPolicyErrors.RemoteCertificateChainErrors, chain));
        var reason = TlsPolicy.Describe(certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors);
        Assert.Contains("CN=mail.exemple.fr", reason);
        Assert.Contains("UntrustedRoot", reason);
    }
}
