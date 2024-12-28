using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Shared;
using Shared.Services;
using Service.Services;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public class Program
{
    [SupportedOSPlatform("windows")]
    private static ICertificatePollingService CreatePollingService(IServiceProvider sp)
    {
        return new CertificatePollingService(
            sp.GetRequiredService<ILogger<CertificatePollingService>>(),
            sp.GetRequiredService<CertificateTrackingService>(),
            sp.GetRequiredService<IConfigurationService>(),
            sp.GetRequiredService<ISocketMessageService>()
        );
    }

    public static async Task Main(string[] args)
    {
        var isService = !(Debugger.IsAttached || args.Contains("--console"));

        var builder = Host.CreateDefaultBuilder(args);

        if (isService)
        {
            builder.UseWindowsService(options =>
            {
                options.ServiceName = "CA Connector Service";
            });
        }

        var host = builder
            .ConfigureServices((context, services) =>
            {
                // Add configuration
                services.AddSingleton<ISecureStorage, SecureStorage>();
                services.AddSingleton<IConfigurationService, ConfigurationService>();
                services.AddHttpClient();
                services.AddSingleton<CertificateTrackingService>();
                
                // Socket services
                services.AddSingleton<SocketService>();
                services.AddSingleton<ISocketMessageService, SocketMessageService>();
                
                // Certificate polling service
                services.AddSingleton<ICertificatePollingService>(sp =>
                {
                    var pollingService = CreatePollingService(sp);
                    var socketService = sp.GetRequiredService<SocketService>();
                    socketService.ConfigurePollingService(pollingService);
                    return pollingService;
                });

                // Add hosted service
                services.AddHostedService<ServiceWorker>();
            })
            .UseSerilog((context, config) =>
            {
                var logPath = Path.Combine(Constants.LogPath, "service.log");
                
                // Ensure log directory exists
                Directory.CreateDirectory(Constants.LogPath);

                config
                    .MinimumLevel.Information()
                    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day);

                if (!isService)
                {
                    config.WriteTo.Console();
                }
            })
            .Build();

        if (!isService)
        {
            Console.WriteLine("Press Ctrl+C to exit");
        }

        try
        {
            await host.RunAsync();
        }
        catch (TaskCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            throw;
        }
    }
} 