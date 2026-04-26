using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Storage;
using PrinterAgent.Infrastructure.Security;

namespace PrinterAgent.Infrastructure.Persistence;

public sealed class AgentSessionStore : IAgentSessionStore
{
    private const string SessionFileName = "agent.session.json";
    private const string InstanceFileName = "client.instance";

    private readonly ILogger<AgentSessionStore> _logger;
    private readonly string _baseDir;
    private AgentSessionDto? _session;

    public AgentSessionStore(ILogger<AgentSessionStore> logger)
    {
        _logger = logger;
        _baseDir = AgentProgramData.Root;
    }

    public string? AgentId => _session?.AgentId;
    public string? AccessToken => _session?.AccessToken;
    public string? RefreshToken => _session?.RefreshToken;
    public string? SessionRestaurantId => _session?.RestaurantId;
    public DateTimeOffset? ExpiresAtUtc =>
        _session == null ? null : new DateTimeOffset(DateTime.SpecifyKind(_session.ExpiresAtUtc, DateTimeKind.Utc));

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_baseDir, SessionFileName);
        if (!File.Exists(path))
        {
            _session = null;
            return;
        }

        try
        {
            await using var fs = File.OpenRead(path);
            var fileDto = await JsonSerializer.DeserializeAsync<AgentSessionFileDto>(fs, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            if (fileDto == null)
            {
                _session = null;
                return;
            }

            var token = ResolveAccessToken(fileDto);
            var refresh = ResolveRefreshToken(fileDto);
            if (string.IsNullOrWhiteSpace(fileDto.AgentId))
            {
                _session = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(refresh))
            {
                _session = null;
                return;
            }

            _session = new AgentSessionDto
            {
                AgentId = fileDto.AgentId,
                AccessToken = token ?? string.Empty,
                RefreshToken = refresh ?? string.Empty,
                RestaurantId = fileDto.RestaurantId ?? string.Empty,
                ExpiresAtUtc = DateTime.SpecifyKind(fileDto.ExpiresAtUtc, DateTimeKind.Utc)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot read {File}; session ignored.", SessionFileName);
            _session = null;
        }
    }

    private static string? ResolveAccessToken(AgentSessionFileDto fileDto)
    {
        if (!string.IsNullOrWhiteSpace(fileDto.AccessToken))
            return fileDto.AccessToken;
        if (string.IsNullOrWhiteSpace(fileDto.AccessTokenProtected))
            return null;
        if (!SessionAccessTokenProtector.IsSupported)
            return null;
        try
        {
            return SessionAccessTokenProtector.UnprotectFromBase64(fileDto.AccessTokenProtected);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveRefreshToken(AgentSessionFileDto fileDto)
    {
        if (!string.IsNullOrWhiteSpace(fileDto.RefreshToken))
            return fileDto.RefreshToken;
        if (string.IsNullOrWhiteSpace(fileDto.RefreshTokenProtected))
            return null;
        if (!SessionAccessTokenProtector.IsSupported)
            return null;
        try
        {
            return SessionAccessTokenProtector.UnprotectFromBase64(fileDto.RefreshTokenProtected);
        }
        catch
        {
            return null;
        }
    }

    public bool HasUsableSession(TimeSpan expirySkew)
    {
        if (_session == null || string.IsNullOrWhiteSpace(_session.AccessToken) || string.IsNullOrWhiteSpace(_session.AgentId))
            return false;
        var limit = DateTime.UtcNow.Add(expirySkew);
        return _session.ExpiresAtUtc > limit;
    }

    public Guid GetOrCreateClientInstanceId(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_baseDir, InstanceFileName);
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            if (Guid.TryParse(text, out var existing))
                return existing;
        }

        var id = Guid.NewGuid();
        File.WriteAllText(path, id.ToString("D"));
        _logger.LogInformation("Created {File} with clientInstanceId {Id}.", InstanceFileName, id);
        return id;
    }

    public async Task SaveSessionAsync(
        string agentId,
        string accessToken,
        string refreshToken,
        string restaurantId,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        _session = new AgentSessionDto
        {
            AgentId = agentId,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            RestaurantId = restaurantId,
            ExpiresAtUtc = DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)
        };

        AgentSessionFileDto fileDto;
        if (SessionAccessTokenProtector.IsSupported)
        {
            fileDto = new AgentSessionFileDto
            {
                AgentId = agentId,
                RestaurantId = restaurantId,
                ExpiresAtUtc = _session.ExpiresAtUtc,
                AccessToken = null,
                AccessTokenProtected = SessionAccessTokenProtector.ProtectToBase64(accessToken),
                RefreshToken = null,
                RefreshTokenProtected = SessionAccessTokenProtector.ProtectToBase64(refreshToken)
            };
        }
        else
        {
            fileDto = new AgentSessionFileDto
            {
                AgentId = agentId,
                RestaurantId = restaurantId,
                ExpiresAtUtc = _session.ExpiresAtUtc,
                AccessToken = accessToken,
                AccessTokenProtected = null,
                RefreshToken = refreshToken,
                RefreshTokenProtected = null
            };
        }

        var path = Path.Combine(_baseDir, SessionFileName);
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, fileDto, SerializerOptions, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Session saved to {File} (agentId={AgentId}).", SessionFileName, agentId);

        AgentProgramDataAgentJsonSync.TryWriteRestaurantId(restaurantId, _logger);
    }

    public Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        _session = null;
        var path = Path.Combine(_baseDir, SessionFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogWarning("Session cleared ({File}). Re-enroll: set EnrollmentCode in agent.json and restart the service.", SessionFileName);
        }

        return Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class AgentSessionDto
    {
        public string AgentId { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string RestaurantId { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }

    private sealed class AgentSessionFileDto
    {
        public string AgentId { get; set; } = string.Empty;
        public string? RestaurantId { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string? AccessToken { get; set; }
        public string? AccessTokenProtected { get; set; }
        public string? RefreshToken { get; set; }
        public string? RefreshTokenProtected { get; set; }
    }
}
