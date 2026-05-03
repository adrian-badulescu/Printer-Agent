using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Networking;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Networking;

public sealed class PrinterDiscoveryService : IPrinterDiscoveryService
{
    private const int ScanParallelism = 40;
    // Probes must allow time for ARP resolution + TCP handshake; 300–400ms can miss reachable printers.
    private const int TcpProbeMs = 1500;
    private const int ArpProbeMs = 1500;
    /// <summary>Neighbor IP probes can follow a long print retry; allow extra time for the raw port.</summary>
    private const int NeighborTcpProbeMs = 2500;
    private const int ScanBudgetSeconds = 90;

    private static readonly SemaphoreSlim RecoveryGate = new(1, 1);
    /// <summary>Cooldown per printer so one subnet scan does not block recovery for another printer.</summary>
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastSubnetScanUtcByPrinterId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MinBetweenSubnetScans = TimeSpan.FromMinutes(5);

    private static void MarkSubnetScanCompleted(string printerId) =>
        LastSubnetScanUtcByPrinterId[printerId] = DateTimeOffset.UtcNow;

    private readonly IAgentPrinterConfigurationUpdater _updater;
    private readonly ILogger<PrinterDiscoveryService> _logger;
    private readonly IConfiguration _configuration;

    public PrinterDiscoveryService(
        IAgentPrinterConfigurationUpdater updater,
        ILogger<PrinterDiscoveryService> logger,
        IConfiguration configuration)
    {
        _updater = updater;
        _logger = logger;
        _configuration = configuration;
    }

    private void ReloadConfiguration()
    {
        if (_configuration is IConfigurationRoot root)
            root.Reload();
    }

