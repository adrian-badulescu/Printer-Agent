using System.Net.Http.Headers;
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

        _httpClient.BaseAddress = new Uri(_appConfiguration.BackendUrl);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _appConfiguration.BackendJwtToken);
    }

    public async Task<AgentUpdateResponse?> CheckForUpdatesAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/api/agents/{agentId}/update", cancellationToken);
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

    public async Task SendHeartbeatAsync(AgentInfo agentInfo, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/agents/heartbeat", agentInfo, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateJobStatusAsync(string jobId, PrintJobStatus status, CancellationToken cancellationToken = default)
    {
        var request = new { Status = status.ToString() };
        // Using POST as proposed in the implementation plan
        var response = await _httpClient.PostAsJsonAsync($"/api/print-jobs/{jobId}/status", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
