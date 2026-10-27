using System.Text;
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
    public void Shipped_configuration_without_a_catalog_override_keeps_default_types()
    {
        using var configuration = (ConfigurationRoot)ShippedConfiguration().Build();
        using var services = Services(configuration);
        services.GetRequiredService<IStartupValidator>().Validate();
        var mappings = services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value.SensorTypeMappings;

        Assert.Equal(11, mappings.Count);
        Assert.Contains("EO", mappings.Keys);
        Assert.All(mappings, entry => Assert.Equal(entry.Key, entry.Value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Catalog_override_on_shipped_configuration_does_not_keep_unselected_default_types(bool useJson)
    {
        var configurationBuilder = ShippedConfiguration();
        if (useJson)
        {
            configurationBuilder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
                { "AutoRun": { "Provider": { "SensorTypeMappings": { "VIS": "visible-provider", "PAN": "PAN" } } } }
                """)));
        }
        else
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AutoRun:Provider:SensorTypeMappings:VIS"] = "visible-provider",
                ["AutoRun:Provider:SensorTypeMappings:PAN"] = "PAN"
            });
        }
        using var configuration = (ConfigurationRoot)configurationBuilder.Build();
        using var services = Services(configuration);
        services.GetRequiredService<IStartupValidator>().Validate();
        var mappings = services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value.SensorTypeMappings;
        var builder = services.GetRequiredService<ProviderQueryBuilder>();

        Assert.Equal(new[] { "PAN", "VIS" }, mappings.Keys.Order(StringComparer.Ordinal));
        Assert.NotEmpty(builder.ValidateSensorTypes(new() { SensorTypes = ["EO"] }));
        Assert.Throws<ArgumentException>(() => builder.Build(new()
        {
            Mode = AutoRunSourceMode.Filters, Filters = new() { SensorTypes = ["EO"] }
        }, []));

        var query = Assert.Single(builder.Build(new()
        {
            Mode = AutoRunSourceMode.Filters, Filters = new() { SensorTypes = ["VIS", "PAN"] }
        }, []));
        var condition = Assert.Single(query.GetProperty("filter").GetProperty("logical_operators")
            .GetProperty("AND").EnumerateArray()).GetProperty("match").GetProperty("sensorType.keyword");
        Assert.Equal(new[] { "visible-provider", "PAN" }, condition.GetProperty("values").EnumerateArray()
            .Select(value => value.GetString()));
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

    [Fact]
    public void Higher_priority_catalog_replaces_lower_priority_keys_instead_of_merging()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonStream(Json("""
                { "AutoRun": { "Provider": { "SensorTypeMappings": { "EO": "EO", "VIS": "VIS", "PAN": "PAN" } } } }
                """))
            .AddJsonStream(Json("""
                { "AutoRun": { "Provider": { "SensorTypeMappings": { "VIS": "visible", "new-type": "new-provider-type" } } } }
                """))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AutoRun:Provider:RequestDelayMs"] = "150"
            }).Build();
        using var services = Services(configuration);
        services.GetRequiredService<IStartupValidator>().Validate();
        var options = services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value;

        Assert.Equal(new[] { "VIS", "new-type" }, options.SensorTypeMappings.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("visible", options.SensorTypeMappings["VIS"]);
        Assert.Equal("new-provider-type", options.SensorTypeMappings["new-type"]);
        Assert.Equal(150, options.RequestDelayMs);
        Assert.NotEmpty(services.GetRequiredService<ProviderQueryBuilder>()
            .ValidateSensorTypes(new() { SensorTypes = ["EO"] }));
    }

    [Fact]
    public void Configuration_manager_uses_only_the_highest_priority_catalog()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(Json("""
            { "AutoRun": { "Provider": { "SensorTypeMappings": { "EO": "EO", "VIS": "VIS" } } } }
            """));
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoRun:Provider:SensorTypeMappings:SAR"] = "radar"
        });
        using var services = Services(configuration);
        services.GetRequiredService<IStartupValidator>().Validate();
        var mappings = services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value.SensorTypeMappings;

        Assert.Equal(new KeyValuePair<string, string>("SAR", "radar"), Assert.Single(mappings));
    }

    [Fact]
    public void Environment_catalog_replaces_the_complete_json_catalog()
    {
        var prefix = $"AutoRunSensorTypesTest_{Guid.NewGuid():N}_";
        var variable = prefix + "AutoRun__Provider__SensorTypeMappings__custom-visible";
        Environment.SetEnvironmentVariable(variable, "VIS");
        try
        {
            using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
                .AddJsonStream(Json("""
                    { "AutoRun": { "Provider": { "SensorTypeMappings": { "EO": "EO", "VIS": "VIS" } } } }
                    """))
                .AddEnvironmentVariables(prefix).Build();
            using var services = Services(configuration);
            services.GetRequiredService<IStartupValidator>().Validate();
            var mappings = services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value.SensorTypeMappings;

            Assert.Equal(new KeyValuePair<string, string>("custom-visible", "VIS"), Assert.Single(mappings));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"VIS\"")]
    [InlineData("{\"VIS\": {\"nested\": \"VIS\"}}")]
    public void Invalid_higher_priority_catalog_does_not_fall_back_to_lower_priority_catalog(string catalog)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonStream(Json("""
                { "AutoRun": { "Provider": { "SensorTypeMappings": { "EO": "EO" } } } }
                """))
            .AddJsonStream(Json("{\"AutoRun\":{\"Provider\":{\"SensorTypeMappings\":" + catalog + "}}}"))
            .Build();
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

    private static MemoryStream Json(string json) => new(Encoding.UTF8.GetBytes(json));

    private static IConfigurationBuilder ShippedConfiguration() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

    private static ServiceProvider Services(IConfiguration configuration, Action<ElasticProviderOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoRuns(configuration);
        if (configure is not null) services.Configure(configure);
        return services.BuildServiceProvider();
    }
}
