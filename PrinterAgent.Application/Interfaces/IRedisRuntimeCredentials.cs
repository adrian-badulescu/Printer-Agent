namespace PrinterAgent.Application.Interfaces;

/// <summary>Restaurant-scoped Redis ACL credentials fetched after enroll (DPAPI-protected on disk).</summary>
public interface IRedisRuntimeCredentials
{
    bool HasCredentials { get; }

    string? Host { get; }
    int Port { get; }
    string? User { get; }
    string? Password { get; }
    string? StreamKeyPrefix { get; }
    string? ConsumerGroup { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(RedisRuntimeCredentialsPayload payload, CancellationToken cancellationToken = default);
}

public sealed class RedisRuntimeCredentialsPayload
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 6379;
    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string StreamKeyPrefix { get; set; } = "print.jobs";
    public string ConsumerGroup { get; set; } = "printer-agents";
}
