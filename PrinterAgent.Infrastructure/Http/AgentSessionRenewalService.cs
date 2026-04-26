using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;

namespace PrinterAgent.Infrastructure.Http;

public sealed class AgentSessionRenewalService : IAgentSessionRenewalService
{
    private const int MaxRefreshAttempts = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentSessionStore _sessionStore;
    private readonly ILogger<AgentSessionRenewalService> _logger;

    public AgentSessionRenewalService(
        IHttpClientFactory httpClientFactory,
        IAgentSessionStore sessionStore,
        ILogger<AgentSessionRenewalService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public async Task<bool> TryRenewIfAccessExpiredAsync(
        TimeSpan expirySkew,
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        if (!force && _sessionStore.HasUsableSession(expirySkew))
            return true;

        if (string.IsNullOrWhiteSpace(_sessionStore.RefreshToken) || string.IsNullOrWhiteSpace(_sessionStore.AgentId))
            return false;

        var instanceId = _sessionStore.GetOrCreateClientInstanceId(cancellationToken);
        var client = _httpClientFactory.CreateClient("PrinterAgentEnroll");

        for (var attempt = 1; attempt <= MaxRefreshAttempts; attempt++)
        {
            try
            {
                using var response = await client.PostAsJsonAsync(
                        "api/agents/refresh",
                        new RefreshRequestBody
                        {
                            AgentId = _sessionStore.AgentId,
                            ClientInstanceId = instanceId,
                            RefreshToken = _sessionStore.RefreshToken
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                var code = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadFromJsonAsync<RefreshResponseBody>(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (payload is null
                        || string.IsNullOrWhiteSpace(payload.AccessToken)
                        || string.IsNullOrWhiteSpace(payload.RefreshToken)
                        || string.IsNullOrWhiteSpace(payload.RestaurantId))
                    {
                        _logger.LogWarning("Invalid refresh response.");
                        return false;
                    }

                    await _sessionStore.SaveSessionAsync(
                            _sessionStore.AgentId,
                            payload.AccessToken,
                            payload.RefreshToken,
                            payload.RestaurantId,
                            payload.ExpiresAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);

                    _logger.LogInformation("Access token renewed for agentId {AgentId}.", _sessionStore.AgentId);
                    return true;
                }

                if (code == 401 || code == 403)
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning("Refresh rejected ({Status}): {Body} — refresh token is no longer valid.", code, err);
                    return false;
                }

                if (code == 429)
                {
                    _logger.LogWarning("Refresh rejected (429); try again later.");
                    return false;
                }

                if (IsTransientHttpStatus(code) && attempt < MaxRefreshAttempts)
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning(
                        "Refresh HTTP {Status} (attempt {Attempt}/{Max}); retrying after delay. {Body}",
                        code,
                        attempt,
                        MaxRefreshAttempts,
                        err);
                    await DelayBeforeRefreshRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Refresh failed ({Status}): {Body}", code, body);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt >= MaxRefreshAttempts)
                {
                    _logger.LogWarning(ex, "Refresh: network/timeout failure after {Max} attempts.", MaxRefreshAttempts);
                    return false;
                }

                _logger.LogWarning(ex, "Refresh: transient error (attempt {Attempt}/{Max}).", attempt, MaxRefreshAttempts);
                await DelayBeforeRefreshRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static bool IsTransientHttpStatus(int code) =>
        code is 408 or 425 or 500 or 502 or 503 or 504;

    private static Task DelayBeforeRefreshRetryAsync(int attemptCompleted, CancellationToken cancellationToken)
    {
        // 1s, 2s după prima, respectiv a doua încercare eșuată
        var seconds = Math.Clamp(attemptCompleted, 1, 4);
        return Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    private sealed class RefreshRequestBody
    {
        public string AgentId { get; set; } = string.Empty;
        public Guid ClientInstanceId { get; set; }
        public string RefreshToken { get; set; } = string.Empty;
    }

    private sealed class RefreshResponseBody
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("restaurantId")]
        public string RestaurantId { get; set; } = string.Empty;

        [JsonPropertyName("expiresAtUtc")]
        public DateTime ExpiresAtUtc { get; set; }
    }
}
