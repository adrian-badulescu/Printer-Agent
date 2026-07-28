using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Security;

namespace PrinterAgent.Infrastructure.System;

public class UpdateService : IUpdateService
{
    private readonly IBackendClient _backendClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(
        IBackendClient backendClient,
        IHttpClientFactory httpClientFactory,
        IAppConfiguration appConfiguration,
        ILogger<UpdateService> logger)
    {
        _backendClient = backendClient;
        _httpClientFactory = httpClientFactory;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    public async Task CheckAndApplyUpdateAsync(string agentId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_appConfiguration.UpdateManifestUrl))
            {
                await CheckAndApplyFromManifestAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await CheckAndApplyFromBackendAsync(agentId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while checking for or applying updates.");
        }
    }

    private async Task CheckAndApplyFromManifestAsync(CancellationToken cancellationToken)
    {
        var manifestUrl = _appConfiguration.UpdateManifestUrl.Trim();
        var http = _httpClientFactory.CreateClient("ReleaseUpdate");

        using var response = await http.GetAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == global::System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Release manifest not found at {Url}.", manifestUrl);
            return;
        }

        response.EnsureSuccessStatusCode();

        var manifest = await response.Content.ReadFromJsonAsync<ReleaseManifest>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (manifest is null)
        {
            _logger.LogWarning("Release manifest at {Url} was empty or invalid JSON.", manifestUrl);
            return;
        }

        // #region agent log
        UpdateDebugLogger.Log("H-D", "UpdateService:manifest", "Manifest fetched", new
        {
            remoteVersion = manifest.Version,
            localVersion = _appConfiguration.Version,
            applicable = ReleaseUpdateHelper.IsManifestApplicable(manifest, _appConfiguration.Version)
        });
        // #endregion

        if (!ReleaseUpdateHelper.IsManifestApplicable(manifest, _appConfiguration.Version))
            return;

        if (!TryValidateManifestSignature(manifest))
        {
            // #region agent log
            UpdateDebugLogger.Log("H-D", "UpdateService:signature", "Signature rejected", new { manifest.Version });
            // #endregion
            return;
        }

        if (!ReleaseUpdateHelper.SupportsSilentAutoApply(_appConfiguration.Version))
        {
            // #region agent log
            UpdateDebugLogger.Log("H-C", "UpdateService:gate", "Auto-apply blocked (pre-minimum version)", new
            {
                localVersion = _appConfiguration.Version,
                minimum = ReleaseUpdateHelper.MinimumAutoApplyVersion,
                remoteVersion = manifest.Version
            });
            // #endregion
            _logger.LogWarning(
                "Update {RemoteVersion} is available but auto-apply requires agent {MinimumVersion}+ (current {LocalVersion}). " +
                "Install once manually; enrollment in ProgramData is preserved.",
                manifest.Version,
                ReleaseUpdateHelper.MinimumAutoApplyVersion,
                _appConfiguration.Version);
            return;
        }

        if (UpdateApplyGuard.ShouldSkipApply(out var skipReason))
        {
            // #region agent log
            UpdateDebugLogger.Log("H-E", "UpdateService:guard", "Apply skipped", new { skipReason });
            // #endregion
            _logger.LogInformation("Skipping update apply: {Reason}.", skipReason);
            return;
        }

