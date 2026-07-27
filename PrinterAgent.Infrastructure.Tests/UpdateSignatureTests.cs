using PrinterAgent.Infrastructure.Security;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class UpdateSignatureTests
{
    private const string Secret = "test-update-secret";

    [Fact]
    public void VerifyManifest_accepts_valid_signature()
    {
        const string version = "1.5.0";
        const string downloadUrl = "https://example.com/URSPrinterAgentSetup.exe";
        const string sha256 = "ABC123";

        var signature = UpdateSignature.ComputeManifest(Secret, version, downloadUrl, sha256);

        Assert.True(UpdateSignature.VerifyManifest(Secret, version, downloadUrl, sha256, signature));
    }

    [Fact]
    public void VerifyManifest_rejects_tampered_sha256()
    {
        const string version = "1.5.0";
        const string downloadUrl = "https://example.com/URSPrinterAgentSetup.exe";
        const string sha256 = "ABC123";

        var signature = UpdateSignature.ComputeManifest(Secret, version, downloadUrl, sha256);

        Assert.False(UpdateSignature.VerifyManifest(Secret, version, downloadUrl, "DEF456", signature));
    }

    [Fact]
    public void Verify_legacy_backend_signature_still_works()
    {
        const string version = "1.2.7";
        const string downloadUrl = "https://example.com/setup.exe";

        var signature = UpdateSignature.Compute(Secret, version, downloadUrl);

        Assert.True(UpdateSignature.Verify(Secret, version, downloadUrl, signature));
    }
}
