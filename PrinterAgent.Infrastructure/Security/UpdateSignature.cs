using System.Security.Cryptography;
using System.Text;

namespace PrinterAgent.Infrastructure.Security;

public static class UpdateSignature
{
    public static string Compute(string secret, string version, string downloadUrl)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;

        var payload = BuildLegacyPayload(version, downloadUrl);
        return ComputePayload(secret, payload);
    }

    public static string ComputeManifest(string secret, string version, string downloadUrl, string sha256Hex)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;

        var payload = BuildManifestPayload(version, downloadUrl, sha256Hex);
        return ComputePayload(secret, payload);
    }

    public static bool Verify(string secret, string version, string downloadUrl, string? signatureHex)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHex))
            return false;

        var expected = Compute(secret, version, downloadUrl);
        return FixedTimeEqualsHex(expected, signatureHex);
    }

    public static bool VerifyManifest(
        string secret,
        string version,
        string downloadUrl,
        string sha256Hex,
        string? signatureHex)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHex))
            return false;

        var expected = ComputeManifest(secret, version, downloadUrl, sha256Hex);
        return FixedTimeEqualsHex(expected, signatureHex);
    }

    internal static string BuildLegacyPayload(string version, string downloadUrl) =>
        $"{version}|{downloadUrl}";

    internal static string BuildManifestPayload(string version, string downloadUrl, string sha256Hex) =>
        $"{version}|{downloadUrl}|{sha256Hex}";

    private static string ComputePayload(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static bool FixedTimeEqualsHex(string expectedHex, string actualHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHex),
                Convert.FromHexString(actualHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
