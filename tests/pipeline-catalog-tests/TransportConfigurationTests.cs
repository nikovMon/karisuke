using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ImagingPipeline.PipelineCatalog.Tests;

public sealed class TransportConfigurationTests
{
    [Theory]
    [InlineData("source", "ingress.example.test", 5672)]
    [InlineData("output", "egress.example.test", 5673)]
    public void ReferencesCanSelectTheSameOrADifferentBroker(string selectedRef, string hostname, int port)
    {
        var options = OptionsFor(Rabbit("asd", selectedRef));
        var catalog = Catalog(options);
        var resolver = (IRabbitMqConnectionResolver)catalog;

        var source = resolver.GetRequired("source");
        var output = resolver.GetRequired(catalog.GetRequired("asd").Transport.RabbitMq!.ConnectionRef);
        Assert.Equal("ingress.example.test", source.Hostname);
        Assert.Equal(hostname, output.Hostname);
        Assert.Equal(port, output.Port);
        Assert.Throws<KeyNotFoundException>(() => resolver.GetRequired("unknown"));
    }

    [Fact]
    public void UnknownOutputReferenceFailsEvenForADisabledPipeline()
    {
        var options = OptionsFor(Rabbit("asd", "unknown") with { Enabled = false });
        var exception = Assert.Throws<OptionsValidationException>(() => Catalog(options));
        Assert.Contains("ConnectionRef", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hostname")]
    [InlineData("port")]
    [InlineData("username")]
    [InlineData("password")]
    [InlineData("virtual-host")]
    public void UnusedInvalidConnectionDefinitionsAreRejectedWithoutCredentialValues(string field)
    {
        var options = OptionsFor(Rabbit("asd", "source"));
        var valid = new RabbitMqConnectionOptions
        {
            Hostname = "broker.example.test", Username = "private-user", Password = "private-password"
        };
        options.RabbitMqConnections["unused"] = field switch
        {
            "hostname" => valid with { Hostname = "https://broker.example.test/path" },
            "port" => valid with { Port = 0 },
            "username" => valid with { Username = "" },
            "password" => valid with { Password = "" },
            "virtual-host" => valid with { VirtualHost = "" },
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<OptionsValidationException>(() => Catalog(options));
        Assert.DoesNotContain("private-user", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-password", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-password", valid.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultExchangeRoutesToQueueWhileCustomExchangePreservesEmptyRoutingKey()
    {
        var queue = new RabbitMqQueueOptions { QueueName = "work" };
        Assert.Equal("work", queue.GetEffectiveRoutingKey());
        var custom = queue with
        {
            ExchangeSettings = new()
            {
                ShouldBindToExchange = true, ExchangeName = "fanout-work", ExchangeType = "fanout",
                RoutingKey = ""
            }
        };

        var errors = new List<string>();
        RabbitMqOptionsValidation.ValidateQueue(custom, "Input", errors);
        Assert.Empty(errors);
        var normalized = RabbitMqOptionsValidation.NormalizeQueue(custom);
        Assert.Equal("", normalized.GetEffectiveRoutingKey());
        Assert.True(normalized.ExchangeSettings.ShouldBindToExchange);
        Assert.Equal("fanout-work", normalized.ExchangeSettings.ExchangeName);
        Assert.Equal("fanout", normalized.ExchangeSettings.ExchangeType);
    }

    [Fact]
    public void BindingRequiresNamedExchangeAndBindingArgumentsRequireBindingEnabled()
    {
        var queue = new RabbitMqQueueOptions
        {
            QueueName = "work", ExchangeSettings = new() { ShouldBindToExchange = true }
        };
        var errors = new List<string>();
        RabbitMqOptionsValidation.ValidateQueue(queue, "Input", errors);
        Assert.Contains(errors, error => error.Contains("requires a named exchange", StringComparison.Ordinal));

        queue = queue with
        {
            ExchangeSettings = new()
            {
                ExchangeName = "work", BindingArguments = new() { ["x-match"] = "all" }
            }
        };
        errors.Clear();
        RabbitMqOptionsValidation.ValidateQueue(queue, "Input", errors);
        Assert.Contains(errors, error => error.Contains("BindingArguments", StringComparison.Ordinal));
    }

    [Fact]
    public void ArgumentsNormalizeScalarsAndPreserveStringTypedRabbitArguments()
    {
        var arguments = RabbitMqOptionsValidation.NormalizeArguments(new Dictionary<string, object?>
        {
            ["x-message-ttl"] = "30000",
            ["x-max-length-bytes"] = "5000000000",
            ["flag"] = "true",
            ["ratio"] = "1.25",
            ["x-dead-letter-routing-key"] = "123",
            ["x-dead-letter-exchange"] = "true",
            ["x-match"] = "all",
            ["text"] = "unchanged",
            ["integer"] = 7,
            ["long"] = 8L,
            ["double"] = 2.5,
            ["boolean"] = false
        }, "Arguments");

        Assert.Equal(30000, Assert.IsType<int>(arguments["x-message-ttl"]));
        Assert.Equal(5000000000, Assert.IsType<long>(arguments["x-max-length-bytes"]));
        Assert.True(Assert.IsType<bool>(arguments["flag"]));
        Assert.Equal(1.25, Assert.IsType<double>(arguments["ratio"]));
        Assert.Equal("123", Assert.IsType<string>(arguments["x-dead-letter-routing-key"]));
        Assert.Equal("true", Assert.IsType<string>(arguments["x-dead-letter-exchange"]));
        Assert.Equal("all", arguments["x-match"]);
        Assert.Equal("unchanged", arguments["text"]);
        Assert.IsType<int>(arguments["integer"]);
        Assert.IsType<long>(arguments["long"]);
        Assert.IsType<double>(arguments["double"]);
        Assert.False(Assert.IsType<bool>(arguments["boolean"]));
    }

    [Fact]
    public void NestedAndUnsupportedArgumentValuesAreRejected()
    {
        foreach (var invalid in new object?[] { null, new object(), new[] { 1 }, new Dictionary<string, object?>(), 1m, double.NaN })
        {
            Assert.Throws<ArgumentException>(() => RabbitMqOptionsValidation.NormalizeArguments(
                new Dictionary<string, object?> { ["invalid"] = invalid }, "Arguments"));
        }
    }

    [Fact]
    public void CatalogDefensivelyCopiesQueueArgumentsHeadersAndConnectionDictionary()
    {
        var rabbit = Rabbit("asd", "output");
        rabbit.Transport.RabbitMq!.Output.Arguments["x-message-ttl"] = "1000";
        var http = Http(new() { ["X-Mode"] = "original" });
        var options = OptionsFor(rabbit, http);
        var catalog = Catalog(options);
        rabbit.Transport.RabbitMq!.Output.Arguments["x-message-ttl"] = "2000";
        http.Transport.Http!.Headers["X-Mode"] = "changed";
        options.RabbitMqConnections.Clear();
        catalog.GetRequired("asd").Transport.RabbitMq!.Output.Arguments.Clear();
        catalog.GetAll().Single(pipeline => pipeline.PipelineId == "http").Transport.Http!.Headers.Clear();

        Assert.Equal(1000, catalog.GetRequired("asd").Transport.RabbitMq!.Output.Arguments["x-message-ttl"]);
        Assert.Equal("original", catalog.GetRequired("http").Transport.Http!.Headers["X-Mode"]);
        Assert.Equal("egress.example.test", ((IRabbitMqConnectionResolver)catalog).GetRequired("output").Hostname);
    }

    [Fact]
    public void HttpPreservesQueryAndHeadersAndNeedsNoBrokerConnections()
    {
        var options = new PipelineCatalogOptions { Pipelines = [Http(new() { ["Authorization"] = "Bearer token" })] };
        var http = Catalog(options).GetRequired("http").Transport.Http!;
        Assert.Equal("https://example.test/path?mode=one&return=%2Ffoo", http.Endpoint);
        Assert.Equal("Bearer token", http.Headers["Authorization"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(HttpTransportOptions.MaximumTimerSeconds + 1)]
    public void HttpTimeoutMustBePositiveAndRepresentable(int timeoutSeconds)
    {
        var options = OptionsFor(Http(new(), timeoutSeconds));

        var exception = Assert.Throws<OptionsValidationException>(() => Catalog(options));

        Assert.Contains("TimeoutSeconds", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(HttpTransportOptions.MaximumTimerSeconds)]
    public void HttpTimeoutRetainsValidBoundaryValues(int timeoutSeconds)
    {
        var catalog = Catalog(OptionsFor(Http(new(), timeoutSeconds)));

        Assert.Equal(timeoutSeconds, catalog.GetRequired("http").Transport.Http!.TimeoutSeconds);
    }

    [Theory]
    [InlineData("Host", "example.test")]
    [InlineData("content-type", "application/json")]
    [InlineData("Content-Length", "1")]
    [InlineData("Transfer-Encoding", "chunked")]
    [InlineData("Connection", "close")]
    [InlineData("TE", "trailers")]
    [InlineData("Trailer", "X-Trailer")]
    [InlineData("Upgrade", "websocket")]
    [InlineData("Keep-Alive", "timeout=5")]
    [InlineData("Proxy-Connection", "keep-alive")]
    [InlineData("bad name", "value")]
    [InlineData("X-Custom", "value\r\nInjected: value")]
    [InlineData("X-Custom", "value\0suffix")]
    public void HttpManagedOrMalformedHeadersFailValidation(string name, string value)
    {
        var options = OptionsFor(Http(new() { [name] = value }));
        Assert.Throws<OptionsValidationException>(() => Catalog(options));
    }

    [Theory]
    [InlineData("Arguments")]
    [InlineData("ExchangeSettings:Arguments")]
    [InlineData("ExchangeSettings:BindingArguments")]
    public async Task NestedArgumentConfigurationCannotBeSilentlyLostByBinding(string section)
    {
        var settings = ValidConfiguration();
        var output = "PipelineCatalog:Pipelines:0:Transport:RabbitMq:Output";
        settings[$"{output}:ExchangeSettings:ShouldBindToExchange"] = "true";
        settings[$"{output}:ExchangeSettings:ExchangeName"] = "work-x";
        settings[$"{output}:{section}:nested:child"] = "value";
        using var host = BuildHost(settings);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("ErrorOnUnknownConfiguration", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("child", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostBindsOutputTopologyNormalizesArgumentsAndResolvesConnection()
    {
        var settings = ValidConfiguration();
        var prefix = "PipelineCatalog:Pipelines:0:Transport:RabbitMq:Output";
        settings[$"{prefix}:Arguments:x-message-ttl"] = "1000";
        settings[$"{prefix}:Arguments:x-dead-letter-routing-key"] = "123";
        settings[$"{prefix}:ExchangeSettings:ShouldBindToExchange"] = "true";
        settings[$"{prefix}:ExchangeSettings:ExchangeName"] = "work-x";
        settings[$"{prefix}:ExchangeSettings:ExchangeType"] = "headers";
        settings[$"{prefix}:ExchangeSettings:BindingArguments:x-match"] = "all";
        using var host = BuildHost(settings);
        await host.StartAsync();
        var catalog = host.Services.GetRequiredService<IPipelineCatalog>();
        var connections = host.Services.GetRequiredService<IRabbitMqConnectionResolver>();
        Assert.Same(catalog, connections);
        Assert.Equal("localhost", connections.GetRequired("source").Hostname);
        var queue = catalog.GetRequired("asd").Transport.RabbitMq!.Output;
        Assert.Equal(1000, queue.Arguments["x-message-ttl"]);
        Assert.Equal("123", queue.Arguments["x-dead-letter-routing-key"]);
        Assert.Equal("all", queue.ExchangeSettings.BindingArguments["x-match"]);
        Assert.Equal("", queue.GetEffectiveRoutingKey());
        await host.StopAsync();
    }

    private static PipelineDefinition Rabbit(string id, string connectionRef) => new()
    {
        PipelineId = id, Enabled = true, ContractId = "asd", RulesIndex = "rules",
        Transport = new()
        {
            Kind = "rabbitmq", RabbitMq = new()
            {
                ConnectionRef = connectionRef, Output = new() { QueueName = "work" }
            }
        }
    };

    private static PipelineDefinition Http(Dictionary<string, string> headers, int timeoutSeconds = 20) => new()
    {
        PipelineId = "http", Enabled = true, ContractId = "asd", RulesIndex = "rules",
        Transport = new()
        {
            Kind = "http", Http = new()
            {
                Endpoint = "https://example.test/path?mode=one&return=%2Ffoo", Headers = headers,
                TimeoutSeconds = timeoutSeconds
            }
        }
    };

    private static PipelineCatalogOptions OptionsFor(params PipelineDefinition[] pipelines) => new()
    {
        Pipelines = pipelines.ToList(),
        RabbitMqConnections = new()
        {
            ["source"] = new() { Hostname = "ingress.example.test", Username = "guest", Password = "guest" },
            ["output"] = new() { Hostname = "egress.example.test", Port = 5673, Username = "guest", Password = "guest" }
        }
    };

    private static IPipelineContractRegistry Registry()
    {
        var contract = new Mock<IPipelineContract>();
        contract.SetupGet(value => value.ContractId).Returns("asd");
        return new PipelineContractRegistry([contract.Object]);
    }

    private static PipelineCatalog Catalog(PipelineCatalogOptions options) =>
        new(Options.Create(options), Registry(), NullLogger<PipelineCatalog>.Instance);

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["PipelineCatalog:RabbitMqConnections:source:Hostname"] = "localhost",
        ["PipelineCatalog:RabbitMqConnections:source:Username"] = "guest",
        ["PipelineCatalog:RabbitMqConnections:source:Password"] = "guest",
        ["PipelineCatalog:Pipelines:0:PipelineId"] = "asd",
        ["PipelineCatalog:Pipelines:0:ContractId"] = "asd",
        ["PipelineCatalog:Pipelines:0:Enabled"] = "true",
        ["PipelineCatalog:Pipelines:0:RulesIndex"] = "rules",
        ["PipelineCatalog:Pipelines:0:Transport:Kind"] = "rabbitmq",
        ["PipelineCatalog:Pipelines:0:Transport:RabbitMq:ConnectionRef"] = "source",
        ["PipelineCatalog:Pipelines:0:Transport:RabbitMq:Output:QueueName"] = "work"
    };

    private static IHost BuildHost(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton(Registry());
        builder.Services.AddPipelineCatalog(builder.Configuration);
        return builder.Build();
    }
}
