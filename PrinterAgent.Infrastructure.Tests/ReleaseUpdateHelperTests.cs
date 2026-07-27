using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.System;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class ReleaseUpdateHelperTests
{
    [Theory]
    [InlineData("1.4.9", "1.5.0", true)]
    [InlineData("1.5.0", "1.5.0", false)]
    [InlineData("1.5.1", "1.5.0", false)]
    [InlineData("v1.4.0", "1.5.0", true)]
    public void IsRemoteVersionNewer_compares_semver(string local, string remote, bool expected)
    {
        Assert.Equal(expected, ReleaseUpdateHelper.IsRemoteVersionNewer(local, remote));
    }

    [Fact]
    public void IsManifestApplicable_requires_newer_version_and_fields()
    {
        var manifest = new ReleaseManifest
        {
            Version = "1.5.0",
            DownloadUrl = "https://example.com/setup.exe",
            Sha256 = "ABC",
            Signature = "SIG"
        };

        Assert.True(ReleaseUpdateHelper.IsManifestApplicable(manifest, "1.4.9"));
        Assert.False(ReleaseUpdateHelper.IsManifestApplicable(manifest, "1.5.0"));
        Assert.False(ReleaseUpdateHelper.IsManifestApplicable(
            new ReleaseManifest
            {
                Version = "",
                DownloadUrl = "https://example.com/setup.exe",
                Sha256 = "ABC",
                Signature = "SIG"
            },
            "1.0.0"));
    }

    [Theory]
    [InlineData("1.5.4", true)]
    [InlineData("1.5.5", true)]
    [InlineData("1.4.9", false)]
    [InlineData("1.5.3", false)]
    public void SupportsSilentAutoApply_requires_1_5_4_or_newer(string local, bool expected)
    {
        Assert.Equal(expected, ReleaseUpdateHelper.SupportsSilentAutoApply(local));
    }
}
