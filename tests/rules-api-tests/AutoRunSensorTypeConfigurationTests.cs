using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunSensorTypeConfigurationTests
{
    [Fact]
    public void Defaults_include_original_types_and_bounded_pacing_and_retries()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().Build();
        using var services = Services(configuration);
        services.GetRequiredService<IStartupValidator>().Validate();
        var options = services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value;

        Assert.Equal(1000, options.RequestDelayMs);
        Assert.Equal(2, options.MaxRetries);
        Assert.Equal(2000, options.RetryDelayMs);
        Assert.Equal("sensorType.keyword", options.RequestFields.SensorType);
        Assert.Equal(11, options.SensorTypeMappings.Count);
        Assert.All(options.SensorTypeMappings, entry => Assert.Equal(entry.Key, entry.Value));
    }

    [Fact]
    public void Configured_catalog_replaces_defaults_and_drives_query_validation()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoRun:Provider:SensorTypeMappings:custom-visible"] = "VIS",
            ["AutoRun:Provider:SensorTypeMappings:custom-radar"] = "SAR"
        }).Build();
        using var services = Services(configuration);
        services.GetRequiredService<IStartupValidator>().Validate();
        var options = services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value;
        var builder = services.GetRequiredService<ProviderQueryBuilder>();

        Assert.Equal(2, options.SensorTypeMappings.Count);
        Assert.Equal("VIS", options.SensorTypeMappings["custom-visible"]);
        Assert.Empty(builder.ValidateSensorTypes(new() { SensorTypes = ["custom-visible"] }));
        Assert.NotEmpty(builder.ValidateSensorTypes(new() { SensorTypes = ["VIS"] }));
    }

    [Fact]
    public void Explicitly_empty_catalog_fails_startup_instead_of_restoring_defaults()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoRun:Provider:SensorTypeMappings"] = null
        }).Build();
        using var services = Services(configuration);

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("SensorTypeMappings", error.Message);
    }

    [Theory]
    [InlineData("RequestDelayMs", "-1")]
    [InlineData("RequestDelayMs", "60001")]
    [InlineData("MaxRetries", "-1")]
    [InlineData("MaxRetries", "6")]
    [InlineData("RetryDelayMs", "0")]
    [InlineData("RetryDelayMs", "60001")]
    public void Invalid_pacing_and_retry_settings_fail_startup(string name, string value)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"AutoRun:Provider:{name}"] = value
        }).Build();
        using var services = Services(configuration);

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(name, error.Message);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(60000, 5, 60000)]
    public void Pacing_and_retry_boundary_values_pass_startup(int delay, int retries, int retryDelay)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoRun:Provider:RequestDelayMs"] = delay.ToString(),
            ["AutoRun:Provider:MaxRetries"] = retries.ToString(),
            ["AutoRun:Provider:RetryDelayMs"] = retryDelay.ToString()
        }).Build();
        using var services = Services(configuration);

        services.GetRequiredService<IStartupValidator>().Validate();

        var options = services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value;
        Assert.Equal(delay, options.RequestDelayMs);
        Assert.Equal(retries, options.MaxRetries);
        Assert.Equal(retryDelay, options.RetryDelayMs);
    }

    [Theory]
    [InlineData("", "VIS")]
    [InlineData("VIS", "")]
    [InlineData("VIS ", "VIS")]
    [InlineData("VIS", " VIS")]
    public void Invalid_catalog_keys_or_values_fail_startup(string key, string value)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().Build();
        using var services = Services(configuration, options => options.SensorTypeMappings = new() { [key] = value });

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("SensorTypeMappings", error.Message);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Catalog_size_is_bounded(int count, bool valid)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().Build();
        using var services = Services(configuration, options => options.SensorTypeMappings =
            Enumerable.Range(0, count).ToDictionary(index => $"type-{index}", _ => "provider-type"));

        if (valid) services.GetRequiredService<IStartupValidator>().Validate();
        else Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Catalog_key_and_value_lengths_are_bounded(int length, bool valid)
    {
        var text = new string('x', length);
        Assert.Equal(valid, SensorTypeCatalog.IsValid(new Dictionary<string, string> { [text] = "VIS" }));
        Assert.Equal(valid, SensorTypeCatalog.IsValid(new Dictionary<string, string> { ["VIS"] = text }));
    }

    private static ServiceProvider Services(IConfiguration configuration, Action<ElasticProviderOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoRuns(configuration);
        if (configure is not null) services.Configure(configure);
        return services.BuildServiceProvider();
    }
}
