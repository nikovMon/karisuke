using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace ImagingPipeline.Observability.Tests;

public sealed class PrometheusMetricsTests
{
    [Fact]
    public async Task EnabledPrometheus_MapsConfiguredScrapePath()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Observability:Metrics:Prometheus:Enabled"] = "true",
            ["Observability:Metrics:Prometheus:Path"] = "/internal/metrics",
            ["Observability:Metrics:Prometheus:Port"] = "9464"
        });
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);

        await using var app = builder.Build();
        app.MapImagingPipelinePrometheusScrapingEndpoint();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/internal/metrics", routes);
        Assert.DoesNotContain("/metrics", routes);
    }

    [Fact]
    public async Task DisabledPrometheus_DoesNotMapScrapePath()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Observability:Metrics:Prometheus:Enabled"] = "false"
        });
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);

        await using var app = builder.Build();
        app.MapImagingPipelinePrometheusScrapingEndpoint();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText);

        Assert.DoesNotContain("/metrics", routes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("not-a-port")]
    [InlineData(" 9464 ")]
    public void InvalidPrometheusPort_FailsConfiguration(string port)
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Observability:Metrics:Prometheus:Port"] = port
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway));

        Assert.Contains(
            "Observability:Metrics:Prometheus:Port",
            error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("metrics")]
    [InlineData("/")]
    [InlineData("/metrics?format=openmetrics")]
    [InlineData("/metrics#fragment")]
    [InlineData("/metrics/{*path}")]
    public void InvalidPrometheusPath_FailsConfiguration(string path)
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Observability:Metrics:Prometheus:Path"] = path
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway));

        Assert.Contains(
            "Observability:Metrics:Prometheus:Path",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPrometheusEnabledValue_FailsConfiguration()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Observability:Metrics:Prometheus:Enabled"] = "yes"
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway));

        Assert.Contains(
            "Observability:Metrics:Prometheus:Enabled",
            error.Message,
            StringComparison.Ordinal);
    }

    private static WebApplicationBuilder CreateBuilder(
        IReadOnlyDictionary<string, string?> overrides)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        var configuration = new Dictionary<string, string?>
        {
            ["Observability:Enabled"] = "true",
            ["Observability:ServiceVersion"] = "1.0.0-test",
            ["Observability:DeploymentEnvironment"] = "test",
            ["Observability:Traces:Enabled"] = "false",
            ["Observability:Metrics:Enabled"] = "true",
            ["Observability:Logs:Enabled"] = "false"
        };

        foreach (var pair in overrides)
        {
            configuration[pair.Key] = pair.Value;
        }

        builder.Configuration.AddInMemoryCollection(configuration);
        return builder;
    }
}
