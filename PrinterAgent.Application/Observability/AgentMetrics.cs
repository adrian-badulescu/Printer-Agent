using System.Diagnostics.Metrics;

namespace PrinterAgent.Application.Observability;

/// <summary>Metrici System.Diagnostics.Metrics (Meter „PrinterAgent”). Poți folosi dotnet-counters monitor pe counter-ul PrinterAgent.</summary>
public static class AgentMetrics
{
    private static readonly Meter Meter = new("PrinterAgent", "1.0.0");

    public static readonly Counter<long> JobsProcessed = Meter.CreateCounter<long>("printeragent.jobs.processed");
    public static readonly Counter<long> PrintFailures = Meter.CreateCounter<long>("printeragent.print.failures");
    public static readonly Counter<long> PrintRetries = Meter.CreateCounter<long>("printeragent.print.retries");
}
