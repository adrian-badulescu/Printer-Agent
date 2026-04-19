using PrinterAgent.Domain;
using PrinterAgent.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace PrinterAgent.Application.UseCases;

public interface IHeartbeatService
{
    Task SendHeartbeatAsync(CancellationToken cancellationToken = default);
}

public class HeartbeatService : IHeartbeatService
{
    private readonly IBackendClient _backendClient;
    private readonly IMacResolver _macResolver;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        IBackendClient backendClient,
        IMacResolver macResolver,
        IAppConfiguration appConfiguration,
        ILogger<HeartbeatService> logger)
    {
        _backendClient = backendClient;
        _macResolver = macResolver;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var agentInfo = new AgentInfo
            {
                AgentId = _macResolver.GetMacAddress(),
                RestaurantId = _appConfiguration.RestaurantId,
                Version = _appConfiguration.Version,
                Printers = _appConfiguration.Printers // Typically you'd map current dynamic status here
            };

            await _backendClient.SendHeartbeatAsync(agentInfo, cancellationToken);
            _logger.LogInformation("Heartbeat sent successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send heartbeat.");
        }
    }
}
