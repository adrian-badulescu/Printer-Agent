using System.Security.Cryptography;
using System.Text;

namespace PrinterAgent.Infrastructure.Security;

public static class UpdateSignature
{
    public static string Compute(string secret, string version, string downloadUrl)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;

        var payload = $"{version}|{downloadUrl}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    public static bool Verify(string secret, string version, string downloadUrl, string? signatureHex)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHex))
            return false;

        var expected = Compute(secret, version, downloadUrl);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected),
            Convert.FromHexString(signatureHex));
    }
}
