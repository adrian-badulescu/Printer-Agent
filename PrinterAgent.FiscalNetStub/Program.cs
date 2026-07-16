var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var receiptCounter = 0;

app.MapPost("/api/Receipt", async (HttpRequest request) =>
{
    var body = await new StreamReader(request.Body).ReadToEndAsync();
    var isDrawerCommand = body.Contains("DS^", StringComparison.Ordinal);
    if (isDrawerCommand)
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] POST /api/Receipt command=open-drawer body={body}");
    else
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] POST /api/Receipt body={body}");

    receiptCounter++;
    return Results.Json(new[] { "BONOK=1", receiptCounter.ToString("D4") });
});

var port = int.TryParse(Environment.GetEnvironmentVariable("FISCALNET_STUB_PORT"), out var p) ? p : 65400;
Console.WriteLine($"FiscalNet stub listening on http://127.0.0.1:{port}/api/Receipt");
app.Run($"http://127.0.0.1:{port}");
