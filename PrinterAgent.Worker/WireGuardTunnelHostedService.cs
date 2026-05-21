using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrinterAgent.Infrastructure.Networking;
using PrinterAgent.Worker.Config;

namespace PrinterAgent.Worker;

/// <summary>
/// Rulează primul: asigură că tunelul WireGuard (serviciu Windows) e activ înainte de Redis/worker.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WireGuardTunnelHostedService : IHostedService
{
    private readonly IOptions<WireGuardOptions> _options;
    private readonly IWireGuardTunnelManager _tunnelManager;
    private readonly ILogger<WireGuardTunnelHostedService> _logger;

    public WireGuardTunnelHostedService(
        IOptions<WireGuardOptions> options,
        IWireGuardTunnelManager tunnelManager,
        ILogger<WireGuardTunnelHostedService> logger)
    {
        _options = options;
        _tunnelManager = tunnelManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opt = _options.Value;
        if (!opt.Enabled)
            return Task.CompletedTask;

        if (!string.IsNullOrWhiteSpace(opt.ConfigFilePath))
        {
            _logger.LogInformation(
                "WireGuard: config file path: {Path}",
                opt.ConfigFilePath);
        }

        var serviceName = ResolveWindowsTunnelServiceName(opt);
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            _logger.LogWarning(
                "WireGuard.Enabled=true but WindowsTunnelServiceName/TunnelName/ConfigFilePath are missing; tunnel will not be checked or started.");
            return Task.CompletedTask;
        }

        try
        {
            using var sc = new ServiceController(serviceName);
            _logger.LogInformation(
                "WireGuard: service {Service} status is {Status}.",
                serviceName,
                sc.Status);

            if (sc.Status == ServiceControllerStatus.Running)
                return Task.CompletedTask;

            if (sc.Status == ServiceControllerStatus.Stopped && !opt.StartServiceIfStopped)
            {
                _logger.LogError(
                    "WireGuard: service {Service} is stopped. Enable StartServiceIfStopped or start the tunnel from the WireGuard app.",
                    serviceName);
                return Task.CompletedTask;
            }

            if (opt.StartServiceIfStopped && sc.Status == ServiceControllerStatus.Stopped)
            {
                _logger.LogInformation("WireGuard: starting service {Service}...", serviceName);
                sc.Start();
            }

            var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.WaitForTunnelServiceSeconds, 5, 600));
            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                _logger.LogError(
                    "WireGuard: service {Service} is not Running after {Timeout}. Current status: {Status}.",
                    serviceName,
                    timeout,
                    sc.Status);
            }
            else
            {
                _logger.LogInformation("WireGuard: service {Service} is Running.", serviceName);
            }
        }
        catch (InvalidOperationException ex)
        {
            if (opt.InstallTunnelServiceIfMissing && !string.IsNullOrWhiteSpace(opt.ConfigFilePath) && File.Exists(opt.ConfigFilePath))
            {
                _logger.LogWarning(
                    "WireGuard: service {Service} not found; attempting to install tunnel service from {ConfPath}.",
                    serviceName,
                    opt.ConfigFilePath);

                return InstallThenStartAsync(serviceName, opt.ConfigFilePath, cancellationToken);
            }

            _logger.LogError(
                ex,
                "WireGuard: service {Service} not found or could not be started (admin rights / wrong name?).",
                serviceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WireGuard: error while checking tunnel.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task InstallThenStartAsync(string serviceName, string confPath, CancellationToken cancellationToken)
    {
        try
        {
            await _tunnelManager.InstallTunnelServiceAsync(confPath, cancellationToken).ConfigureAwait(false);

            using var sc = new ServiceController(serviceName);
            _logger.LogInformation("WireGuard: after install, service {Service} status is {Status}.", serviceName, sc.Status);

            var opt = _options.Value;
            // installtunnelservice often leaves the service StartPending or Running; only start when fully Stopped.
            if (sc.Status == ServiceControllerStatus.Stopped && opt.StartServiceIfStopped)
            {
                _logger.LogInformation("WireGuard: starting service {Service}...", serviceName);
                sc.Start();
            }
            else if (sc.Status == ServiceControllerStatus.Stopped && !opt.StartServiceIfStopped)
            {
                _logger.LogWarning(
                    "WireGuard: service {Service} is stopped after install; enable StartServiceIfStopped to start it automatically.",
                    serviceName);
                return;
            }

            var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.WaitForTunnelServiceSeconds, 5, 600));
            if (sc.Status != ServiceControllerStatus.Running)
                sc.WaitForStatus(ServiceControllerStatus.Running, timeout);

            if (sc.Status != ServiceControllerStatus.Running)
            {
                _logger.LogError(
                    "WireGuard: service {Service} is not Running after {Timeout}. Current status: {Status}.",
                    serviceName,
                    timeout,
                    sc.Status);
            }
            else
            {
                _logger.LogInformation("WireGuard: service {Service} is Running.", serviceName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WireGuard: failed to install/start tunnel service from {ConfPath}.", confPath);
        }
    }

    private static string ResolveWindowsTunnelServiceName(WireGuardOptions opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.WindowsTunnelServiceName))
            return opt.WindowsTunnelServiceName.Trim();

        var tunnelName = opt.TunnelName?.Trim();
        if (string.IsNullOrWhiteSpace(tunnelName) && !string.IsNullOrWhiteSpace(opt.ConfigFilePath))
        {
            try { tunnelName = Path.GetFileNameWithoutExtension(opt.ConfigFilePath); }
            catch { tunnelName = null; }
        }

        return string.IsNullOrWhiteSpace(tunnelName) ? string.Empty : $"WireGuardTunnel${tunnelName}";
    }
}
