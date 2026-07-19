using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqServiceCollectionTests
{
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
                ["RabbitMq:InputQueue"] = "input"
            }))
            .AddLogging()
            .AddRabbitMqPublisher(Configuration(new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "broker",
                ["RabbitMq:Port"] = "5673",
                ["RabbitMq:InputQueue"] = "input"
            }))
            .BuildServiceProvider(validateScopes: true);

        var options = provider.GetRequiredService<IOptions<RabbitMqClientOptions>>().Value;

        Assert.Equal("broker", options.Host);
        Assert.Equal(5673, options.Port);
        Assert.Equal("input", options.InputQueue);
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
            ["RabbitMq:Port"] = "5672"
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
