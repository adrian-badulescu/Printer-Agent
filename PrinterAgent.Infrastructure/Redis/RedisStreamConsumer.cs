using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.UseCases;
using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Observability;
using StackExchange.Redis;

namespace PrinterAgent.Infrastructure.Redis;

public class RedisStreamConsumer : IRedisStreamConsumer
{
    private static readonly JsonSerializerOptions JobJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IRedisConnectionMultiplexerHolder _redisHolder;
    private readonly IPrintJobProcessor _printJobProcessor;
    private readonly IBackendClient _backendClient;
    private readonly ILogger<RedisStreamConsumer> _logger;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAppConfiguration _appConfiguration;

    public RedisStreamConsumer(
        IRedisConnectionMultiplexerHolder redisHolder,
        IPrintJobProcessor printJobProcessor,
        IBackendClient backendClient,
        ILogger<RedisStreamConsumer> logger,
        IAgentSessionStore sessionStore,
        IAppConfiguration appConfiguration)
    {
        _redisHolder = redisHolder;
        _printJobProcessor = printJobProcessor;
        _backendClient = backendClient;
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

        var redis = _redisHolder.Get();
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

        // #region agent log
        DebugSessionLog.Write("D", "RedisStreamConsumer.cs:StartConsumingAsync", "consumer init", new
        {
            streamName,
            groupName,
            consumerName,
            conn = _appConfiguration.RedisConnectionSummary,
        });
        // #endregion

        try
        {
            await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
            // #region agent log
            DebugSessionLog.Write("D", "RedisStreamConsumer.cs:StartConsumingAsync", "XGROUP create ok", new { streamName, groupName });
            // #endregion
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // #region agent log
            DebugSessionLog.Write("D", "RedisStreamConsumer.cs:StartConsumingAsync", "XGROUP busygroup exists", new { streamName, groupName });
            // #endregion
        }
        catch (Exception ex)
        {
            // #region agent log
            DebugSessionLog.Write("D", "RedisStreamConsumer.cs:StartConsumingAsync", "XGROUP failed", new
            {
                streamName,
                groupName,
                exType = ex.GetType().Name,
                exMessage = ex.Message,
            });
            // #endregion
            throw;
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
        var drainRound = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            drainRound++;
            var pending = await db.StreamReadGroupAsync(
                streamName,
                groupName,
                consumerName,
                "0",
                count: 10).ConfigureAwait(false);

            if (pending.Length == 0)
                break;

            if (drainRound > 50)
            {
                _logger.LogError(
                    "Pending drain exceeded 50 rounds on stream {Stream} for consumer {Consumer}; stopping drain to avoid blocking new jobs.",
                    streamName,
                    consumerName);
                break;
            }

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
        var messageId = message.Id.ToString();
        try
        {
            var payloadJson = message.Values.FirstOrDefault(v => v.Name == "payload").Value.ToString();
            if (string.IsNullOrEmpty(payloadJson))
            {
                _logger.LogWarning("Stream message {MessageId} has empty payload; acknowledging to unblock consumer.", messageId);
                await db.StreamAcknowledgeAsync(streamName, groupName, message.Id).ConfigureAwait(false);
                return;
            }

            PrintJob? job;
            try
            {
                job = JsonSerializer.Deserialize<PrintJob>(payloadJson, JobJsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize print job {MessageId}.", messageId);
                job = null;
            }

            if (job == null)
            {
                _logger.LogWarning("Stream message {MessageId} could not be parsed as PrintJob; marking Failed and acknowledging.", messageId);
                await TryMarkJobFailedAsync(messageId, cancellationToken).ConfigureAwait(false);
                await db.StreamAcknowledgeAsync(streamName, groupName, message.Id).ConfigureAwait(false);
                return;
            }

            job.RedisMessageId = messageId;
            _logger.LogInformation("Received job {JobId} from Redis.", job.RedisMessageId);

            await _printJobProcessor.ProcessJobAsync(job, cancellationToken).ConfigureAwait(false);
            await db.StreamAcknowledgeAsync(streamName, groupName, message.Id).ConfigureAwait(false);
            _logger.LogInformation("Job {JobId} acknowledged.", job.RedisMessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing stream message {MessageId}; acknowledging to avoid PEL stall.", messageId);
            try
            {
                await db.StreamAcknowledgeAsync(streamName, groupName, message.Id).ConfigureAwait(false);
            }
            catch (Exception ackEx)
            {
                _logger.LogError(ackEx, "Failed to acknowledge message {MessageId} after error.", messageId);
            }
        }
    }

    private async Task TryMarkJobFailedAsync(string jobId, CancellationToken cancellationToken)
    {
        try
        {
            await _backendClient.UpdateJobStatusAsync(jobId, PrintJobStatus.Failed, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mark job {JobId} as Failed after parse error.", jobId);
        }
    }
}
