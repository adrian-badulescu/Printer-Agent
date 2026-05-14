using System.Net.Http.Json;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Http;

public class BackendClient : IBackendClient
{
    private readonly HttpClient _httpClient;
    private readonly IAppConfiguration _appConfiguration;

    public BackendClient(HttpClient httpClient, IAppConfiguration appConfiguration)
    {
        _httpClient = httpClient;
        _appConfiguration = appConfiguration;

        var baseUrl = _appConfiguration.BackendUrl?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException("BackendUrl is required.");
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";
        _httpClient.BaseAddress = new Uri(baseUrl);
    }

    public async Task<AgentUpdateResponse?> CheckForUpdatesAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"api/agents/{Uri.EscapeDataString(agentId)}/update", cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<AgentUpdateResponse>(cancellationToken: cancellationToken);
        }
        return null;
    }

    public async Task<string?> GetWireGuardConfAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var url = $"api/agents/{Uri.EscapeDataString(agentId)}/wireguard-conf";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        var code = (int)response.StatusCode;
        if (code == 401 || code == 403 || code == 404)
            return null;
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<Stream> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task<bool> SendHeartbeatAsync(AgentInfo agentInfo, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/agents/heartbeat", agentInfo, cancellationToken);
        var code = (int)response.StatusCode;
        // Treat any auth/validation-style rejection as "not ok", so HeartbeatService can clear session
        // and attempt a recovery path (re-enroll).
        if (code is 400 or 401 or 403)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<AgentEnrollResponse?> EnrollAsync(
        string enrollmentCode,
        Guid clientInstanceId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/agents/enroll",
            new { EnrollmentCode = enrollmentCode, ClientInstanceId = clientInstanceId },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            return null;

        // Backend uses camelCase; our DTO properties are PascalCase but System.Text.Json is case-insensitive by default.
        return await response.Content.ReadFromJsonAsync<AgentEnrollResponse>(cancellationToken: cancellationToken);
    }

    public async Task UpdateJobStatusAsync(string jobId, PrintJobStatus status, CancellationToken cancellationToken = default)
    {
        var request = new { Status = status.ToString() };
        const int maxAttempts = 4;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"api/print-jobs/{Uri.EscapeDataString(jobId)}/status",
                request,
                cancellationToken);

            if (response.IsSuccessStatusCode)
                return;

            if ((int)response.StatusCode != 400 || attempt == maxAttempts)
                response.EnsureSuccessStatusCode();

            await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
        }
    }
}
