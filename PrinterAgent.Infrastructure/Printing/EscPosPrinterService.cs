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

    public EscPosPrinterService(ILogger<EscPosPrinterService> logger, IAppConfiguration appConfiguration)
    {
        _logger = logger;
        _appConfiguration = appConfiguration;
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
                await client.ConnectAsync(printer.IpAddress, printer.Port, cancellationToken);
                using var stream = client.GetStream();

                var escPosData = RenderReceipt(job);
                await stream.WriteAsync(escPosData, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                _logger.LogInformation("Job {JobId} printed successfully to {PrinterName}.", job.RedisMessageId, printer.Name);
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

        return ms.ToArray();
    }
}
