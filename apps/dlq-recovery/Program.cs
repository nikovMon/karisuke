using ImagingPipeline.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.DlqRecovery;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddImagingPipelineObservability("dlq-recovery");
        var options = builder.Configuration.GetSection("Recovery").Get<RecoveryOptions>() ?? new RecoveryOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRecoverySessionFactory, RabbitMqRecoverySessionFactory>();
        builder.Services.AddSingleton<RecoveryRunner>();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<RecoveryRunner>>();
        try
        {
            options.Validate();
            await host.StartAsync();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            return await host.Services.GetRequiredService<RecoveryRunner>().RunAsync(lifetime.ApplicationStopping);
        }
        catch (InvalidOperationException ex)
        {
            DlqRecoveryLog.InvalidConfiguration(logger, ex.Message);
            return 2;
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(options.ShutdownReserveSeconds, 1)));
            await host.StopAsync(shutdown.Token);
        }
    }
}