    public async Task<PrinterRecoveryResult> TryRecoverAfterPrintFailureAsync(
        Printer printer,
        CancellationToken cancellationToken = default)
    {
        // --- Fast path: MAC known → ARP table only (no subnet scan cooldown) ---
        var macNorm = PrinterMacNormalizer.Normalize(printer.MacAddress);
        if (!string.IsNullOrEmpty(macNorm) &&
            IpHlpNative.TryFindIpv4ForMac(macNorm, out var arpIp) &&
            arpIp != null)
        {
            var ipStr = arpIp.ToString();
            if (string.Equals(ipStr, printer.IpAddress, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Printer recovery: ARP still maps MAC to configured IP {Ip} for {PrinterId} (stale ARP or wrong MAC). Subnet scan will run if allowed.",
                    ipStr, printer.Id);
            }
            else if (await ProbeTcpAsync(ipStr, printer.Port, TcpProbeMs, cancellationToken).ConfigureAwait(false))
            {
                if (_updater.TryPatchPrinter(printer.Id, ipStr, macNorm, false, "arp_mac_match"))
                    ReloadConfiguration();
                _logger.LogInformation(
                    "Printer recovery (ARP): {PrinterId} now at {Ip} (MAC match).",
                    printer.Id, ipStr);
                return new PrinterRecoveryResult
                {
                    Recovered = true,
                    Printer = ClonePrinter(printer, ipStr, false, "arp_mac_match"),
                    TelemetryNote = "arp_mac_match"
                };
            }

            _logger.LogWarning(
                "Printer recovery: ARP reports {Ip} for MAC but TCP :{Port} probe failed for {PrinterId}; continuing to subnet scan.",
                ipStr, printer.Port, printer.Id);
        }

        // --- Same-subnet last-octet neighbors (common mis-key / DHCP ±1) before ARP populates / before heavy scan ---
        var parsedNeighborOk = IPAddress.TryParse(printer.IpAddress ?? string.Empty, out var parsedNeighbor);
        var neighborEligible = !string.IsNullOrEmpty(macNorm) &&
                               parsedNeighborOk &&
                               parsedNeighbor is not null &&
                               parsedNeighbor.AddressFamily == AddressFamily.InterNetwork;
        if (neighborEligible && parsedNeighbor is not null)
        {
            const int neighborRadius = 10;
            foreach (var cand in EnumerateSameSubnetLastOctetNeighbors(parsedNeighbor, neighborRadius))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(cand, printer.IpAddress, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var ping = new Ping();
                    _ = await ping.SendPingAsync(cand, 400).ConfigureAwait(false);
                }
                catch
                {
                    // ICMP may be blocked; TCP probe still runs.
                }

                if (!await ProbeTcpAsync(cand, printer.Port, NeighborTcpProbeMs, cancellationToken).ConfigureAwait(false))
                    continue;

                if (!IPAddress.TryParse(cand, out var candIp))
                    continue;

                string? macAtHost = null;
                for (var ri = 0; ri < 10; ri++)
                {
                    if (IpHlpNative.TryGetMacForIPv4(candIp, out macAtHost) && !string.IsNullOrEmpty(macAtHost))
                        break;
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(macAtHost))
                    continue;

                var macAtNorm = PrinterMacNormalizer.Normalize(macAtHost);
                if (!string.Equals(macAtNorm, macNorm, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_updater.TryPatchPrinter(printer.Id, cand, macNorm, false, "same_subnet_neighbor_mac_match"))
                    ReloadConfiguration();
                _logger.LogInformation(
                    "Printer recovery (neighbor): {PrinterId} at {Ip} (MAC match; configured was {OldIp}).",
                    printer.Id,
                    cand,
                    printer.IpAddress);
                return new PrinterRecoveryResult
                {
                    Recovered = true,
                    Printer = ClonePrinter(printer, cand, false, "same_subnet_neighbor_mac_match"),
                    TelemetryNote = "same_subnet_neighbor_mac_match"
                };
            }
        }

        await RecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (LastSubnetScanUtcByPrinterId.TryGetValue(printer.Id, out var lastScan) &&
                DateTimeOffset.UtcNow - lastScan < MinBetweenSubnetScans)
            {
                _logger.LogWarning(
                    "Printer recovery scan skipped (cooldown) for {PrinterId}.",
                    printer.Id);
                return new PrinterRecoveryResult
                {
                    Recovered = false,
                    TelemetryNote = "scan_cooldown"
                };
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(ScanBudgetSeconds));

            var hosts = LocalSubnetEnumerator.EnumerateLikelyHostAddresses();
            _logger.LogInformation(
                "Printer recovery: scanning {Count} addresses for open port {Port} on printer {PrinterId}.",
                hosts.Count, printer.Port, printer.Id);

            var openHosts = new ConcurrentBag<(string Ip, string? Mac)>();
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = ScanParallelism,
                CancellationToken = budget.Token
            };

