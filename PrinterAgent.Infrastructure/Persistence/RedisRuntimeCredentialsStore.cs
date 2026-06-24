using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Storage;
using PrinterAgent.Infrastructure.Security;

namespace PrinterAgent.Infrastructure.Persistence;

public sealed class RedisRuntimeCredentialsStore : IRedisRuntimeCredentials
{
    private const string FileName = "redis.credentials.json";
    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly ILogger<RedisRuntimeCredentialsStore> _logger;
    private readonly string _path;
    private RedisRuntimeCredentialsPayload? _cached;

    public RedisRuntimeCredentialsStore(ILogger<RedisRuntimeCredentialsStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(AgentProgramData.Root, FileName);
    }

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(_cached?.Host)
        && !string.IsNullOrWhiteSpace(_cached?.User)
        && !string.IsNullOrWhiteSpace(_cached?.Password);

    public string? Host => _cached?.Host;
    public int Port => _cached?.Port ?? 6379;
    public string? User => _cached?.User;
    public string? Password => _cached?.Password;
    public string? StreamKeyPrefix => _cached?.StreamKeyPrefix;
    public string? ConsumerGroup => _cached?.ConsumerGroup;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _cached = null;
        if (!File.Exists(_path))
            return;

        try
        {
            await using var fs = File.OpenRead(_path);
            var fileDto = await JsonSerializer.DeserializeAsync<RedisCredentialsFileDto>(fs, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            if (fileDto == null || string.IsNullOrWhiteSpace(fileDto.Host) || string.IsNullOrWhiteSpace(fileDto.User))
                return;

            var password = ResolvePassword(fileDto);
            if (string.IsNullOrWhiteSpace(password))
                return;

            _cached = new RedisRuntimeCredentialsPayload
            {
                Host = fileDto.Host.Trim(),
                Port = fileDto.Port > 0 ? fileDto.Port : 6379,
                User = fileDto.User.Trim(),
                Password = password,
                StreamKeyPrefix = string.IsNullOrWhiteSpace(fileDto.StreamKeyPrefix) ? "print.jobs" : fileDto.StreamKeyPrefix.Trim(),
                ConsumerGroup = string.IsNullOrWhiteSpace(fileDto.ConsumerGroup) ? "printer-agents" : fileDto.ConsumerGroup.Trim()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot read {File}; Redis runtime credentials ignored.", FileName);
            _cached = null;
        }
    }

    public async Task SaveAsync(RedisRuntimeCredentialsPayload payload, CancellationToken cancellationToken = default)
    {
        await SaveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            RedisCredentialsFileDto fileDto;
            if (RedisCredentialProtector.IsSupported)
            {
                fileDto = new RedisCredentialsFileDto
                {
                    Host = payload.Host,
                    Port = payload.Port,
                    User = payload.User,
                    PasswordProtected = RedisCredentialProtector.ProtectToBase64(payload.Password),
                    StreamKeyPrefix = payload.StreamKeyPrefix,
                    ConsumerGroup = payload.ConsumerGroup
                };
            }
            else
            {
                _logger.LogWarning("DPAPI unavailable; storing Redis password in plaintext in {File}.", FileName);
                fileDto = new RedisCredentialsFileDto
                {
                    Host = payload.Host,
                    Port = payload.Port,
                    User = payload.User,
                    Password = payload.Password,
                    StreamKeyPrefix = payload.StreamKeyPrefix,
                    ConsumerGroup = payload.ConsumerGroup
                };
            }

            var json = JsonSerializer.Serialize(fileDto, SerializerOptions);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);

            _cached = payload;
            _logger.LogInformation(
                "Saved Redis runtime credentials for user {RedisUser} at {Host}:{Port}.",
                payload.User,
                payload.Host,
                payload.Port);
        }
        finally
        {
            SaveLock.Release();
        }
    }

    private static string? ResolvePassword(RedisCredentialsFileDto fileDto)
    {
        if (!string.IsNullOrWhiteSpace(fileDto.Password))
            return fileDto.Password;

        if (string.IsNullOrWhiteSpace(fileDto.PasswordProtected) || !RedisCredentialProtector.IsSupported)
            return null;

        try
        {
            return RedisCredentialProtector.UnprotectFromBase64(fileDto.PasswordProtected);
        }
        catch
        {
            return null;
        }
    }

    private sealed class RedisCredentialsFileDto
    {
        public string? Host { get; set; }
        public int Port { get; set; }
        public string? User { get; set; }
        public string? Password { get; set; }
        public string? PasswordProtected { get; set; }
        public string? StreamKeyPrefix { get; set; }
        public string? ConsumerGroup { get; set; }
    }
}
