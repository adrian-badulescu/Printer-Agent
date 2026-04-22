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

    public async Task<Stream> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task<bool> SendHeartbeatAsync(AgentInfo agentInfo, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/agents/heartbeat", agentInfo, cancellationToken);
        // Auth rejected: do not throw — HeartbeatService clears session and operator can re-enroll.
        var code = (int)response.StatusCode;
        if (code == 401 || code == 403)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task UpdateJobStatusAsync(string jobId, PrintJobStatus status, CancellationToken cancellationToken = default)
    {
        var request = new { Status = status.ToString() };
        // Using POST as proposed in the implementation plan
        var response = await _httpClient.PostAsJsonAsync($"api/print-jobs/{Uri.EscapeDataString(jobId)}/status", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
