using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class SensorCatalogConfigurationTests
{
    [Fact]
    public void Defaults_disable_catalog_and_refresh_daily_when_configured()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().Build();
        using var services = Services(configuration);
        services.GetRequiredService<IStartupValidator>().Validate();
        var options = services.GetRequiredService<IOptions<SensorCatalogOptions>>().Value;
        Assert.Equal("", options.BaseUrl);
        Assert.Equal("/sensors/api/v1/sensors/list/sensor/", options.Path);
        Assert.Equal(1440, options.RefreshIntervalMinutes);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.Same(services.GetRequiredService<ISensorNameCatalog>(),
            services.GetRequiredService<SensorNameCatalog>());
    }

    [Fact]
    public void Configuration_binds_and_production_handler_rejects_redirects()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoRun:SensorCatalog:BaseUrl"] = "https://sensors.example",
            ["AutoRun:SensorCatalog:Path"] = "/custom/sensors",
            ["AutoRun:SensorCatalog:RefreshIntervalMinutes"] = "120",
            ["AutoRun:SensorCatalog:TimeoutSeconds"] = "15"
        }).Build();
        using var services = Services(configuration);
        services.GetRequiredService<IStartupValidator>().Validate();
        var options = services.GetRequiredService<IOptions<SensorCatalogOptions>>().Value;
        Assert.Equal("https://sensors.example", options.BaseUrl);
        Assert.Equal("/custom/sensors", options.Path);
        Assert.Equal(120, options.RefreshIntervalMinutes);
        Assert.Equal(15, options.TimeoutSeconds);
        var handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(SensorNameCatalog.HttpClientName);
        while (handler is DelegatingHandler wrapper) handler = wrapper.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }

    [Theory]
    [InlineData("BaseUrl", "file:///sensors")]
    [InlineData("BaseUrl", "https://user:secret@sensors.example")]
    [InlineData("BaseUrl", "https://sensors.example?token=secret")]
    [InlineData("BaseUrl", "https://sensors.example#fragment")]
    [InlineData("BaseUrl", "sensors.example")]
    [InlineData("Path", "https://other.example/sensors")]
    [InlineData("Path", "//other.example/sensors")]
    [InlineData("Path", "/\\other.example/sensors")]
    [InlineData("Path", "")]
    [InlineData("RefreshIntervalMinutes", "0")]
    [InlineData("RefreshIntervalMinutes", "43201")]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("TimeoutSeconds", "86401")]
    public void Invalid_configuration_fails_startup_without_echoing_values(string name, string value)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"AutoRun:SensorCatalog:{name}"] = value
        }).Build();
        using var services = Services(configuration);
        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("Sensor catalog", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    private static ServiceProvider Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoRuns(configuration);
        return services.BuildServiceProvider();
    }
}
