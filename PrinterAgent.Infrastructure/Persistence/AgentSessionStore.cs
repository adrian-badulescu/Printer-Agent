using System.Linq;
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

    private static readonly SemaphoreSlim SessionSaveLock = new(1, 1);
    private const int SessionSaveMaxAttempts = 6;
    private static readonly TimeSpan SessionSaveRetryDelay = TimeSpan.FromMilliseconds(60);

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
            var recovered = FindLatestSessionTempFile();
            if (recovered == null)
            {
                _session = null;
                return;
            }

            _logger.LogWarning("Main session file missing; loading from recovered temp file {Path}.", recovered);
            path = recovered;
        }

        if (!await TryLoadSessionFromPathAsync(path, cancellationToken).ConfigureAwait(false)
            && path.EndsWith(SessionFileName, StringComparison.OrdinalIgnoreCase))
        {
            var recovered = FindLatestSessionTempFile();
            if (recovered != null && !string.Equals(recovered, path, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Retrying session load from temp file {Path}.", recovered);
                await TryLoadSessionFromPathAsync(recovered, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryLoadSessionFromPathAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var fileDto = await JsonSerializer.DeserializeAsync<AgentSessionFileDto>(fs, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            if (fileDto == null)
            {
                _session = null;
                return false;
            }

            var token = ResolveAccessToken(fileDto);
            var refresh = ResolveRefreshToken(fileDto);
            if (string.IsNullOrWhiteSpace(fileDto.AgentId))
            {
                _session = null;
                return false;
            }

            if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(refresh))
            {
                _session = null;
                return false;
            }

            _session = new AgentSessionDto
            {
                AgentId = fileDto.AgentId,
                AccessToken = token ?? string.Empty,
                RefreshToken = refresh ?? string.Empty,
                RestaurantId = fileDto.RestaurantId ?? string.Empty,
                ExpiresAtUtc = DateTime.SpecifyKind(fileDto.ExpiresAtUtc, DateTimeKind.Utc)
            };
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot read {File}; session ignored.", path);
            _session = null;
            return false;
        }
    }

    private string? FindLatestSessionTempFile()
    {
        try
        {
            if (!Directory.Exists(_baseDir))
                return null;

            return Directory.EnumerateFiles(_baseDir, SessionFileName + ".tmp.*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
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
        Directory.CreateDirectory(_baseDir);

        for (var attempt = 1; attempt <= SessionSaveMaxAttempts; attempt++)
        {
            var tempPath = Path.Combine(_baseDir, SessionFileName + ".tmp." + Guid.NewGuid().ToString("N"));
            await SessionSaveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var fs = new FileStream(
                                   tempPath,
                                   FileMode.Create,
                                   FileAccess.Write,
                                   FileShare.Read,
                                   bufferSize: 4096,
                                   options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await JsonSerializer.SerializeAsync(fs, fileDto, SerializerOptions, cancellationToken).ConfigureAwait(false);
                }

                // File.Replace often throws "Unable to remove the file to be replaced" when AV/indexers lock the destination.
                // Move(..., overwrite: true) uses ReplaceFile less aggressively on typical installs.
                File.Move(tempPath, path, overwrite: true);

                TryDeleteQuiet(tempPath);
                break;
            }
            catch (IOException ex)
            {
                TryDeleteQuiet(tempPath);
                if (attempt >= SessionSaveMaxAttempts)
                {
                    _logger.LogError(
                        ex,
                        "Session save failed after {Max} attempts (agentId={AgentId}).",
                        SessionSaveMaxAttempts,
                        agentId);
                    throw;
                }

                _logger.LogWarning(
                    ex,
                    "Session save IO error (attempt {Attempt}/{Max}); retrying.",
                    attempt,
                    SessionSaveMaxAttempts);
            }
            catch (UnauthorizedAccessException ex)
            {
                TryDeleteQuiet(tempPath);
                if (attempt >= SessionSaveMaxAttempts)
                {
                    _logger.LogError(
                        ex,
                        "Session save access denied after {Max} attempts (agentId={AgentId}).",
                        SessionSaveMaxAttempts,
                        agentId);
                    throw;
                }

                _logger.LogWarning(
                    ex,
                    "Session save access denied (attempt {Attempt}/{Max}); retrying.",
                    attempt,
                    SessionSaveMaxAttempts);
            }
            finally
            {
                SessionSaveLock.Release();
            }

            if (attempt < SessionSaveMaxAttempts)
                await Task.Delay(SessionSaveRetryDelay, cancellationToken).ConfigureAwait(false);
        }

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
            _logger.LogWarning("Session cleared ({File}). Recovery: device credential renew, or set EnrollmentCode in agent.json.", SessionFileName);
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

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
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
