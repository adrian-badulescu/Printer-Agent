using System.IO;
using System.Net.Sockets;
using System.Text;
using PrinterAgent.Domain;

namespace PrinterAgent.Configurator.Services;

public sealed class TestPrintService
{
    private const int ConnectTimeoutSeconds = 10;

    /// <summary>ESC/POS minimal: init, text, partial cut.</summary>
    public async Task<bool> SendTestPageAsync(Printer printer, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
            await client.ConnectAsync(printer.IpAddress, printer.Port, cts.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            var payload = BuildPayload(printer.Name);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] BuildPayload(string printerName)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 0x1B, 0x40 });
        bw.Write(new byte[] { 0x1B, 0x61, 0x01 });
        bw.Write(Encoding.ASCII.GetBytes("URS Agent — test configurator\n\n"));
        bw.Write(Encoding.ASCII.GetBytes($"{printerName}\n\n"));
        bw.Write(new byte[] { 0x1B, 0x61, 0x00 });
        bw.Write(Encoding.ASCII.GetBytes($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n"));
        bw.Write(new byte[] { 0x1D, 0x56, 0x41, 0x00 });
        return ms.ToArray();
    }
}
