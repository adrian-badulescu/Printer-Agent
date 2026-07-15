using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.UseCases;
using PrinterAgent.Domain;

namespace PrinterAgent.Worker;

public sealed class LocalPrintApiHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILocalPrintJobHandler _printHandler;
    private readonly ILocalPrintAuthTokenProvider _authTokenProvider;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<LocalPrintApiHostedService> _logger;
    private HttpListener? _listener;

    public LocalPrintApiHostedService(
        ILocalPrintJobHandler printHandler,
        ILocalPrintAuthTokenProvider authTokenProvider,
        IAppConfiguration appConfiguration,
        ILogger<LocalPrintApiHostedService> logger)
    {
        _printHandler = printHandler;
        _authTokenProvider = authTokenProvider;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_appConfiguration.LocalPrintEnabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunListenerAsync(_appConfiguration.LocalPrintPort, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local print API listener failed; retrying in 10s.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener?.Close();
        }
        catch
        {
            // ignore
        }

        return base.StopAsync(cancellationToken);
    }

    private async Task RunListenerAsync(int port, CancellationToken stoppingToken)
    {
        var safePort = port is > 0 and <= 65535 ? port : 9247;
        var prefix = $"http://+:{safePort}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _logger.LogInformation("Local print API listening on {Prefix}", prefix);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(ctx, stoppingToken), CancellationToken.None);
            }
        }
        finally
        {
            _listener.Close();
            _listener = null;
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            AddCorsHeaders(ctx.Response);

            if (string.Equals(ctx.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
                ctx.Response.Close();
                return;
            }

            if (!string.Equals(ctx.Request.Url?.AbsolutePath, "/local/print-jobs", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(ctx.Response, HttpStatusCode.NotFound, new { message = "Not found." }).ConfigureAwait(false);
                return;
            }

            if (!await ValidateAuthAsync(ctx.Request, cancellationToken).ConfigureAwait(false))
            {
                await WriteJsonAsync(ctx.Response, HttpStatusCode.Unauthorized, new { message = "Unauthorized." }).ConfigureAwait(false);
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var job = JsonSerializer.Deserialize<PrintJob>(body, JsonOptions);
            if (job == null
                || string.IsNullOrWhiteSpace(job.RestaurantId)
                || string.IsNullOrWhiteSpace(job.PrinterId))
            {
                await WriteJsonAsync(ctx.Response, HttpStatusCode.BadRequest, new { message = "Invalid payload." }).ConfigureAwait(false);
                return;
            }

            var result = await _printHandler.PrintAsync(job, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                await WriteJsonAsync(ctx.Response, HttpStatusCode.OK, new
                {
                    success = true,
                    fiscalReceiptNumber = result.FiscalReceiptNumber,
                }).ConfigureAwait(false);
            }
            else
            {
                await WriteJsonAsync(ctx.Response, HttpStatusCode.InternalServerError, new
                {
                    success = false,
                    errorCode = result.ErrorCode,
                    deviceErrorCode = result.DeviceErrorCode,
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local print API request failed.");
            try
            {
                await WriteJsonAsync(ctx.Response, HttpStatusCode.InternalServerError, new { message = "Internal error." }).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task<bool> ValidateAuthAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        var expected = await _authTokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        var auth = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        var presented = auth["Bearer ".Length..].Trim();
        return string.Equals(presented, expected, StringComparison.Ordinal);
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode status, object payload)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }
}
