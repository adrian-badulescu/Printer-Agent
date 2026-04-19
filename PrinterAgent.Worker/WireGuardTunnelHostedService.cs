using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrinterAgent.Worker.Config;

namespace PrinterAgent.Worker;

/// <summary>
/// Rulează primul: asigură că tunelul WireGuard (serviciu Windows) e activ înainte de Redis/worker.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WireGuardTunnelHostedService : IHostedService
{
    private readonly IOptions<WireGuardOptions> _options;
    private readonly ILogger<WireGuardTunnelHostedService> _logger;

    public WireGuardTunnelHostedService(
        IOptions<WireGuardOptions> options,
        ILogger<WireGuardTunnelHostedService> logger)
    {
        _options = options;
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
                "WireGuard: fișier config înregistrat (admin: import în aplicația WireGuard): {Path}",
                opt.ConfigFilePath);
        }

        if (string.IsNullOrWhiteSpace(opt.WindowsTunnelServiceName))
        {
            _logger.LogWarning(
                "WireGuard.Enabled=true dar WindowsTunnelServiceName lipsește; nu se verifică/pornește tunelul.");
            return Task.CompletedTask;
        }

        try
        {
            using var sc = new ServiceController(opt.WindowsTunnelServiceName);
            _logger.LogInformation(
                "WireGuard: serviciu {Service} are statusul {Status}.",
                opt.WindowsTunnelServiceName,
                sc.Status);

            if (sc.Status == ServiceControllerStatus.Running)
                return Task.CompletedTask;

            if (sc.Status == ServiceControllerStatus.Stopped && !opt.StartServiceIfStopped)
            {
                _logger.LogError(
                    "WireGuard: serviciul {Service} este oprit. Activați StartServiceIfStopped sau porniți tunelul din aplicația WireGuard.",
                    opt.WindowsTunnelServiceName);
                return Task.CompletedTask;
            }

            if (opt.StartServiceIfStopped && sc.Status == ServiceControllerStatus.Stopped)
            {
                _logger.LogInformation("WireGuard: pornire serviciu {Service}...", opt.WindowsTunnelServiceName);
                sc.Start();
            }

            var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.WaitForTunnelServiceSeconds, 5, 600));
            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                _logger.LogError(
                    "WireGuard: serviciul {Service} nu este Running după {Timeout}. Status curent: {Status}.",
                    opt.WindowsTunnelServiceName,
                    timeout,
                    sc.Status);
            }
            else
            {
                _logger.LogInformation("WireGuard: serviciu {Service} este Running.", opt.WindowsTunnelServiceName);
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(
                ex,
                "WireGuard: serviciul {Service} nu a fost găsit sau nu poate fi pornit (drepturi admin / nume greșit?).",
                opt.WindowsTunnelServiceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WireGuard: eroare la verificarea tunelului.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
