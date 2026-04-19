using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.UseCases;
using PrinterAgent.Domain;
using StackExchange.Redis;

namespace PrinterAgent.Infrastructure.Redis;

public class RedisStreamConsumer : IRedisStreamConsumer
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IPrintJobProcessor _printJobProcessor;
    private readonly ILogger<RedisStreamConsumer> _logger;
    private readonly IMacResolver _macResolver;
    private readonly IAppConfiguration _appConfiguration;

    public RedisStreamConsumer(
        IConnectionMultiplexer redis,
        IPrintJobProcessor printJobProcessor,
        ILogger<RedisStreamConsumer> logger,
        IMacResolver macResolver,
        IAppConfiguration appConfiguration)
    {
        _redis = redis;
        _printJobProcessor = printJobProcessor;
        _logger = logger;
        _macResolver = macResolver;
        _appConfiguration = appConfiguration;
    }

    public async Task StartConsumingAsync(string restaurantId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var prefix = _appConfiguration.RedisStreamKeyPrefix.Trim().TrimEnd('.');
        var streamName = $"{prefix}.{restaurantId}";
        var groupName = _appConfiguration.RedisConsumerGroup.Trim();
        var consumerName = _macResolver.GetMacAddress();

        _logger.LogInformation(
            "Redis Streams: stream={Stream} group={Group} consumer={Consumer} (conexiune: {Conn})",
            streamName,
            groupName,
            consumerName,
            _appConfiguration.RedisConnectionSummary);

        try
        {
            await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists, which is fine
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var messages = await db.StreamReadGroupAsync(
                    streamName,
                    groupName,
                    consumerName,
                    ">",
                    count: 1);

                if (messages.Length > 0)
                {
                    var message = messages[0];
                    var payloadJson = message.Values.FirstOrDefault(v => v.Name == "payload").Value.ToString();
                    
                    if (!string.IsNullOrEmpty(payloadJson))
                    {
                        var job = JsonSerializer.Deserialize<PrintJob>(payloadJson);
                        if (job != null)
                        {
                            job.RedisMessageId = message.Id.ToString();
                            _logger.LogInformation("Received job {JobId} from Redis.", job.RedisMessageId);
                            
                            await _printJobProcessor.ProcessJobAsync(job, cancellationToken);
                            
                            // Acknowledge the message
                            await db.StreamAcknowledgeAsync(streamName, groupName, message.Id);
                            _logger.LogInformation("Job {JobId} acknowledged.", job.RedisMessageId);
                        }
                    }
                }
                else
                {
                    // No new messages, wait a bit before polling again
                    await Task.Delay(1000, cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while consuming from Redis stream.");
                await Task.Delay(5000, cancellationToken); // Backoff on error
            }
        }
    }
}
