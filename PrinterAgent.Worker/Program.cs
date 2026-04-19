using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.UseCases;
using PrinterAgent.Infrastructure.Http;
using PrinterAgent.Infrastructure.Printing;
using PrinterAgent.Infrastructure.Redis;
using PrinterAgent.Infrastructure.System;
using PrinterAgent.Worker;
using PrinterAgent.Worker.Config;
using StackExchange.Redis;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "URSPrinterAgent";
    })
    .ConfigureAppConfiguration((hostContext, config) =>
    {
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("agent.json", optional: false, reloadOnChange: true);
    })
    .ConfigureServices((hostContext, services) =>
    {
        // Config
        services.AddSingleton<IAppConfiguration, AppConfiguration>();
        services.Configure<WireGuardOptions>(hostContext.Configuration.GetSection(WireGuardOptions.SectionName));
        services.Configure<ConnectivityOptions>(hostContext.Configuration.GetSection(ConnectivityOptions.SectionName));

        // Tunel WireGuard (opțional) înainte de Redis / AgentWorker
        services.AddHostedService<WireGuardTunnelHostedService>();
        services.AddHostedService<StartupConnectivityHostedService>();

        // Application
        services.AddTransient<IPrintJobProcessor, PrintJobProcessor>();
        services.AddTransient<IHeartbeatService, HeartbeatService>();

        // Infrastructure
        services.AddSingleton<IMacResolver, MacResolver>();
        services.AddTransient<IPrinterService, EscPosPrinterService>();
        services.AddTransient<IUpdateService, UpdateService>();
        services.AddHttpClient<IBackendClient, BackendClient>();
        
        // Redis
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var config = sp.GetRequiredService<IAppConfiguration>();
            return ConnectionMultiplexer.Connect(config.RedisConnectionString);
        });
        services.AddTransient<IRedisStreamConsumer, RedisStreamConsumer>();

        // Worker
        services.AddHostedService<AgentWorker>();
    })
    .Build();

await host.RunAsync();
