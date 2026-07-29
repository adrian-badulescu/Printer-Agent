var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var receiptCounter = 0;
var zReportNumber = Environment.GetEnvironmentVariable("FISCALNET_STUB_ZREPORT")?.Trim();
if (string.IsNullOrWhiteSpace(zReportNumber))
    zReportNumber = "0001";

var port = int.TryParse(Environment.GetEnvironmentVariable("FISCALNET_STUB_PORT"), out var p) ? p : 65400;

app.MapPost("/api/Receipt", async (HttpRequest request) =>
{
    var body = await new StreamReader(request.Body).ReadToEndAsync();
    var isDrawerCommand = body.Contains("DS^", StringComparison.Ordinal);
    var message = isDrawerCommand ? "POST /api/Receipt command=open-drawer" : "POST /api/Receipt";
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message} body={body}");

    receiptCounter++;
    var fiscalDate = DateTime.Now.ToString("ddMMyyyy");
    return Results.Json(new[]
    {
        "BONOK=1",
        $"NRBON={receiptCounter:D4}",
        $"NRZ={zReportNumber}",
        $"DATA={fiscalDate}",
    });
});

// Bind all interfaces so agent can use LAN IP, 127.0.0.1, or a changed Wi-Fi address.
Console.WriteLine($"FiscalNet stub listening on http://0.0.0.0:{port}/api/Receipt (POST only)");
app.Run($"http://0.0.0.0:{port}");