        _logger.LogInformation(
            "Update available: {Version}. Downloading from {Url}",
            manifest.Version,
            manifest.DownloadUrl);

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri))
        {
            _logger.LogError("Invalid download URL in manifest: {Url}", manifest.DownloadUrl);
            return;
        }

        var updatesDir = UpdateInstallerLauncher.GetUpdatesDirectory();
        Directory.CreateDirectory(updatesDir);
        var installerPath = Path.Combine(
            updatesDir,
            $"PrinterAgent_Update_{ReleaseUpdateHelper.NormalizeVersion(manifest.Version)}.exe");

        await DownloadFileAsync(http, downloadUri, installerPath, cancellationToken).ConfigureAwait(false);

        if (!await VerifyFileSha256Async(installerPath, manifest.Sha256, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogError("Update rejected: SHA256 mismatch for version {Version}.", manifest.Version);
            TryDeleteInstaller(installerPath);
            UpdateApplyGuard.MarkApplyFailed();
            return;
        }

        try
        {
            UpdateApplyGuard.MarkApplyStarting();
            // #region agent log
            UpdateDebugLogger.Log("H-C", "UpdateService:launch", "Launching delayed installer", new
            {
                installerPath,
                delayPings = UpdateInstallerLauncher.DefaultDelayPingCount,
                localVersion = _appConfiguration.Version
            });
            // #endregion
            LaunchInstallerAndExit(installerPath);
        }
        catch
        {
            UpdateApplyGuard.MarkApplyFailed();
            throw;
        }
    }

    private bool TryValidateManifestSignature(ReleaseManifest manifest)
    {
        var secret = _appConfiguration.UpdateSignatureSecret;
        if (!string.IsNullOrEmpty(secret))
        {
            if (!UpdateSignature.VerifyManifest(
                    secret,
                    manifest.Version,
                    manifest.DownloadUrl,
                    manifest.Sha256,
                    manifest.Signature))
            {
                var installDirConfig = Path.Combine(AppContext.BaseDirectory, "agent.json");
                _logger.LogError(
                    "Update rejected: manifest signature mismatch for version {Version}. " +
                    "UpdateSignatureSecret is loaded from install-dir ({InstallDirConfig}), not ProgramData. " +
                    "Secret length={SecretLength}, manifest signature length={SignatureLength}. " +
                    "Run scripts/Verify-ReleaseManifestSignature.ps1 to compare payload.",
                    manifest.Version,
                    installDirConfig,
                    secret.Length,
                    manifest.Signature?.Length ?? 0);
                return false;
            }

            return true;
        }

        if (!string.IsNullOrEmpty(manifest.Signature))
        {
            _logger.LogWarning(
                "Release manifest is signed but agent has no UpdateSignatureSecret; skipping apply for version {Version}.",
                manifest.Version);
            return false;
        }

        _logger.LogWarning(
            "Applying unsigned release manifest for version {Version} (no UpdateSignatureSecret configured).",
            manifest.Version);
        return true;
    }

    private async Task CheckAndApplyFromBackendAsync(string agentId, CancellationToken cancellationToken)
    {
        var updateInfo = await _backendClient.CheckForUpdatesAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (updateInfo == null || !updateInfo.UpdateAvailable || updateInfo.Version == _appConfiguration.Version)
            return;

        if (!string.IsNullOrEmpty(_appConfiguration.UpdateSignatureSecret))
        {
            if (!UpdateSignature.Verify(
                    _appConfiguration.UpdateSignatureSecret,
                    updateInfo.Version,
                    updateInfo.DownloadUrl,
                    updateInfo.Signature))
            {
                _logger.LogError("Update rejected: signature mismatch for version {Version}.", updateInfo.Version);
                return;
            }
        }
        else if (!string.IsNullOrEmpty(updateInfo.Signature))
        {
            _logger.LogWarning("Backend sent update signature but agent has no UpdateSignatureSecret; skipping apply.");
            return;
        }

        _logger.LogInformation("Update available: {Version}. Downloading from {Url}", updateInfo.Version, updateInfo.DownloadUrl);

        if (!Uri.TryCreate(updateInfo.DownloadUrl, UriKind.Absolute, out var downloadUri))
        {
            _logger.LogError("Invalid download URL: {Url}", updateInfo.DownloadUrl);
            return;
        }

        var updatesDir = UpdateInstallerLauncher.GetUpdatesDirectory();
        Directory.CreateDirectory(updatesDir);
        var installerPath = Path.Combine(updatesDir, $"PrinterAgent_Update_{updateInfo.Version}.exe");
        var http = _httpClientFactory.CreateClient("ReleaseUpdate");
        await DownloadFileAsync(http, downloadUri, installerPath, cancellationToken).ConfigureAwait(false);
        LaunchInstallerAndExit(installerPath);
    }

    private static async Task DownloadFileAsync(
        HttpClient http,
        Uri downloadUri,
        string installerPath,
        CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync(downloadUri, cancellationToken).ConfigureAwait(false);
        await using var fs = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyFileSha256Async(
        string filePath,
        string expectedHex,
        CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash);
        return string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void LaunchInstallerAndExit(string installerPath)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "urs-agent-update.log");
        _logger.LogInformation(
            "Download complete. Scheduling silent installer in ~{DelaySeconds}s (log: {LogPath}). Service will exit.",
            UpdateInstallerLauncher.DefaultDelayPingCount - 1,
            logPath);

        UpdateInstallerLauncher.LaunchDelayedInstallAndExit(installerPath, logPath);
    }

    private void TryDeleteInstaller(string installerPath)
    {
        try
        {
            if (File.Exists(installerPath))
                File.Delete(installerPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete failed update installer at {Path}.", installerPath);
        }
    }
}
