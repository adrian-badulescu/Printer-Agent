namespace PrinterAgent.Worker.Config;

/// <summary>
/// Opțional: tunel WireGuard pe Windows înainte de conectarea la Redis/backend în VPN.
/// În mod tipic, importați .conf în aplicația WireGuard; Windows creează un serviciu
/// de forma WireGuardTunnel$NumeFisierConf.
/// </summary>
public class WireGuardOptions
{
    public const string SectionName = "WireGuard";

    /// <summary>Pornește logica de bootstrap (așteptare / pornire serviciu).</summary>
    public bool Enabled { get; set; }

    /// <summary>Cale către fișierul .conf (doar documentare / loguri; nu e citit de agent).</summary>
    public string ConfigFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Numele serviciului Windows creat de WireGuard, ex. <c>WireGuardTunnel$restaurant</c>
    /// pentru fișierul restaurant.conf importat în UI.
    /// </summary>
    public string WindowsTunnelServiceName { get; set; } = string.Empty;

    /// <summary>Timp maxim de așteptare ca serviciul tunelului să fie Running.</summary>
    public int WaitForTunnelServiceSeconds { get; set; } = 120;

    /// <summary>Dacă e true, încearcă porunirea serviciului dacă nu rulează (necesită drepturi).</summary>
    public bool StartServiceIfStopped { get; set; }
}
