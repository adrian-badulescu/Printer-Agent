using System.Diagnostics;

namespace PrinterAgent.Infrastructure.System;

/// <summary>
/// Starts WiX Burn after the Windows service process exits, avoiding stop/install races.
/// </summary>
internal static class UpdateInstallerLauncher
{
    /// <summary>~1s between pings; 8 pings ≈ 7s delay before the installer runs.</summary>
    internal const int DefaultDelayPingCount = 8;

    internal static string GetUpdatesDirectory() =>
        Path.Combine(PrinterAgent.Application.Storage.AgentProgramData.Root, "updates");

    internal static string BuildDelayedInstallArguments(string installerPath, string logPath, int delayPingCount = DefaultDelayPingCount)
    {
        var pings = Math.Clamp(delayPingCount, 2, 60);
        return $"/c ping 127.0.0.1 -n {pings} >nul & \"{installerPath}\" /quiet /norestart /log \"{logPath}\"";
    }

    internal static void LaunchDelayedInstallAndExit(string installerPath, string logPath, int delayPingCount = DefaultDelayPingCount)
    {
        var arguments = BuildDelayedInstallArguments(installerPath, logPath, delayPingCount);
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _ = Process.Start(psi);

        // Give cmd.exe time to register before the service host is torn down.
        Thread.Sleep(TimeSpan.FromSeconds(2));

        Environment.Exit(0);
    }
}
