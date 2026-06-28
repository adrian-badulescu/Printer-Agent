using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Infrastructure.Security;

namespace PrinterAgent.Infrastructure.Http;

public sealed class AgentDeviceRenewalService : IAgentDeviceRenewalService
{
    private const int MaxRenewAttempts = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IDeviceCredentialStore _deviceCredentialStore;
    private readonly ILogger<AgentDeviceRenewalService> _logger;

    public AgentDeviceRenewalService(
        IHttpClientFactory httpClientFactory,
        IAgentSessionStore sessionStore,
        IDeviceCredentialStore deviceCredentialStore,
        ILogger<AgentDeviceRenewalService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sessionStore = sessionStore;
        _deviceCredentialStore = deviceCredentialStore;
        _logger = logger;
    }

    public async Task<bool> TryRenewWithDeviceCredentialAsync(CancellationToken cancellationToken = default)
    {
        await AgentAuthCoordination.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _deviceCredentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!_deviceCredentialStore.HasCredential)
                return false;

            var agentId = _deviceCredentialStore.AgentId!;
            var credential = _deviceCredentialStore.DeviceCredential!;
            var instanceId = _sessionStore.GetOrCreateClientInstanceId(cancellationToken);
            if (!string.Equals(agentId, instanceId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Device credential agentId {CredentialAgentId} does not match clientInstanceId {InstanceId}; renew skipped.",
                    agentId,
                    instanceId);
                return false;
            }

            var client = _httpClientFactory.CreateClient("PrinterAgentEnroll");
            var timestampUtc = DateTime.UtcNow;
            var signature = PrinterAgentRenewSignature.Compute(credential, agentId, instanceId, timestampUtc);

            for (var attempt = 1; attempt <= MaxRenewAttempts; attempt++)
            {
                try
                {
                    using var response = await client.PostAsJsonAsync(
                            "api/agents/renew",
                            new RenewRequestBody
                            {
                                AgentId = agentId,
                                ClientInstanceId = instanceId,
                                TimestampUtc = timestampUtc,
                                DeviceCredential = credential,
                                Signature = signature
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    var code = (int)response.StatusCode;

                    if (response.IsSuccessStatusCode)
                    {
                        var payload = await response.Content.ReadFromJsonAsync<RenewResponseBody>(cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        if (payload is null
                            || string.IsNullOrWhiteSpace(payload.AccessToken)
                            || string.IsNullOrWhiteSpace(payload.RefreshToken)
                            || string.IsNullOrWhiteSpace(payload.RestaurantId))
                        {
                            _logger.LogWarning("Invalid renew response.");
                            return false;
                        }

                        await _sessionStore.SaveSessionAsync(
                                agentId,
                                payload.AccessToken,
                                payload.RefreshToken,
                                payload.RestaurantId,
                                payload.ExpiresAtUtc,
                                cancellationToken)
                            .ConfigureAwait(false);

                        _logger.LogInformation("Session recovered via device credential renew for agentId {AgentId}.", agentId);
                        return true;
                    }

                    if (code is 401 or 403)
                    {
                        var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning("Device renew rejected ({Status}): {Body}", code, err);
                        return false;
                    }

                    if (code == 429)
                    {
                        _logger.LogWarning("Device renew rejected (429); try again later.");
                        return false;
                    }

                    if (IsTransientHttpStatus(code) && attempt < MaxRenewAttempts)
                    {
                        var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning(
                            "Device renew HTTP {Status} (attempt {Attempt}/{Max}); retrying. {Body}",
                            code,
                            attempt,
                            MaxRenewAttempts,
                            err);
                        await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning("Device renew failed ({Status}): {Body}", code, body);
                    return false;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    if (attempt >= MaxRenewAttempts)
                    {
                        _logger.LogWarning(ex, "Device renew: network/timeout failure after {Max} attempts.", MaxRenewAttempts);
                        return false;
                    }

                    _logger.LogWarning(ex, "Device renew: transient error (attempt {Attempt}/{Max}).", attempt, MaxRenewAttempts);
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            return false;
        }
        finally
        {
            AgentAuthCoordination.Gate.Release();
        }
    }

    private static bool IsTransientHttpStatus(int code) =>
        code is 408 or 425 or 500 or 502 or 503 or 504;

    private static Task DelayBeforeRetryAsync(int attemptCompleted, CancellationToken cancellationToken)
    {
        var seconds = Math.Clamp(attemptCompleted, 1, 4);
        return Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    private sealed class RenewRequestBody
    {
        [JsonPropertyName("agentId")]
        public string AgentId { get; set; } = string.Empty;

        [JsonPropertyName("clientInstanceId")]
        public Guid ClientInstanceId { get; set; }

        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; }

        [JsonPropertyName("deviceCredential")]
        public string DeviceCredential { get; set; } = string.Empty;

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;
    }

    private sealed class RenewResponseBody
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
