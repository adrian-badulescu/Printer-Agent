namespace PrinterAgent.Worker.Config;

/// <summary>Probe opționale la pornire (după WireGuard, înainte de AgentWorker).</summary>
public class ConnectivityOptions
{
    public const string SectionName = "Connectivity";

    /// <summary>Dacă e true, face PING Redis și un GET HTTP către backend (implicit ping fără JWT).</summary>
    public bool VerifyAtStartup { get; set; } = true;

    /// <summary>Cale relativă la BackendUrl (ex. api/ping-lite).</summary>
    public string BackendHealthPath { get; set; } = "api/ping-lite";

    /// <summary>Timp pentru cererea HTTP de health.</summary>
    public int BackendHealthTimeoutSeconds { get; set; } = 10;
}
