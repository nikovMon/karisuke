using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Rules.Api.Health;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

internal sealed class RulesApiFactory : WebApplicationFactory<Program>
{
    private readonly IElasticsearchDocumentClient _client;
    private readonly bool _isHealthy;
    private readonly ILoggerProvider? _loggerProvider;

    public RulesApiFactory(
        IElasticsearchDocumentClient client,
        bool isHealthy = true,
        ILoggerProvider? loggerProvider = null)
    {
        _client = client;
        _isHealthy = isHealthy;
        _loggerProvider = loggerProvider;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Observability:Enabled", bool.FalseString);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            if (_loggerProvider is not null)
            {
                logging.AddProvider(_loggerProvider);
            }
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IElasticsearchDocumentClient>();
            services.AddSingleton<IElasticsearchDocumentClient>(_client);
            services.RemoveAll<IElasticsearchHealthProbe>();
            services.AddSingleton<IElasticsearchHealthProbe>(new StubElasticsearchHealthProbe(_isHealthy));
        });
    }

    private sealed class StubElasticsearchHealthProbe(bool isHealthy) : IElasticsearchHealthProbe
    {
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(isHealthy);
    }
}
