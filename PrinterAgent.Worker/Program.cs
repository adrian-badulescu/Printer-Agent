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
using PrinterAgent.Infrastructure.LocalApi;
using PrinterAgent.Infrastructure.Persistence;
using PrinterAgent.Infrastructure.Networking;
using PrinterAgent.Infrastructure.Persistence;
using PrinterAgent.Infrastructure.Printing;
using PrinterAgent.Infrastructure.Printing.Fiscal;
using PrinterAgent.Infrastructure.Redis;
using PrinterAgent.Infrastructure.System;
using PrinterAgent.Worker;
using PrinterAgent.Worker.Config;
using PrinterAgent.Worker.Logging;

try
{
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
            var bundledReceiptHeader = Path.Combine(AppContext.BaseDirectory, AgentProgramData.ReceiptHeaderFileName);
            var programDataReceiptHeader = Path.Combine(AgentProgramData.Root, AgentProgramData.ReceiptHeaderFileName);
            EnsureProgramDataAgentJsonForConfiguration(bundledAgentJson, programDataAgentJson);
            AgentProgramDataAgentJsonSync.TryWriteVersionFromInstallDir(bundledAgentJson);
            EnsureProgramDataReceiptHeaderForConfiguration(bundledReceiptHeader, programDataReceiptHeader);
            ValidateProgramDataAgentJsonOrWarn(programDataAgentJson);
            config.AddJsonFile(bundledAgentJson, optional: true, reloadOnChange: false);
            config.AddJsonFile(programDataAgentJson, optional: true, reloadOnChange: true);
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
            services.Configure<LocalPrintOptions>(hostContext.Configuration.GetSection(LocalPrintOptions.SectionName));

            // Tunel WireGuard (opțional) înainte de Redis / enroll / AgentWorker
            services.AddHostedService<WireGuardTunnelHostedService>();
            services.AddHostedService<PrinterStartupRecoveryHostedService>();
            services.AddHostedService<AgentConfigurationReloadHostedService>();

            services.AddSingleton<IAgentSessionStore, AgentSessionStore>();
            services.AddSingleton<IDeviceCredentialStore, DeviceCredentialStore>();
            services.AddSingleton<IRedisRuntimeCredentials, RedisRuntimeCredentialsStore>();
            services.AddSingleton<IAgentSessionRenewalService, AgentSessionRenewalService>();
            services.AddSingleton<IAgentDeviceRenewalService, AgentDeviceRenewalService>();
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

            services.AddHttpClient("FiscalNet", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(3);
            });

            services.AddHttpClient("FpMate")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
                })
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(3));

            // Application
            services.AddTransient<IPrintJobProcessor, PrintJobProcessor>();
            services.AddTransient<ILocalPrintJobHandler, LocalPrintJobHandler>();
            services.AddTransient<IHeartbeatService, HeartbeatService>();
            services.AddSingleton<ILocalPrintAuthTokenProvider, LocalPrintAuthTokenProvider>();

            // Infrastructure
            services.AddTransient<PrinterAgentAuthHandler>();
            services.AddTransient<EscPosPrinterService>();
            services.AddTransient<FiscalNetHttpClient>();
            services.AddTransient<FiscalNetPrinterService>();
            services.AddTransient<FpMateSoapClient>();
            services.AddTransient<IEpsonFiscalClient>(sp => sp.GetRequiredService<FpMateSoapClient>());
            services.AddTransient<EpsonFiscalPrinterService>();
            services.AddTransient<IFiscalCommandHandler, FiscalNetCommandHandler>();
            services.AddTransient<IFiscalCommandHandler, EpsonFiscalCommandHandler>();
            services.AddSingleton<IFiscalCommandRouter, FiscalCommandRouter>();
            services.AddSingleton<IPrinterServiceFactory, PrinterServiceFactory>();
            services.AddHttpClient("ReleaseUpdate", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(15);
            });

            services.AddTransient<IUpdateService, UpdateService>();
            services.AddHttpClient<IBackendClient, BackendClient>().AddHttpMessageHandler<PrinterAgentAuthHandler>();

            // WireGuard for Windows automation (install tunnel service from .conf).
            services.AddSingleton<IWireGuardTunnelManager, WireGuardWindowsTunnelManager>();

            services.AddSingleton<IRedisConnectionMultiplexerHolder, RedisConnectionMultiplexerHolder>();
            services.AddTransient<IRedisStreamConsumer, RedisStreamConsumer>();

            services.AddHostedService<AgentEnrollmentHostedService>();
            services.AddHostedService<AgentWorker>();
            services.AddHostedService<LocalPrintApiHostedService>();
            services.AddHostedService<StartupConnectivityHostedService>();
        })
        .Build();

    var bootLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PrinterAgent.Worker");
    var bootCfg = host.Services.GetRequiredService<IAppConfiguration>();
    var programDataAgentJson = Path.Combine(AgentProgramData.Root, "agent.json");
    string? programDataBackendUrl = null;
    if (File.Exists(programDataAgentJson))
    {
        try
        {
            programDataBackendUrl = JsonDocument.Parse(File.ReadAllText(programDataAgentJson))
                .RootElement.GetProperty("BackendUrl").GetString();
        }
        catch
        {
            // optional diagnostic only
        }
    }

    bootLogger.LogInformation(
        "Effective config (install-dir wins for BackendUrl/Redis): BackendUrl={BackendUrl} Redis={RedisSummary} Version={Version}. ProgramData BackendUrl={ProgramDataBackendUrl} (may differ after upgrade — see docs/PRODUCTION_AGENT_CHECKLIST.md).",
        bootCfg.BackendUrl,
        bootCfg.RedisConnectionSummary,
        bootCfg.Version,
        programDataBackendUrl ?? "(missing)");

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

/// <summary>
/// Ensures ProgramData agent.json exists at host build so reloadOnChange watches the path Configurator writes to.
/// </summary>
static void EnsureProgramDataReceiptHeaderForConfiguration(string bundledReceiptHeader, string programDataReceiptHeader)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(programDataReceiptHeader)!);
        if (File.Exists(programDataReceiptHeader))
            return;

        if (File.Exists(bundledReceiptHeader))
            File.Copy(bundledReceiptHeader, programDataReceiptHeader);
    }
    catch
    {
        // optional file; EscPosPrinterService falls back to embedded default
    }
}

static void EnsureProgramDataAgentJsonForConfiguration(string bundledAgentJson, string programDataAgentJson)
{
    try
    {
        AgentProgramDataAccess.EnsureWritable();
        Directory.CreateDirectory(Path.GetDirectoryName(programDataAgentJson)!);
        if (File.Exists(programDataAgentJson))
            return;

        if (File.Exists(bundledAgentJson))
            File.Copy(bundledAgentJson, programDataAgentJson);
        else
            File.WriteAllText(programDataAgentJson, "{}");
    }
    catch (Exception ex)
    {
        TryWriteConfigSkipWarning(programDataAgentJson, ex);
    }
}

static void ValidateProgramDataAgentJsonOrWarn(string programDataAgentJson)
{
    if (!File.Exists(programDataAgentJson))
        return;

    try
    {
        JsonDocument.Parse(File.ReadAllText(programDataAgentJson));
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
