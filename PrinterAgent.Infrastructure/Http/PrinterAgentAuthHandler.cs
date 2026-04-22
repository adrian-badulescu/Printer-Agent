using System.Net.Http.Headers;
using PrinterAgent.Application.Interfaces;

namespace PrinterAgent.Infrastructure.Http;

/// <summary>
/// Adaugă Bearer din sesiune; fallback la <see cref="IAppConfiguration.BackendJwtToken"/> (ex. dev).
/// </summary>
public sealed class PrinterAgentAuthHandler : DelegatingHandler
{
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAppConfiguration _appConfiguration;

    public PrinterAgentAuthHandler(IAgentSessionStore sessionStore, IAppConfiguration appConfiguration)
    {
        _sessionStore = sessionStore;
        _appConfiguration = appConfiguration;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _sessionStore.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            token = _appConfiguration.BackendJwtToken;

        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return base.SendAsync(request, cancellationToken);
    }
}
