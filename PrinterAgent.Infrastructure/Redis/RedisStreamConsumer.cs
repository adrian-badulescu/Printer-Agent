using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.UseCases;
using PrinterAgent.Domain;
using StackExchange.Redis;

namespace PrinterAgent.Infrastructure.Redis;

public class RedisStreamConsumer : IRedisStreamConsumer
{
    private readonly Lazy<IConnectionMultiplexer> _redisLazy;
    private readonly IPrintJobProcessor _printJobProcessor;
    private readonly ILogger<RedisStreamConsumer> _logger;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAppConfiguration _appConfiguration;

    public RedisStreamConsumer(
        Lazy<IConnectionMultiplexer> redisLazy,
        IPrintJobProcessor printJobProcessor,
        ILogger<RedisStreamConsumer> logger,
        IAgentSessionStore sessionStore,
        IAppConfiguration appConfiguration)
    {
        _redisLazy = redisLazy;
        _printJobProcessor = printJobProcessor;
        _logger = logger;
        _sessionStore = sessionStore;
        _appConfiguration = appConfiguration;
    }

    public async Task StartConsumingAsync(string restaurantId, CancellationToken cancellationToken = default)
    {
        var agentId = _sessionStore.AgentId;
        if (string.IsNullOrWhiteSpace(agentId))
        {
            _logger.LogError("Redis consumer: AgentId is missing in session.");
            return;
        }

        var redis = _redisLazy.Value;
        var db = redis.GetDatabase();
        var prefix = _appConfiguration.RedisStreamKeyPrefix.Trim().TrimEnd('.');
        var streamName = $"{prefix}.{restaurantId}";
        var groupName = _appConfiguration.RedisConsumerGroup.Trim();
        var consumerName = agentId;

        _logger.LogInformation(
            "Redis Streams: stream={Stream} group={Group} consumer={Consumer} (connection: {Conn})",
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

        await DrainPendingMessagesAsync(db, streamName, groupName, consumerName, cancellationToken).ConfigureAwait(false);

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
                    await ProcessStreamMessageAsync(db, streamName, groupName, messages[0], cancellationToken).ConfigureAwait(false);
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

    private async Task DrainPendingMessagesAsync(
        IDatabase db,
        string streamName,
        string groupName,
        string consumerName,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pending = await db.StreamReadGroupAsync(
                streamName,
                groupName,
                consumerName,
                "0",
                count: 10).ConfigureAwait(false);

            if (pending.Length == 0)
                break;

            foreach (var message in pending)
            {
                await ProcessStreamMessageAsync(db, streamName, groupName, message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessStreamMessageAsync(
        IDatabase db,
        string streamName,
        string groupName,
        StreamEntry message,
        CancellationToken cancellationToken)
    {
        var payloadJson = message.Values.FirstOrDefault(v => v.Name == "payload").Value.ToString();
        if (string.IsNullOrEmpty(payloadJson))
            return;

        var job = JsonSerializer.Deserialize<PrintJob>(payloadJson);
        if (job == null)
            return;

        job.RedisMessageId = message.Id.ToString();
        _logger.LogInformation("Received job {JobId} from Redis.", job.RedisMessageId);

        await _printJobProcessor.ProcessJobAsync(job, cancellationToken).ConfigureAwait(false);
        await db.StreamAcknowledgeAsync(streamName, groupName, message.Id).ConfigureAwait(false);
        _logger.LogInformation("Job {JobId} acknowledged.", job.RedisMessageId);
    }
}
