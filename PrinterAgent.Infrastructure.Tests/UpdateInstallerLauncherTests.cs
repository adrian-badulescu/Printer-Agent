using PrinterAgent.Infrastructure.System;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class UpdateInstallerLauncherTests
{
    [Fact]
    public void BuildDelayedInstallArguments_waits_then_runs_installer()
    {
        var args = UpdateInstallerLauncher.BuildDelayedInstallArguments(
            @"C:\ProgramData\URSPrinterAgent\updates\setup.exe",
            @"C:\Temp\urs-agent-update.log");

        Assert.Contains("ping 127.0.0.1 -n 8 >nul", args);
        Assert.Contains(@"""C:\ProgramData\URSPrinterAgent\updates\setup.exe"" /quiet /norestart", args);
        Assert.Contains(@"/log ""C:\Temp\urs-agent-update.log""", args);
    }

    [Fact]
    public void GetUpdatesDirectory_uses_ProgramData()
    {
        var dir = UpdateInstallerLauncher.GetUpdatesDirectory();
        Assert.EndsWith(@"\URSPrinterAgent\updates", dir, StringComparison.OrdinalIgnoreCase);
    }
}
