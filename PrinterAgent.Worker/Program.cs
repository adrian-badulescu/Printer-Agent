using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Storage;
using PrinterAgent.Application.UseCases;
using PrinterAgent.Infrastructure.Http;
using PrinterAgent.Infrastructure.Persistence;
using PrinterAgent.Infrastructure.Networking;
using PrinterAgent.Infrastructure.Printing;
using PrinterAgent.Infrastructure.Redis;
using PrinterAgent.Infrastructure.System;
using PrinterAgent.Worker;
using PrinterAgent.Worker.Config;
using PrinterAgent.Worker.Logging;
using PrinterAgent.Infrastructure.Redis;

try
{
    // #region agent log
    LogServiceBootstrapDiagnostics();
    // #endregion

    var host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = "URSPrinterAgent";
        })
        .ConfigureAppConfiguration((hostContext, config) =>
        {
            // 1) Defaults bundled with the EXE (updated on every MSI / in-app update install).
            // 2) %ProgramData%\URSPrinterAgent\agent.json — operator overrides; must be optional: MSI may not have
            //    laid down the file yet, or a manual copy was never created; ProgramData still exists for session/logs.
            var bundledAgentJson = Path.Combine(AppContext.BaseDirectory, "agent.json");
            _ = AgentProgramData.Root;
            var programDataAgentJson = Path.Combine(AgentProgramData.Root, "agent.json");
            config.AddJsonFile(bundledAgentJson, optional: true, reloadOnChange: false);
            AddProgramDataAgentJsonIfValid(config, programDataAgentJson);
        })
        .ConfigureLogging(logging =>
        {
            logging.AddProvider(new ProgramDataFileLoggerProvider());
        })
        .ConfigureServices((hostContext, services) =>
        {
            // Config
            services.AddSingleton<IAppConfiguration, AppConfiguration>();
            services.Configure<WireGuardOptions>(hostContext.Configuration.GetSection(WireGuardOptions.SectionName));
            services.Configure<ConnectivityOptions>(hostContext.Configuration.GetSection(ConnectivityOptions.SectionName));

            // Tunel WireGuard (opțional) înainte de Redis / enroll / AgentWorker
            services.AddHostedService<WireGuardTunnelHostedService>();
            services.AddHostedService<StartupConnectivityHostedService>();
            services.AddHostedService<PrinterStartupRecoveryHostedService>();

            services.AddSingleton<IAgentSessionStore, AgentSessionStore>();
            services.AddSingleton<IAgentSessionRenewalService, AgentSessionRenewalService>();
            services.AddSingleton<IAgentPrinterConfigurationUpdater, AgentPrinterConfigurationUpdater>();
            services.AddSingleton<IPrinterDiscoveryService, PrinterDiscoveryService>();
            services.AddSingleton<IPrinterMacCapture, PrinterMacCaptureService>();

            services.AddHttpClient("PrinterAgentEnroll", (sp, client) =>
            {
                var cfg = sp.GetRequiredService<IAppConfiguration>();
                var baseUrl = cfg.BackendUrl?.Trim();
                if (string.IsNullOrEmpty(baseUrl))
                    throw new InvalidOperationException("BackendUrl is required.");
                if (!baseUrl.EndsWith('/'))
                    baseUrl += "/";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromMinutes(2);
            });

            // Application
            services.AddTransient<IPrintJobProcessor, PrintJobProcessor>();
            services.AddTransient<IHeartbeatService, HeartbeatService>();

            // Infrastructure
            services.AddTransient<PrinterAgentAuthHandler>();
            services.AddTransient<IPrinterService, EscPosPrinterService>();
            services.AddTransient<IUpdateService, UpdateService>();
            services.AddHttpClient<IBackendClient, BackendClient>().AddHttpMessageHandler<PrinterAgentAuthHandler>();

            // WireGuard for Windows automation (install tunnel service from .conf).
            services.AddSingleton<IWireGuardTunnelManager, WireGuardWindowsTunnelManager>();

            services.AddSingleton<IRedisConnectionMultiplexerHolder, RedisConnectionMultiplexerHolder>();
            services.AddTransient<IRedisStreamConsumer, RedisStreamConsumer>();

            services.AddHostedService<AgentEnrollmentHostedService>();
            services.AddHostedService<AgentWorker>();
        })
        .Build();

    var bootLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PrinterAgent.Worker");
    var infraAsm = typeof(AgentSessionStore).Assembly;
    var infraVer = infraAsm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? infraAsm.GetName().Version?.ToString()
                   ?? "?";
    bootLogger.LogInformation(
        "Bootstrap: PrinterAgent.Infrastructure={InfraVersion}. Session IO uses File.Replace + retry (if logs still show File.Create, the installed DLL is outdated).",
        infraVer);

    await host.RunAsync();
}
catch (Exception ex)
{
    TryWriteFatalStartupLog(ex);
    throw;
}

static void AddProgramDataAgentJsonIfValid(IConfigurationBuilder config, string programDataAgentJson)
{
    if (!File.Exists(programDataAgentJson))
        return;

    try
    {
        var json = File.ReadAllText(programDataAgentJson);
        JsonDocument.Parse(json);
        config.AddJsonFile(programDataAgentJson, optional: true, reloadOnChange: true);
    }
    catch (Exception ex)
    {
        TryWriteFatalStartupLog(ex);
        TryWriteConfigSkipWarning(programDataAgentJson, ex);
    }
}

static void TryWriteConfigSkipWarning(string path, Exception ex)
{
    try
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AgentProgramData.FolderName,
            "logs");
        Directory.CreateDirectory(logDir);
        var warningPath = Path.Combine(logDir, "agent-json-invalid.txt");
        File.WriteAllText(
            warningPath,
            $"""
            {DateTime.UtcNow:O} UTC — agent.json was NOT loaded (invalid JSON). Service starts with install-dir defaults only.
            File: {path}
            Fix: use normal JSON quotes only (no backslash-escaped \" around values). For Redis passwords containing #, set "Password": "your-redis-password" in agent.json — do not hand-quote ConnectionString unless you know JSON escaping.

            Error: {ex.Message}
            """);
    }
    catch
    {
        // ignore
    }
}

static void LogServiceBootstrapDiagnostics()
{
    try
    {
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PrinterAgent.Worker.exe");
        var bundledJson = Path.Combine(AppContext.BaseDirectory, "agent.json");
        var programDataJson = Path.Combine(AgentProgramData.Root, "agent.json");
        string? startType = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\URSPrinterAgent");
            startType = key?.GetValue("Start")?.ToString();
        }
        catch { /* ignore */ }

        DebugSessionLog.Write("H1", "Program.cs:bootstrap", "service_registry", new
        {
            startType,
            startTypeDisabled = startType == "4",
            exePath,
            exeExists = File.Exists(exePath),
            bundledJsonExists = File.Exists(bundledJson),
            programDataJsonExists = File.Exists(programDataJson)
        });
    }
    catch { /* ignore */ }
}

static void TryWriteFatalStartupLog(Exception ex)
{
    try
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AgentProgramData.FolderName);
        Directory.CreateDirectory(root);
        var logDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDir);
        var path = Path.Combine(logDir, "fatal-startup.txt");
        File.AppendAllText(
            path,
            $"{DateTime.UtcNow:O} UTC{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 72)}{Environment.NewLine}");
    }
    catch
    {
        // ignore secondary failures
    }
}
