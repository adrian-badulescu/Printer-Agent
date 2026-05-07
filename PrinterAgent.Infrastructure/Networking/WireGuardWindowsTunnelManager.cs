using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace PrinterAgent.Infrastructure.Networking;

[SupportedOSPlatform("windows")]
public interface IWireGuardTunnelManager
{
    /// <summary>Installs (or re-installs) the tunnel as a Windows service from a .conf file.</summary>
    Task InstallTunnelServiceAsync(string confPath, CancellationToken cancellationToken = default);

    /// <summary>Uninstalls the tunnel service by tunnel name (the config base name).</summary>
    Task UninstallTunnelServiceAsync(string tunnelName, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the tunnel service exists.</summary>
    bool ServiceExists(string windowsTunnelServiceName);
}

[SupportedOSPlatform("windows")]
public sealed class WireGuardWindowsTunnelManager : IWireGuardTunnelManager
{
    private readonly ILogger<WireGuardWindowsTunnelManager> _logger;

    public WireGuardWindowsTunnelManager(ILogger<WireGuardWindowsTunnelManager> logger)
    {
        _logger = logger;
    }

    public bool ServiceExists(string windowsTunnelServiceName)
    {
        try
        {
            using var sc = new global::System.ServiceProcess.ServiceController(windowsTunnelServiceName);
            _ = sc.Status; // forces query
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task InstallTunnelServiceAsync(string confPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(confPath))
            throw new ArgumentException("confPath is required.", nameof(confPath));

        var exe = ResolveWireGuardExePath();
        var args = $"/installtunnelservice \"{confPath}\"";
        _logger.LogInformation("WireGuard: installing tunnel service from {ConfPath}", confPath);
        await RunAsync(exe, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task UninstallTunnelServiceAsync(string tunnelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tunnelName))
            throw new ArgumentException("tunnelName is required.", nameof(tunnelName));

        var exe = ResolveWireGuardExePath();
        var args = $"/uninstalltunnelservice \"{tunnelName}\"";
        _logger.LogInformation("WireGuard: uninstalling tunnel service {TunnelName}", tunnelName);
        await RunAsync(exe, args, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveWireGuardExePath()
    {
        // Typical install location for WireGuard for Windows.
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new[]
        {
            Path.Combine(pf, "WireGuard", "wireguard.exe"),
            Path.Combine(pf86, "WireGuard", "wireguard.exe"),
            "wireguard.exe" // fallback to PATH
        };

        foreach (var c in candidates)
        {
            if (string.Equals(c, "wireguard.exe", StringComparison.OrdinalIgnoreCase))
                return c;
            if (File.Exists(c))
                return c;
        }

        // Let ProcessStart fail with a clear error rather than guessing other paths.
        return "wireguard.exe";
    }

    private static async Task RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start process: {fileName}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = p.StandardError.ReadToEndAsync(cancellationToken);

        await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"WireGuard command failed (exit={p.ExitCode}). stdout={(string.IsNullOrWhiteSpace(stdout) ? "<empty>" : stdout.Trim())} stderr={(string.IsNullOrWhiteSpace(stderr) ? "<empty>" : stderr.Trim())}");
        }
    }
}

