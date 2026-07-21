using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal static class FpMateStubSelfSignedCertificate
{
    public static X509Certificate2 Create(string? bindHost)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=FpMate Stub Local",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")],
                critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);

        if (!string.IsNullOrWhiteSpace(bindHost)
            && bindHost is not ("0.0.0.0" or "*" or "+")
            && IPAddress.TryParse(bindHost, out var bindIp))
        {
            san.AddIpAddress(bindIp);
        }
        else
        {
            foreach (var ip in Dns.GetHostAddresses(Environment.MachineName))
            {
                if (ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    san.AddIpAddress(ip);
            }
        }

        request.CertificateExtensions.Add(san.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(2);
        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        // Persist key material for Kestrel/Schannel (ephemeral ECDSA PFX reload can break TLS handshake on Windows).
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
    }
}
