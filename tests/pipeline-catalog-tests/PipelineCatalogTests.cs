using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ImagingPipeline.PipelineCatalog.Tests;

public sealed class PipelineCatalogTests
{
    [Fact]
    public void DisabledPipelinesRemainDiscoverableButAreExcludedFromEnabledSelection()
    {
        var catalog = CreateCatalog(new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(),
            Pipelines = [ValidPipeline(), ValidPipeline("future") with { Enabled = false }]
        });

        Assert.Equal(2, catalog.GetAll().Count);
        Assert.Equal("asd", Assert.Single(catalog.GetEnabled()).PipelineId);
        Assert.False(catalog.GetRequired("future").Enabled);
        Assert.Throws<KeyNotFoundException>(() => catalog.GetRequired("unknown"));
    }

    [Fact]
    public void RuleSourcesResolveEachConfiguredIndexAndPreservePipelineOwnership()
    {
        var catalog = CreateCatalog(new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(),
            Pipelines = [ValidPipeline(), ValidPipeline("second")]
        });

        Assert.Equal(new RuleSource("asd-rules", "asd"), catalog.Resolve("asd"));
        Assert.Equal(new RuleSource("second-rules", "second"), catalog.Resolve("second"));
        Assert.Throws<KeyNotFoundException>(() => catalog.Resolve("unknown"));
    }

    [Fact]
    public void SharedIndexAllowsDifferentContractsAndTransportsWithDistinctRuleOwnership()
    {
        var rabbitPipeline = ValidPipeline() with { RulesIndex = "rules-integ" };
        var httpPipeline = WithHttp(ValidPipeline("http-pipeline"), new HttpTransportOptions
        {
            Endpoint = "https://example.test/missions"
        }) with { RulesIndex = "rules-integ", ContractId = "http" };
        var catalog = CreateCatalog(new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(),
            Pipelines = [rabbitPipeline, httpPipeline]
        });

        Assert.Equal(new RuleSource("rules-integ", "asd"), catalog.Resolve("asd"));
        Assert.Equal(new RuleSource("rules-integ", "http-pipeline"), catalog.Resolve("http-pipeline"));
        Assert.Equal("asd", catalog.GetRequired("asd").ContractId);
        Assert.Equal("rabbitmq", catalog.GetRequired("asd").Transport.Kind);
        Assert.Equal("publisher", catalog.GetRequired("asd").Transport.RabbitMq!.Output.QueueName);
        Assert.Equal("http", catalog.GetRequired("http-pipeline").ContractId);
        Assert.Equal("http", catalog.GetRequired("http-pipeline").Transport.Kind);
        Assert.Equal("https://example.test/missions", catalog.GetRequired("http-pipeline").Transport.Http!.Endpoint);
    }

    [Fact]
    public void IntegPipelineNameDoesNotOverrideItsExplicitRulesIndex()
    {
        var catalog = CreateCatalog(new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(),
            Pipelines = [ValidPipeline("integ") with { RulesIndex = "explicitly-configured-rules" }]
        });

        Assert.Equal(new RuleSource("explicitly-configured-rules", "integ"), catalog.Resolve("integ"));
        Assert.Equal("asd", catalog.GetRequired("integ").ContractId);
    }

    [Fact]
    public void CatalogSnapshotCannotBeChangedThroughOptionsOrReturnedCollections()
    {
        var original = ValidPipeline();
        var options = new PipelineCatalogOptions { RabbitMqConnections = Connections(), Pipelines = [original] };
        var catalog = CreateCatalog(options);

        options.Pipelines[0] = original with
        {
            Enabled = false,
            RulesIndex = "changed-index",
            Transport = original.Transport with
            {
                RabbitMq = original.Transport.RabbitMq! with { Output = new() { QueueName = "changed-queue" } }
            }
        };
        options.Pipelines.Clear();

        Assert.Equal("publisher", catalog.GetRequired("asd").Transport.RabbitMq!.Output.QueueName);
        Assert.True(Assert.Single(catalog.GetEnabled()).Enabled);
        Assert.Equal(new RuleSource("asd-rules", "asd"), catalog.Resolve("asd"));
        var exposedList = Assert.IsAssignableFrom<IList<PipelineDefinition>>(catalog.GetAll());
        Assert.Throws<NotSupportedException>(() => exposedList.Clear());
    }

    [Theory]
    [InlineData("pipeline-id")]
    [InlineData("unknown-contract")]
    [InlineData("missing-contract")]
    [InlineData("rules-index")]
    [InlineData("transport-kind")]
    [InlineData("missing-transport")]
    [InlineData("rabbit-connection")]
    [InlineData("rabbit-queue")]
    [InlineData("rabbit-null-queue")]
    [InlineData("mixed-settings")]
    [InlineData("http-relative-endpoint")]
    [InlineData("http-credentials")]
    [InlineData("http-method")]
    [InlineData("http-timeout")]
    public void InvalidDisabledPipelineIsRejected(string invalidField)
    {
        var definition = ValidPipeline() with { Enabled = false };
        var invalid = invalidField switch
        {
            "pipeline-id" => definition with { PipelineId = " " },
            "unknown-contract" => definition with { ContractId = "unknown" },
            "missing-contract" => definition with { ContractId = " " },
            "rules-index" => definition with { RulesIndex = " " },
            "transport-kind" => definition with { Transport = new() { Kind = "kafka" } },
            "missing-transport" => definition with { Transport = null! },
            "rabbit-connection" => WithRabbit(definition, new() { Output = new() { QueueName = "publisher" } }),
            "rabbit-queue" => WithRabbit(definition, new() { ConnectionRef = "asd-broker" }),
            "rabbit-null-queue" => WithRabbit(definition, new() { ConnectionRef = "asd-broker", Output = null! }),
            "mixed-settings" => definition with { Transport = definition.Transport with { Http = new() } },
            "http-relative-endpoint" => WithHttp(definition, new() { Endpoint = "/missions" }),
            "http-credentials" => WithHttp(definition, new() { Endpoint = "https://user:secret@example.test/missions" }),
            "http-method" => WithHttp(definition, new() { Endpoint = "https://example.test/missions", Method = "POST\r\n" }),
            "http-timeout" => WithHttp(definition, new() { Endpoint = "https://example.test/missions", TimeoutSeconds = 0 }),
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<OptionsValidationException>(() =>
            CreateCatalog(new PipelineCatalogOptions { RabbitMqConnections = Connections(), Pipelines = [invalid] }));
    }

    [Theory]
    [InlineData("duplicate-pipeline")]
    [InlineData("index-expression")]
    public void DuplicatePipelineIdentityOrInvalidIndexExpressionIsRejected(string scenario)
    {
        var options = new PipelineCatalogOptions { RabbitMqConnections = Connections(), Pipelines = [ValidPipeline()] };
        switch (scenario)
        {
            case "duplicate-pipeline": options.Pipelines.Add(ValidPipeline()); break;
            case "index-expression": options.Pipelines[0] = ValidPipeline() with { RulesIndex = "rules-*" }; break;
        }

        Assert.Throws<OptionsValidationException>(() => CreateCatalog(options));
    }

    [Fact]
    public void RegisteredButUnusedContractsAndValidHttpSettingsAreAccepted()
    {
        var definition = WithHttp(ValidPipeline(), new HttpTransportOptions
        {
            Endpoint = "https://example.test/missions",
            Method = "POST",
            TimeoutSeconds = 30
        });
        var catalog = CreateCatalog(new PipelineCatalogOptions { RabbitMqConnections = Connections(), Pipelines = [definition] });

        Assert.Equal("http", catalog.GetRequired("asd").Transport.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("RULES")]
    [InlineData("cluster:rules")]
    [InlineData("-rules")]
    [InlineData("_rules")]
    [InlineData("+rules")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("rules\u0001")]
    [InlineData("rules|other")]
    [InlineData("rules,other")]
    [InlineData("<rules>")]
    [InlineData("rules?other")]
    [InlineData("rules/other")]
    [InlineData("rules\\other")]
    [InlineData("rules*other")]
    [InlineData("rules#other")]
    [InlineData("rules\"other")]
    [InlineData("rules other")]
    [InlineData("rules\tother")]
    public void MalformedIndexReferencesAreRejected(string indexName)
    {
        var options = new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(),
            Pipelines = [ValidPipeline() with { RulesIndex = indexName }]
        };

        Assert.Throws<OptionsValidationException>(() => CreateCatalog(options));
    }

    [Theory]
    [InlineData("rules")]
    [InlineData("asd-rules-prod")]
    [InlineData("rules_integ.v2")]
    [InlineData(".hidden-rules")]
    [InlineData("כללים")]
    public void ValidLiteralRuleSourcesRemainUsableWithoutAnElasticsearchConnection(string indexName)
    {
        var catalog = CreateCatalog(new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(), Pipelines = [ValidPipeline() with { RulesIndex = indexName }]
        });
        Assert.Equal(new RuleSource(indexName, "asd"), catalog.Resolve("asd"));
    }

    [Fact]
    public void ControlCharactersCannotBecomePipelineOrConnectionIdentities()
    {
        var options = new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(), Pipelines = [ValidPipeline() with { PipelineId = "as\u0001d" }]
        };
        var pipelineError = Assert.Throws<OptionsValidationException>(() => CreateCatalog(options));
        Assert.Contains("PipelineId", pipelineError.Message, StringComparison.Ordinal);

        var invalidReference = "asd\u0001broker";
        options.RabbitMqConnections = new()
        {
            [invalidReference] = new() { Hostname = "localhost", Username = "guest", Password = "guest" }
        };
        options.Pipelines = [WithRabbit(ValidPipeline(), new()
        {
            ConnectionRef = invalidReference, Output = new() { QueueName = "publisher" }
        })];
        var connectionError = Assert.Throws<OptionsValidationException>(() => CreateCatalog(options));
        Assert.Contains("control characters", connectionError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a%b")]
    [InlineData(".")]
    [InlineData("..")]
    public void PipelineIdsMustBeUnambiguousSingleUrlPathSegments(string pipelineId)
    {
        var error = Assert.Throws<OptionsValidationException>(() => CreateCatalog(new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(), Pipelines = [ValidPipeline() with { PipelineId = pipelineId }]
        }));
        Assert.Contains("PipelineId", error.Message, StringComparison.Ordinal);
        Assert.Contains("URL path segment", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("asd-integ")]
    [InlineData("ALGO_1")]
    [InlineData("algo.pipeline~variant")]
    [InlineData("0")]
    public void PipelineIdsAcceptUrlUnreservedAsciiCharacters(string pipelineId)
    {
        var catalog = CreateCatalog(new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(), Pipelines = [ValidPipeline() with { PipelineId = pipelineId }]
        });
        Assert.Equal(pipelineId, catalog.GetRequired(pipelineId).PipelineId);
    }

    [Fact]
    public void IndexNameLimitUsesUtf8Bytes()
    {
        var options = new PipelineCatalogOptions
        {
            RabbitMqConnections = Connections(),
            Pipelines = [ValidPipeline() with { RulesIndex = new string('\u05d0', 128) }]
        };

        Assert.Throws<OptionsValidationException>(() => CreateCatalog(options));
        options.Pipelines[0] = ValidPipeline() with { RulesIndex = new string('a', 255) };
        Assert.Equal(options.Pipelines[0].RulesIndex, CreateCatalog(options).Resolve("asd").IndexName);
        options.Pipelines[0] = ValidPipeline() with { RulesIndex = new string('\u05d0', 127) + "a" };
        Assert.Equal(options.Pipelines[0].RulesIndex, CreateCatalog(options).Resolve("asd").IndexName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSectionOrEmptyPipelineListFailsHostStartup(bool includeSection)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        if (includeSection)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PipelineCatalog:Pipelines"] = null
            });
        }

        builder.Services.AddSingleton(CreateRegistry());
        builder.Services.AddPipelineCatalog(builder.Configuration);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData("PipelineCatalog:Environment", "prod", "Environment")]
    [InlineData("PipelineCatalog:IntegrationRulesIndex", "rules-integ", "IntegrationRulesIndex")]
    [InlineData("PipelineCatalog:Pipelines:0:ProductionRulesIndex", "asd-rules-prod", "ProductionRulesIndex")]
    [InlineData("PipelineCatalog:Pipelines:0:RuelsIndex", "rules-integ", "RuelsIndex")]
    [InlineData("PipelineCatalog:Pipelines:0:DisplayName", "ASD", "DisplayName")]
    public async Task RemovedOrMisspelledConfigurationKeysFailHostStartup(
        string key, string value, string invalidProperty)
    {
        var configuration = ValidConfiguration();
        configuration[key] = value;
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddSingleton(CreateRegistry());
        builder.Services.AddPipelineCatalog(builder.Configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains(invalidProperty, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStartupBindsConfigAndUsesOneCatalogForSelectionAndRuleSources()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(ValidConfiguration());
        builder.Services.AddSingleton(CreateRegistry());
        builder.Services.AddPipelineCatalog(builder.Configuration);
        using var host = builder.Build();

        await host.StartAsync();
        var catalog = host.Services.GetRequiredService<IPipelineCatalog>();
        var resolver = host.Services.GetRequiredService<IRuleSourceResolver>();
        Assert.Same(catalog, resolver);
        Assert.Equal("asd", Assert.Single(catalog.GetEnabled()).PipelineId);
        Assert.Equal(new RuleSource("rules-integ", "asd"), resolver.Resolve("asd"));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("PipelineCatalog:Pipelines:0:ContractId", "unknown")]
    [InlineData("PipelineCatalog:Pipelines:0:Transport:Kind", "unsupported")]
    [InlineData("PipelineCatalog:Pipelines:0:Transport:RabbitMq:Output:QueueName", "")]
    [InlineData("PipelineCatalog:Pipelines:0:Transport:RabbitMq:Output:QueueName", "amq.reserved")]
    [InlineData("PipelineCatalog:Pipelines:0:RulesIndex", "RULES")]
    [InlineData("PipelineCatalog:Pipelines:0:RulesIndex", "rules-*")]
    public async Task InvalidDisabledConfigurationFailsHostStartupEvenWhenCatalogIsNotResolved(
        string key, string value)
    {
        var configuration = ValidConfiguration();
        configuration["PipelineCatalog:Pipelines:0:Enabled"] = "false";
        configuration[key] = value;
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddSingleton(CreateRegistry());
        builder.Services.AddPipelineCatalog(builder.Configuration);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    private static PipelineCatalog CreateCatalog(PipelineCatalogOptions options) =>
        new(Options.Create(options), CreateRegistry(), NullLogger<PipelineCatalog>.Instance);

    private static IPipelineContractRegistry CreateRegistry()
    {
        var active = new Mock<IPipelineContract>();
        active.SetupGet(contract => contract.ContractId).Returns("asd");
        var unused = new Mock<IPipelineContract>();
        unused.SetupGet(contract => contract.ContractId).Returns("unused");
        var http = new Mock<IPipelineContract>();
        http.SetupGet(contract => contract.ContractId).Returns("http");
        return new PipelineContractRegistry([active.Object, http.Object, unused.Object]);
    }

    private static PipelineDefinition ValidPipeline(string pipelineId = "asd") => new()
    {
        PipelineId = pipelineId,
        Enabled = true,
        ContractId = "asd",
        RulesIndex = $"{pipelineId}-rules",
        Transport = new PipelineTransportOptions
        {
            Kind = "rabbitmq",
            RabbitMq = new RabbitMqTransportOptions
            {
                ConnectionRef = "asd-broker",
                Output = new RabbitMqQueueOptions { QueueName = "publisher" }
            }
        }
    };

    private static PipelineDefinition WithRabbit(PipelineDefinition definition, RabbitMqTransportOptions options) =>
        definition with { Transport = new() { Kind = "rabbitmq", RabbitMq = options } };

    private static PipelineDefinition WithHttp(PipelineDefinition definition, HttpTransportOptions options) =>
        definition with { Transport = new() { Kind = "http", Http = options } };

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["PipelineCatalog:RabbitMqConnections:asd-broker:Hostname"] = "localhost",
        ["PipelineCatalog:RabbitMqConnections:asd-broker:Username"] = "guest",
        ["PipelineCatalog:RabbitMqConnections:asd-broker:Password"] = "guest",
        ["PipelineCatalog:Pipelines:0:PipelineId"] = "asd",
        ["PipelineCatalog:Pipelines:0:Enabled"] = "true",
        ["PipelineCatalog:Pipelines:0:ContractId"] = "asd",
        ["PipelineCatalog:Pipelines:0:RulesIndex"] = "rules-integ",
        ["PipelineCatalog:Pipelines:0:Transport:Kind"] = "rabbitmq",
        ["PipelineCatalog:Pipelines:0:Transport:RabbitMq:ConnectionRef"] = "asd-broker",
        ["PipelineCatalog:Pipelines:0:Transport:RabbitMq:Output:ExchangeSettings:ExchangeName"] = "",
        ["PipelineCatalog:Pipelines:0:Transport:RabbitMq:Output:QueueName"] = "publisher"
    };

    private static Dictionary<string, RabbitMqConnectionOptions> Connections() => new(StringComparer.Ordinal)
    {
        ["asd-broker"] = new() { Hostname = "localhost", Username = "guest", Password = "guest" }
    };
}
