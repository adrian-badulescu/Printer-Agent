using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Storage;
using PrinterAgent.Application.UseCases;
using PrinterAgent.Infrastructure.Http;
using PrinterAgent.Infrastructure.Persistence;
using PrinterAgent.Infrastructure.Printing;
using PrinterAgent.Infrastructure.Redis;
using PrinterAgent.Infrastructure.System;
using PrinterAgent.Worker;
using PrinterAgent.Worker.Config;
using StackExchange.Redis;

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
            // 2) %ProgramData%\URSPrinterAgent\agent.json — operator overrides; survives upgrades when MSI uses NeverOverwrite on seed.
            var bundledAgentJson = Path.Combine(AppContext.BaseDirectory, "agent.json");
            _ = AgentProgramData.Root;
            var programDataAgentJson = Path.Combine(AgentProgramData.Root, "agent.json");
            config.AddJsonFile(bundledAgentJson, optional: true, reloadOnChange: false);
            config.AddJsonFile(programDataAgentJson, optional: false, reloadOnChange: true);
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

            services.AddSingleton<IAgentSessionStore, AgentSessionStore>();
            services.AddSingleton<IAgentSessionRenewalService, AgentSessionRenewalService>();

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

            // Redis: conectare la prima folosire (după enroll), nu la rezolvarea IHostedService.
            services.AddSingleton(sp =>
            {
                var config = sp.GetRequiredService<IAppConfiguration>();
                return new Lazy<IConnectionMultiplexer>(() =>
                    ConnectionMultiplexer.Connect(config.RedisConnectionString));
            });
            services.AddTransient<IRedisStreamConsumer, RedisStreamConsumer>();

            services.AddHostedService<AgentEnrollmentHostedService>();
            services.AddHostedService<AgentWorker>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    TryWriteFatalStartupLog(ex);
    throw;
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
