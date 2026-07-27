using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Text;
using ImagingPipeline.Observability;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqServiceCollectionTests
{
    [Fact]
    public void RegistrationConfiguresStableNativeRabbitMqTracing()
    {
        var previousBaggage = Baggage.Current;
        try
        {
            Baggage.SetBaggage("tenant", "north");
            _ = new ServiceCollection()
                .AddRabbitMqPublisher(Configuration());

            Assert.False(RabbitMQActivitySource.UseRoutingKeyAsOperationName);
            Assert.True(RabbitMQActivitySource.TracingOptions.UsePublisherAsParent);
            Assert.NotNull(RabbitMQActivitySource.ContextInjector);
            Assert.NotNull(RabbitMQActivitySource.ContextExtractor);

            using var activity = new Activity("producer").Start();
            var headers = new Dictionary<string, object?>();
            RabbitMQActivitySource.ContextInjector(activity, headers);
            var properties = new BasicProperties { Headers = headers };

            var extracted = RabbitMQActivitySource.ContextExtractor(properties);

            Assert.Equal(activity.TraceId, extracted.TraceId);
            Assert.Equal(activity.SpanId, extracted.SpanId);
            Assert.Contains(
                "tenant=north",
                Encoding.UTF8.GetString(Assert.IsType<byte[]>(headers["baggage"])),
                StringComparison.Ordinal);
        }
        finally
        {
            Baggage.Current = previousBaggage;
        }
    }

    [Fact]
    public void NativeRabbitMqInjectorSerializesScopedCanonicalPipelineBaggage()
    {
        var previous = Baggage.Current;
        try
        {
            Baggage.Current = Baggage.Create(new Dictionary<string, string>
            {
                ["secret"] = "do-not-forward"
            });
            _ = new ServiceCollection().AddRabbitMqPublisher(Configuration());

            using (PipelineCorrelationBaggage.Push(new PipelineCorrelationContext(
                       TaskId: "task-1",
                       RequestId: "request-1",
                       ImageId: "image-1",
                       RuleId: "rule-1",
                       TenantId: "tenant-1",
                       AlgorithmName: "FindAir")))
            using (var activity = new Activity("producer").Start())
            {
                var headers = new Dictionary<string, object?>
                {
                    ["baggage"] = Encoding.UTF8.GetBytes("secret=stale"),
                    ["business-header"] = "preserved"
                };

                RabbitMQActivitySource.ContextInjector(activity, headers);

                var extracted = new W3CMessageTraceContextPropagator().Extract(headers);
                Assert.Equal("task-1", extracted.Baggage.GetBaggage(TelemetryAttributeNames.PipelineTaskId));
                Assert.Equal("request-1", extracted.Baggage.GetBaggage(TelemetryAttributeNames.PipelineRequestId));
                Assert.Equal("image-1", extracted.Baggage.GetBaggage(TelemetryAttributeNames.PipelineImageId));
                Assert.Equal("rule-1", extracted.Baggage.GetBaggage(TelemetryAttributeNames.PipelineRuleId));
                Assert.Equal("tenant-1", extracted.Baggage.GetBaggage(TelemetryAttributeNames.PipelineTenantId));
                Assert.Equal("FindAir", extracted.Baggage.GetBaggage(TelemetryAttributeNames.PipelineAlgorithmName));
                Assert.Null(extracted.Baggage.GetBaggage("secret"));
                Assert.Equal("preserved", headers["business-header"]);
            }

            Assert.Equal("do-not-forward", Baggage.Current.GetBaggage("secret"));
        }
        finally
        {
            Baggage.Current = previous;
        }
    }

    [Fact]
    public async Task AddRabbitMqPublisherRegistersPublisherOnlyServices()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(Configuration())
            .AddLogging()
            .AddRabbitMqPublisher(Configuration())
            .BuildServiceProvider(validateScopes: true);

        Assert.NotNull(provider.GetService<IRabbitMqPublisher>());
        Assert.Null(provider.GetService<IRabbitMqConsumer>());
        Assert.Null(provider.GetService<IRabbitMqClient>());
    }

    [Fact]
    public async Task AddRabbitMqConsumerRegistersFullClientServices()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(Configuration())
            .AddLogging()
            .AddRabbitMqConsumer(Configuration())
            .BuildServiceProvider(validateScopes: true);

        Assert.NotNull(provider.GetService<IRabbitMqPublisher>());
        Assert.NotNull(provider.GetService<IRabbitMqConsumer>());
        Assert.NotNull(provider.GetService<IRabbitMqClient>());
    }

    [Fact]
    public async Task AddRabbitMqConsumerUsesSeparatePublisherAndConsumerConnectionManagers()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(Configuration())
            .AddLogging()
            .AddRabbitMqConsumer(Configuration())
            .BuildServiceProvider(validateScopes: true);

        var publisherConnections = provider.GetRequiredService<IRabbitMqPublisherConnectionManager>();
        var consumerConnections = provider.GetRequiredService<IRabbitMqConsumerConnectionManager>();

        Assert.NotSame(publisherConnections, consumerConnections);
    }

    [Fact]
    public async Task AddRabbitMqClientRegistersFullClientServices()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(Configuration())
            .AddLogging()
            .AddRabbitMqClient(Configuration())
            .BuildServiceProvider(validateScopes: true);

        Assert.NotNull(provider.GetService<IRabbitMqPublisher>());
        Assert.NotNull(provider.GetService<IRabbitMqConsumer>());
        Assert.NotNull(provider.GetService<IRabbitMqClient>());
    }

    [Fact]
    public async Task AddRabbitMqPublisherBindsOptionsFromRabbitMqSection()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(Configuration(new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "broker",
                ["RabbitMq:Port"] = "5673",
                ["RabbitMq:InputQueue"] = "input",
                ["RabbitMq:ConsumerConcurrency"] = "3",
                ["RabbitMq:OutputPublishConcurrency"] = "7",
                ["RabbitMq:RetryDelayMilliseconds"] = "2500",
                ["RabbitMq:MaxRetryAttempts"] = "5",
                ["RabbitMq:RetryCountHeader"] = "x-service-retry-count",
                ["RabbitMq:ForwardedInputStage"] = "TileBuilder",
                ["RabbitMq:RetryQueues:0:RetryCount"] = "1",
                ["RabbitMq:RetryQueues:0:Queue"] = "retry.1",
                ["RabbitMq:RetryQueues:0:RoutingKey"] = "retry.1.key",
                ["RabbitMq:RetryQueues:0:DelayMilliseconds"] = "1000"
            }))
            .AddLogging()
            .AddRabbitMqPublisher(Configuration(new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "broker",
                ["RabbitMq:Port"] = "5673",
                ["RabbitMq:InputQueue"] = "input",
                ["RabbitMq:ConsumerConcurrency"] = "3",
                ["RabbitMq:OutputPublishConcurrency"] = "7",
                ["RabbitMq:RetryDelayMilliseconds"] = "2500",
                ["RabbitMq:MaxRetryAttempts"] = "5",
                ["RabbitMq:RetryCountHeader"] = "x-service-retry-count",
                ["RabbitMq:ForwardedInputStage"] = "TileBuilder",
                ["RabbitMq:RetryQueues:0:RetryCount"] = "1",
                ["RabbitMq:RetryQueues:0:Queue"] = "retry.1",
                ["RabbitMq:RetryQueues:0:RoutingKey"] = "retry.1.key",
                ["RabbitMq:RetryQueues:0:DelayMilliseconds"] = "1000"
            }))
            .BuildServiceProvider(validateScopes: true);

        var options = provider.GetRequiredService<IOptions<RabbitMqClientOptions>>().Value;

        Assert.Equal("broker", options.Host);
        Assert.Equal(5673, options.Port);
        Assert.Equal("input", options.InputQueue);
        Assert.Equal((ushort)3, options.ConsumerConcurrency);
        Assert.Equal(7, options.OutputPublishConcurrency);
        Assert.Equal(2500, options.RetryDelayMilliseconds);
        Assert.Equal(5, options.MaxRetryAttempts);
        Assert.Equal("x-service-retry-count", options.RetryCountHeader);
        Assert.Equal(ImagingPipeline.Observability.PipelineStage.TileBuilder, options.ForwardedInputStage);
        Assert.Equal("retry.1", options.RetryQueues[0].Queue);
        Assert.Equal("retry.1.key", options.RetryQueues[0].RoutingKey);
    }

    [Fact]
    public async Task AddRabbitMqConsumerBindsCollectionOptionsOnlyOnce()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["RabbitMq:RetryExchange"] = "retry.exchange",
            ["RabbitMq:RetryExchangeType"] = "headers",
            ["RabbitMq:RetryRoutingKey"] = "retry",
            ["RabbitMq:MaxRetryAttempts"] = "2",
            ["RabbitMq:RetryQueues:0:RetryCount"] = "1",
            ["RabbitMq:RetryQueues:0:Queue"] = "retry.1",
            ["RabbitMq:RetryQueues:0:DelayMilliseconds"] = "1000",
            ["RabbitMq:RetryQueues:1:RetryCount"] = "2",
            ["RabbitMq:RetryQueues:1:Queue"] = "retry.2",
            ["RabbitMq:RetryQueues:1:DelayMilliseconds"] = "2000"
        });

        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddRabbitMqConsumer(configuration)
            .BuildServiceProvider(validateScopes: true);

        var options = provider.GetRequiredService<IOptions<RabbitMqClientOptions>>().Value;

        Assert.Collection(
            options.RetryQueues,
            retry => Assert.Equal(1, retry.RetryCount),
            retry => Assert.Equal(2, retry.RetryCount));
        Assert.True(options.IsConsumerValid(out var error), error);
    }

    [Fact]
    public async Task AddRabbitMqPublisherDoesNotReplaceExistingPublisher()
    {
        var existing = new FakePublisher();
        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(Configuration())
            .AddLogging()
            .AddSingleton<IRabbitMqPublisher>(existing)
            .AddRabbitMqPublisher(Configuration())
            .BuildServiceProvider(validateScopes: true);

        Assert.Same(existing, provider.GetRequiredService<IRabbitMqPublisher>());
    }

    [Fact]
    public async Task AddRabbitMqConsumerDoesNotReplaceExistingClientOrConsumer()
    {
        var existingConsumer = new FakeConsumer();
        var existingClient = new FakeClient();
        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(Configuration())
            .AddLogging()
            .AddSingleton<IRabbitMqConsumer>(existingConsumer)
            .AddSingleton<IRabbitMqClient>(existingClient)
            .AddRabbitMqConsumer(Configuration())
            .BuildServiceProvider(validateScopes: true);

        Assert.Same(existingConsumer, provider.GetRequiredService<IRabbitMqConsumer>());
        Assert.Same(existingClient, provider.GetRequiredService<IRabbitMqClient>());
    }

    [Fact]
    public async Task InvalidPublisherOptionsFailWhenOptionsAreResolved()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(Configuration(new Dictionary<string, string?>
            {
                ["RabbitMq:InputQueue"] = ""
            }))
            .AddLogging()
            .AddRabbitMqPublisher(Configuration(new Dictionary<string, string?>
            {
                ["RabbitMq:InputQueue"] = ""
            }))
            .BuildServiceProvider(validateScopes: true);

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RabbitMqClientOptions>>().Value);

        Assert.Contains("RabbitMq publisher configuration is invalid.", exception.Failures);
    }

    private static IConfiguration Configuration() =>
        Configuration(null);

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?>? extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Port"] = "5672",
            ["RabbitMq:InputQueue"] = "input",
            ["RabbitMq:OutputQueue"] = "output",
            ["RabbitMq:DeadLetterQueue"] = "dlq",
            ["RabbitMq:RetryQueue"] = "retry"
        };

        if (extra is not null)
        {
            foreach (var item in extra)
            {
                values[item.Key] = item.Value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class FakePublisher : IRabbitMqPublisher
    {
        public Task PublishAsync(
            string exchange,
            string routingKey,
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PublishToInputAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PublishToOutputAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConsumer : IRabbitMqConsumer
    {
        public Task ConsumeAsync(
            IRabbitMqMessageHandler handler,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ConsumeBatchAsync(
            IRabbitMqBatchMessageHandler handler,
            int batchSize,
            TimeSpan maxWaitTime,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient : IRabbitMqClient
    {
        public Task PublishAsync(
            string exchange,
            string routingKey,
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PublishToInputAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PublishToOutputAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ConsumeAsync(
            IRabbitMqMessageHandler handler,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ConsumeBatchAsync(
            IRabbitMqBatchMessageHandler handler,
            int batchSize,
            TimeSpan maxWaitTime,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
