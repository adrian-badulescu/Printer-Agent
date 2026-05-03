using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Observability;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing;

public class EscPosPrinterService : IPrinterService
{
    private readonly ILogger<EscPosPrinterService> _logger;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IPrinterMacCapture _macCapture;

    public EscPosPrinterService(
        ILogger<EscPosPrinterService> logger,
        IAppConfiguration appConfiguration,
        IPrinterMacCapture macCapture)
    {
        _logger = logger;
        _appConfiguration = appConfiguration;
        _macCapture = macCapture;
    }

    public async Task<bool> PrintAsync(Printer printer, PrintJob job, CancellationToken cancellationToken = default)
    {
        var maxAttempts = _appConfiguration.MaxPrintRetryAttempts;
        var baseDelayMs = _appConfiguration.PrintRetryBaseDelayMs;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Connecting to printer {PrinterName} at {Ip}:{Port} (attempt {Attempt}/{Max})",
                    printer.Name, printer.IpAddress, printer.Port, attempt, maxAttempts);

                using var client = new TcpClient();
                var connectTimeout = TimeSpan.FromSeconds(_appConfiguration.PrinterConnectTimeoutSeconds);
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(connectTimeout);
                try
                {
                    await client.ConnectAsync(printer.IpAddress, printer.Port, connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Connect to {printer.IpAddress}:{printer.Port} exceeded {connectTimeout.TotalSeconds}s.");
                }

                using var stream = client.GetStream();

                var escPosData = RenderReceipt(job);
                await stream.WriteAsync(escPosData, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                _logger.LogInformation("Job {JobId} printed successfully to {PrinterName}.", job.RedisMessageId, printer.Name);

                await _macCapture.TryPersistMacAfterSuccessfulPrintAsync(printer.Id, printer.IpAddress, cancellationToken)
                    .ConfigureAwait(false);

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                AgentMetrics.PrintRetries.Add(1);
                var delay = TimeSpan.FromMilliseconds(baseDelayMs * Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex, "Print attempt {Attempt} failed for job {JobId}; retry in {Delay}.", attempt, job.RedisMessageId, delay);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to print job {JobId} to printer {PrinterName} after {Attempts} attempts.", job.RedisMessageId, printer.Name, maxAttempts);
                return false;
            }
        }

        return false;
    }

    private static byte[] RenderReceipt(PrintJob job)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(new byte[] { 0x1B, 0x40 });

        var kind = (job.Payload.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (kind == "bill")
        {
            RenderBill(bw, job);
        }
        else
        {
            RenderOrder(bw, job);
        }

        return ms.ToArray();
    }

    private static void RenderOrder(BinaryWriter bw, PrintJob job)
    {
        bw.Write(new byte[] { 0x1B, 0x61, 0x01 });
        bw.Write(Encoding.ASCII.GetBytes($"*** ORDER {job.Payload.OrderId} ***\n\n"));
        bw.Write(new byte[] { 0x1B, 0x61, 0x00 });

        foreach (var item in job.Payload.Items)
        {
            var line = $"{item.Quantity}x {item.Name}".PadRight(32) + item.Price.ToString("F2").PadLeft(10) + "\n";
            bw.Write(Encoding.ASCII.GetBytes(line));
        }

        bw.Write(Encoding.ASCII.GetBytes("\n\n"));
        bw.Write(new byte[] { 0x1D, 0x56, 0x41, 0x00 });
    }

    private static void RenderBill(BinaryWriter bw, PrintJob job)
    {
        static string SafeAscii(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;
            return new string(s.Select(c => c <= 127 ? c : '?').ToArray());
        }

        static string[] UrsAsciiArt()
        {
            return
            [
                " _   _   ____    ____ ",
                "| | | | |  _ \\  / ___|",
                "| |_| | | |_) | \\___ \\",
                "|  _  | |  _ <   ___) |",
                "|_| |_| |_| \\_\\ |____/ ",
            ];
        }

        var restaurantName = SafeAscii(job.Payload.RestaurantName);
        var tableName = SafeAscii(job.Payload.TableName);
        var currency = SafeAscii(job.Payload.Currency);
        var paymentMethod = SafeAscii(job.Payload.PaymentMethod);

        bw.Write(new byte[] { 0x1B, 0x61, 0x01 });
        foreach (var line in UrsAsciiArt())
            bw.Write(Encoding.ASCII.GetBytes(line + "\n"));
        bw.Write(Encoding.ASCII.GetBytes("\n"));
        bw.Write(Encoding.ASCII.GetBytes("NOTA DE PLATA (NEFISCALA)\n"));
        if (!string.IsNullOrWhiteSpace(restaurantName))
            bw.Write(Encoding.ASCII.GetBytes($"{restaurantName}\n"));
        bw.Write(Encoding.ASCII.GetBytes("\n"));
        bw.Write(new byte[] { 0x1B, 0x61, 0x00 });

        bw.Write(Encoding.ASCII.GetBytes($"Comanda: {SafeAscii(job.Payload.OrderId)}\n"));
        if (!string.IsNullOrWhiteSpace(tableName))
            bw.Write(Encoding.ASCII.GetBytes($"Masa: {tableName}\n"));

        if (job.Payload.ClosedAtUtc is { } closedAt)
            bw.Write(Encoding.ASCII.GetBytes($"Data: {closedAt:yyyy-MM-dd HH:mm} UTC\n"));

        if (!string.IsNullOrWhiteSpace(paymentMethod))
            bw.Write(Encoding.ASCII.GetBytes($"Plata: {paymentMethod}\n"));

        bw.Write(Encoding.ASCII.GetBytes("--------------------------------\n"));

        decimal computed = 0m;
        foreach (var item in job.Payload.Items)
        {
            var unit = item.UnitPrice ?? item.Price;
            var lineTotal = unit * item.Quantity;
            computed += lineTotal;

            bw.Write(Encoding.ASCII.GetBytes($"{item.Quantity}x {SafeAscii(item.Name)}\n"));
            var right = string.IsNullOrWhiteSpace(currency)
                ? lineTotal.ToString("F2")
                : $"{lineTotal:F2} {currency}";
            bw.Write(Encoding.ASCII.GetBytes(right.PadLeft(32) + "\n"));
        }

        bw.Write(Encoding.ASCII.GetBytes("--------------------------------\n"));

        var final = job.Payload.FinalTotal ?? computed;
        var finalStr = string.IsNullOrWhiteSpace(currency)
            ? final.ToString("F2")
            : $"{final:F2} {currency}";
        bw.Write(Encoding.ASCII.GetBytes(("TOTAL: " + finalStr).PadLeft(32) + "\n"));

        bw.Write(Encoding.ASCII.GetBytes("\n\n"));
        bw.Write(new byte[] { 0x1D, 0x56, 0x41, 0x00 });
    }
}
