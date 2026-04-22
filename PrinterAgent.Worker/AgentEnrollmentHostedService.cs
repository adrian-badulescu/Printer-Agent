using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;

namespace PrinterAgent.Worker;

/// <summary>
/// Înainte de <see cref="AgentWorker"/>: încarcă sau obține sesiune (enroll cu cod dacă e nevoie).
/// </summary>
public sealed class AgentEnrollmentHostedService : IHostedService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAgentSessionRenewalService _sessionRenewal;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<AgentEnrollmentHostedService> _logger;

    public AgentEnrollmentHostedService(
        IHttpClientFactory httpClientFactory,
        IAgentSessionStore sessionStore,
        IAgentSessionRenewalService sessionRenewal,
        IAppConfiguration appConfiguration,
        ILogger<AgentEnrollmentHostedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sessionStore = sessionStore;
        _sessionRenewal = sessionRenewal;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (_sessionStore.HasUsableSession(TimeSpan.FromMinutes(5)))
        {
            _logger.LogInformation("Sesiune agent validă; enroll omis.");
            return;
        }

        if (await _sessionRenewal.TryRenewIfAccessExpiredAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false)
            && _sessionStore.HasUsableSession(TimeSpan.FromMinutes(5)))
        {
            _logger.LogInformation("Sesiune reînnoită prin refresh; enroll omis.");
            return;
        }

        var code = _appConfiguration.EnrollmentCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            if (CanContinueWithRefreshOnly())
            {
                _logger.LogWarning(
                    "EnrollmentCode lipsește în agent.json, dar există AgentId + refresh în sesiune — pornire continuă; Heartbeat va reîncerca refresh.");
                return;
            }

            // Nu aruncăm: altfel procesul de serviciu se închide imediat după MSI (înainte de Configurator),
            // iar în SCM apare «oprit». După ce salvezi codul, repornește serviciul ca să ruleze enroll.
            _logger.LogWarning(
                "EnrollmentCode lipsește în agent.json — serviciul rămâne pornit fără enroll. Salvează codul (Configurator), apoi repornește serviciul URSPrinterAgent.");
            return;
        }

        var instanceId = _sessionStore.GetOrCreateClientInstanceId(cancellationToken);
        var client = _httpClientFactory.CreateClient("PrinterAgentEnroll");

        using var response = await client.PostAsJsonAsync(
                "api/agents/enroll",
                new EnrollRequestBody { EnrollmentCode = code, ClientInstanceId = instanceId },
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            if (CanContinueWithRefreshOnly())
            {
                _logger.LogWarning("Enroll respins (429); există refresh în sesiune — pornire continuă.");
                return;
            }

            throw new InvalidOperationException("Prea multe încercări de enroll de pe această rețea; încearcă mai târziu.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (CanContinueWithRefreshOnly())
            {
                _logger.LogWarning(
                    "Enroll esuat ({Status}): {Body}. Codul poate fi consumat sau invalid — există refresh; pornire continuă (Heartbeat reincearcă refresh).",
                    (int)response.StatusCode,
                    err);
                return;
            }

            throw new InvalidOperationException($"Enroll esuat ({(int)response.StatusCode}): {err}");
        }

        var payload = await response.Content.ReadFromJsonAsync<EnrollResponseBody>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (payload is null
            || string.IsNullOrWhiteSpace(payload.AccessToken)
            || string.IsNullOrWhiteSpace(payload.AgentId)
            || string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            if (CanContinueWithRefreshOnly())
            {
                _logger.LogWarning("Raspuns enroll invalid, dar exista refresh in sesiune — pornire continua.");
                return;
            }

            throw new InvalidOperationException("Răspuns enroll invalid.");
        }

        await _sessionStore.SaveSessionAsync(
                payload.AgentId,
                payload.AccessToken,
                payload.RefreshToken,
                payload.RestaurantId,
                payload.ExpiresAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Enroll reușit pentru agentId {AgentId}.", payload.AgentId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Dacă enroll nu mai poate reuși (ex. cod consumat), dar avem încă refresh, lăsăm hostul pornit:
    /// Heartbeat reaplează refresh la fiecare ciclu.
    /// </summary>
    private bool CanContinueWithRefreshOnly() =>
        !string.IsNullOrWhiteSpace(_sessionStore.AgentId)
        && !string.IsNullOrWhiteSpace(_sessionStore.RefreshToken);

    private sealed class EnrollRequestBody
    {
        public string EnrollmentCode { get; set; } = string.Empty;
        public Guid ClientInstanceId { get; set; }
    }

    private sealed class EnrollResponseBody
    {
        [JsonPropertyName("agentId")]
        public string AgentId { get; set; } = string.Empty;

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
