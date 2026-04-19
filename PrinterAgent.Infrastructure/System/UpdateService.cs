using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Infrastructure.Security;

namespace PrinterAgent.Infrastructure.System;

public class UpdateService : IUpdateService
{
    private readonly IBackendClient _backendClient;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(
        IBackendClient backendClient,
        IAppConfiguration appConfiguration,
        ILogger<UpdateService> logger)
    {
        _backendClient = backendClient;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    public async Task CheckAndApplyUpdateAsync(string agentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var updateInfo = await _backendClient.CheckForUpdatesAsync(agentId, cancellationToken);
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

            var installerPath = Path.Combine(Path.GetTempPath(), $"PrinterAgent_Update_{updateInfo.Version}.exe");

            await using (var stream = await _backendClient.DownloadAsync(downloadUri, cancellationToken))
            await using (var fs = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.CopyToAsync(fs, cancellationToken);
            }

            _logger.LogInformation("Download complete. Starting installer and exiting.");

            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/SILENT /Update",
                UseShellExecute = true
            };
            Process.Start(psi);

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while checking for or applying updates.");
        }
    }
}
