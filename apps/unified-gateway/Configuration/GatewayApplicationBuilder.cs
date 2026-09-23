using System.Reflection;
using ImagingPipeline.PipelineCatalog;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.UnifiedGateway.Configuration;

public static class GatewayApplicationBuilder
{
    public static WebApplicationBuilder Create(string[] args, WebApplicationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var effectiveArgs = options?.Args ?? args;
        var hostSources = new ConfigurationBuilder();
        var workingDirectory = Environment.CurrentDirectory;
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(workingDirectory, Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase))
        {
            hostSources.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HostDefaults.ContentRootKey] = workingDirectory
            });
        }

        hostSources.AddEnvironmentVariables("ASPNETCORE_");
        hostSources.AddEnvironmentVariables("DOTNET_");
        if (effectiveArgs.Length > 0)
            hostSources.AddCommandLine(effectiveArgs);

        using var hostConfiguration = (ConfigurationRoot)hostSources.Build();
        var resolvedOptions = new WebApplicationOptions
        {
            Args = effectiveArgs,
            ApplicationName = options?.ApplicationName ?? hostConfiguration[HostDefaults.ApplicationKey],
            EnvironmentName = options?.EnvironmentName ?? hostConfiguration[HostDefaults.EnvironmentKey],
            ContentRootPath = options?.ContentRootPath ?? hostConfiguration[HostDefaults.ContentRootKey],
            WebRootPath = options?.WebRootPath ?? hostConfiguration[WebHostDefaults.WebRootKey]
        };

        // CreateBuilder eagerly flattens appsettings before custom providers can be installed.
        // Start empty so arbitrary ExtraData keys and JSON types survive their very first read.
        var builder = WebApplication.CreateEmptyBuilder(resolvedOptions);
        foreach (var source in hostSources.Sources)
            ((IConfigurationBuilder)builder.Configuration).Add(source);

        // Explicit WebApplicationOptions take precedence over prefixed host variables and arguments.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HostDefaults.ApplicationKey] = builder.Environment.ApplicationName,
            [HostDefaults.EnvironmentKey] = builder.Environment.EnvironmentName,
            [HostDefaults.ContentRootKey] = builder.Environment.ContentRootPath
        });
        if (resolvedOptions.WebRootPath is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WebHostDefaults.WebRootKey] = resolvedOptions.WebRootPath
            });
        }

        AddApplicationConfiguration(builder, effectiveArgs);
        AddHostingDefaults(builder);
        return builder;
    }

    private static void AddApplicationConfiguration(WebApplicationBuilder builder, string[] args)
    {
        var environment = builder.Environment;
        var reload = builder.Configuration.GetValue("hostBuilder:reloadConfigOnChange", true);
        var files = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: reload)
            .AddJsonFile($"appsettings.{environment.EnvironmentName}.json", optional: true, reloadOnChange: reload);

        if (!string.IsNullOrEmpty(environment.ApplicationName))
        {
            var applicationName = environment.ApplicationName
                .Replace(Path.DirectorySeparatorChar, '_')
                .Replace(Path.AltDirectorySeparatorChar, '_');
            files.AddJsonFile($"{applicationName}.settings.json", optional: true, reloadOnChange: reload)
                .AddJsonFile($"{applicationName}.settings.{environment.EnvironmentName}.json", optional: true, reloadOnChange: reload);
            if (environment.IsDevelopment())
            {
                try
                {
                    files.AddUserSecrets(Assembly.Load(new AssemblyName(environment.ApplicationName)), optional: true, reloadOnChange: reload);
                }
                catch (FileNotFoundException)
                {
                    // Match the standard host: an unavailable application assembly has no user secrets.
                }
            }
        }

        // An ordinary ConfigurationBuilder only stages sources. Wrap them before adding them
        // to the live ConfigurationManager, which loads each source immediately.
        files.PreservePipelineExtraDataJson();
        foreach (var source in files.Sources)
            ((IConfigurationBuilder)builder.Configuration).Add(source);

        builder.Configuration.AddEnvironmentVariables();
        if (args.Length > 0)
            builder.Configuration.AddCommandLine(args);
    }

    private static void AddHostingDefaults(WebApplicationBuilder builder)
    {
        // Restore the standard host behavior used by this service after CreateEmptyBuilder.
        if (OperatingSystem.IsWindows())
            builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddConsole().AddDebug().AddEventSourceLogger();
        if (OperatingSystem.IsWindows())
            builder.Logging.AddEventLog();
        builder.Logging.Configure(logging => logging.ActivityTrackingOptions =
            ActivityTrackingOptions.SpanId | ActivityTrackingOptions.TraceId | ActivityTrackingOptions.ParentId);
        builder.Metrics.AddConfiguration(builder.Configuration.GetSection("Metrics"));
        builder.Host.UseDefaultServiceProvider((context, provider) =>
        {
            provider.ValidateScopes = context.HostingEnvironment.IsDevelopment();
            provider.ValidateOnBuild = context.HostingEnvironment.IsDevelopment();
        });

        builder.WebHost.UseKestrel((context, kestrel) =>
            kestrel.Configure(context.Configuration.GetSection("Kestrel"), reloadOnChange: true));
        builder.WebHost.UseIIS().UseIISIntegration();
        if (builder.Environment.IsDevelopment())
            builder.WebHost.UseStaticWebAssets();
        builder.Services.AddRouting();

        builder.Services.AddHostFiltering(_ => { });
        builder.Services.PostConfigure<HostFilteringOptions>(filtering =>
        {
            if (filtering.AllowedHosts is null || filtering.AllowedHosts.Count == 0)
            {
                var hosts = builder.Configuration["AllowedHosts"]?.Split(';', StringSplitOptions.RemoveEmptyEntries);
                filtering.AllowedHosts = hosts is { Length: > 0 } ? hosts : ["*"];
            }
        });
        builder.Services.AddSingleton<IOptionsChangeTokenSource<HostFilteringOptions>>(
            new ConfigurationChangeTokenSource<HostFilteringOptions>(builder.Configuration));
        builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            if (!ForwardingEnabled(builder.Configuration))
                return;
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
        });
        builder.Services.AddTransient<IStartupFilter, GatewayHostingStartupFilter>();
    }

    private static bool ForwardingEnabled(IConfiguration configuration) =>
        string.Equals(configuration["ForwardedHeaders_Enabled"], "true", StringComparison.OrdinalIgnoreCase);

    private sealed class GatewayHostingStartupFilter(IConfiguration configuration, IHostEnvironment environment) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseHostFiltering();
            if (ForwardingEnabled(configuration))
                app.UseForwardedHeaders();
            if (environment.IsDevelopment())
                app.UseDeveloperExceptionPage();
            next(app);
        };
    }
}
