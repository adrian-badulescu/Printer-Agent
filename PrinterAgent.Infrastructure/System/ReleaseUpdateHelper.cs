using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.System;

internal static class ReleaseUpdateHelper
{
    /// <summary>First release with aligned WiX MajorUpgrade + delayed silent installer.</summary>
    internal const string MinimumAutoApplyVersion = "1.5.4";

    public static bool IsRemoteVersionNewer(string localVersion, string remoteVersion)
    {
        if (!Version.TryParse(NormalizeVersion(localVersion), out var local)
            || !Version.TryParse(NormalizeVersion(remoteVersion), out var remote))
        {
            return false;
        }

        return remote > local;
    }

    public static bool IsManifestApplicable(ReleaseManifest manifest, string localVersion)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.DownloadUrl)
            || string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return false;
        }

        return IsRemoteVersionNewer(localVersion, manifest.Version);
    }

    /// <summary>Agents below 1.5.4 must not self-exit for update (legacy installer race left service missing).</summary>
    public static bool SupportsSilentAutoApply(string localVersion)
    {
        if (!Version.TryParse(NormalizeVersion(localVersion), out var local)
            || !Version.TryParse(NormalizeVersion(MinimumAutoApplyVersion), out var minimum))
        {
            return false;
        }

        return local >= minimum;
    }

    /// <summary>Normalizes semver tags like <c>v1.5.0</c> for <see cref="Version.TryParse"/>.</summary>
    internal static string NormalizeVersion(string version)
    {
        var trimmed = version.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 1
            ? trimmed[1..]
            : trimmed;
    }
}
