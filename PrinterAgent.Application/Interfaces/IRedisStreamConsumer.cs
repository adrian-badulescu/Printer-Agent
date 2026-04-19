namespace PrinterAgent.Application.Interfaces;

public interface IRedisStreamConsumer
{
    Task StartConsumingAsync(string restaurantId, CancellationToken cancellationToken = default);
}