            try
            {
                await Parallel.ForEachAsync(hosts, po, async (hostIp, ct) =>
                {
                    if (!await ProbeTcpAsync(hostIp, printer.Port, TcpProbeMs, ct).ConfigureAwait(false))
                        return;

                    string? mac = null;
                    if (IPAddress.TryParse(hostIp, out var ipa))
                        IpHlpNative.TryGetMacForIPv4(ipa, out mac);

                    openHosts.Add((hostIp, mac));
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Printer recovery scan cancelled or timed out for {PrinterId}.", printer.Id);
                MarkSubnetScanCompleted(printer.Id);
                return new PrinterRecoveryResult { Recovered = false, TelemetryNote = "scan_cancelled" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Printer recovery scan failed for {PrinterId}.", printer.Id);
                MarkSubnetScanCompleted(printer.Id);
                return new PrinterRecoveryResult { Recovered = false, TelemetryNote = "scan_exception" };
            }

            var openCount = openHosts.Count;

            string? matchedIp = null;
            string? firstAny = null;
            foreach (var (ip, mac) in openHosts)
            {
                firstAny ??= ip;
                if (!string.IsNullOrEmpty(macNorm) && mac != null)
                {
                    var m = PrinterMacNormalizer.Normalize(mac);
                    if (m != null &&
                        string.Equals(m, macNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedIp = ip;
                        break;
                    }
                }
            }

            if (matchedIp != null)
            {
                if (_updater.TryPatchPrinter(printer.Id, matchedIp, macNorm, false, "scan_mac_match"))
                    ReloadConfiguration();
                MarkSubnetScanCompleted(printer.Id);
                _logger.LogInformation(
                    "Printer recovery (scan): {PrinterId} matched MAC at {Ip}.",
                    printer.Id, matchedIp);
                return new PrinterRecoveryResult
                {
                    Recovered = true,
                    Printer = ClonePrinter(printer, matchedIp, false, "scan_mac_match"),
                    TelemetryNote = "scan_mac_match"
                };
            }

            if (firstAny != null)
            {
                if (_updater.TryPatchPrinter(
                        printer.Id,
                        firstAny,
                        printer.MacAddress,
                        true,
                        "fallback_first_port9100"))
                    ReloadConfiguration();
                MarkSubnetScanCompleted(printer.Id);
                _logger.LogWarning(
                    "Printer recovery (degraded): {PrinterId} mapped to first host with port open {Ip}.",
                    printer.Id, firstAny);
                return new PrinterRecoveryResult
                {
                    Recovered = true,
                    Printer = ClonePrinter(printer, firstAny, true, "fallback_first_port9100"),
                    TelemetryNote = "fallback_first_port9100"
                };
            }

            // Do not cooldown after no_candidate — operator may fix network / next print must be allowed to scan again.
            _logger.LogWarning(
                "Printer recovery: no host with TCP port {Port} open on scanned subnets for {PrinterId} (openCandidates={Open}). Check AP isolation, firewall, or VLAN.",
                printer.Port,
                printer.Id,
                openCount);
            return new PrinterRecoveryResult { Recovered = false, TelemetryNote = "no_candidate" };
        }
        finally
        {
            RecoveryGate.Release();
        }
    }

    public async Task<IReadOnlyList<Printer>> MergeArpEndpointsAsync(
        IReadOnlyList<Printer> printers,
        CancellationToken cancellationToken = default)
    {
        var list = new List<Printer>(printers.Count);
        foreach (var p in printers)
        {
            var copy = ClonePrinter(p, p.IpAddress, p.FallbackProvisional, p.LastDiscoveryNote);
            var macNorm = PrinterMacNormalizer.Normalize(p.MacAddress);
            if (!string.IsNullOrEmpty(macNorm) &&
                IpHlpNative.TryFindIpv4ForMac(macNorm, out var nip) &&
                nip != null)
            {
                var s = nip.ToString();
                if (!string.Equals(s, p.IpAddress, StringComparison.OrdinalIgnoreCase) &&
                    await ProbeTcpAsync(s, p.Port, ArpProbeMs, cancellationToken).ConfigureAwait(false))
                {
                    copy.IpAddress = s;
                    if (_updater.TryPatchPrinter(p.Id, s, null, null, "heartbeat_arp_refresh"))
                        ReloadConfiguration();
                }
            }

            list.Add(copy);
        }

        return list;
    }

    /// <summary>Try .x±1, .x±2, … on the same /24 (config row’s subnet) — fixes stale ARP + wrong last octet.</summary>
    private static IEnumerable<string> EnumerateSameSubnetLastOctetNeighbors(IPAddress configured, int radius)
    {
        var b = configured.GetAddressBytes();
        if (b.Length != 4)
            yield break;

        for (var d = 1; d <= radius; d++)
        {
            foreach (var delta in new[] { d, -d })
            {
                var n = (int)b[3] + delta;
                if (n is < 1 or > 254)
                    continue;
                yield return $"{b[0]}.{b[1]}.{b[2]}.{n}";
            }
        }
    }

    private static async Task<bool> ProbeTcpAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Printer ClonePrinter(Printer p, string ip, bool fallback, string? note) =>
        new()
        {
            Id = p.Id,
            Name = p.Name,
            IpAddress = ip,
            Port = p.Port,
            Status = p.Status,
            MacAddress = p.MacAddress,
            FallbackProvisional = fallback,
            LastDiscoveryNote = note
        };
}
