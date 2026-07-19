using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqClientBrokerTests : IClassFixture<RabbitMqBrokerFixture>
{
    private readonly RabbitMqBrokerFixture _broker;

    public RabbitMqClientBrokerTests(RabbitMqBrokerFixture broker)
    {
        _broker = broker;
    }

    [RabbitMqBrokerFact]
    public async Task PublishToInputAsyncPublishesPersistentMessageToInputQueue()
    {
        var topology = CreateTopology();
        await using var provider = BuildProvider(topology);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();

        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("hello"));

        var message = await WaitForMessageAsync(topology.InputQueue);

        Assert.Equal("hello", message);
    }

    [RabbitMqBrokerFact]
    public async Task SuccessfulHandlerOutputIsPublishedToOutputQueue()
    {
        var topology = CreateTopology();
        await using var provider = BuildProvider(topology);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new SuccessHandler(), cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("ok"), cts.Token);

        var output = await WaitForMessageAsync(topology.OutputQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal("OK", output);
        Assert.Null(await BasicGetAsync(topology.DeadLetterQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task SuccessfulHandlerOutputResetsRetryCountHeader()
    {
        var topology = CreateTopology();
        await using var provider = BuildProvider(topology);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new SuccessHandler(), cts.Token);
        await publisher.PublishToInputAsync(
            new RabbitMqMessageEnvelope(
                "input-with-retry-count",
                Encoding.UTF8.GetBytes("ok"),
                Headers: new Dictionary<string, object?>
                {
                    ["x-retry-count"] = 2
                }),
            cts.Token);

        var output = await WaitForEnvelopeAsync(topology.OutputQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal("OK", output.BodyAsUtf8());
        Assert.Equal(0, ReadRetryCount(output));
    }

    [RabbitMqBrokerFact]
    public async Task SuccessfulHandlerOutputMessagesArePublishedToOutputQueue()
    {
        var topology = CreateTopology();
        await using var provider = BuildProvider(topology);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new MultiOutputHandler(), cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("ok", "input-1"), cts.Token);

        var outputs = new[]
        {
            await WaitForMessageAsync(topology.OutputQueue, cts.Token),
            await WaitForMessageAsync(topology.OutputQueue, cts.Token)
        };
        await StopConsumerAsync(consumerTask, cts);

        Assert.Contains("OK-1", outputs);
        Assert.Contains("OK-2", outputs);
        Assert.Null(await BasicGetAsync(topology.DeadLetterQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task FailedHandlerIsDeadLetteredByBroker()
    {
        var topology = CreateTopology();
        await using var provider = BuildProvider(topology);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new FailureHandler(), cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("fail"), cts.Token);

        var deadLetter = await WaitForMessageAsync(topology.DeadLetterQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal("fail", deadLetter);
        Assert.Null(await BasicGetAsync(topology.OutputQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task HandlerExceptionIsPublishedToRetryQueue()
    {
        var topology = CreateTopology();
        await using var provider = BuildProvider(topology);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new ThrowingHandler(), cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("boom"), cts.Token);

        var retry = await WaitForEnvelopeAsync(topology.RetryQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal("boom", retry.BodyAsUtf8());
        Assert.Equal(1, ReadRetryCount(retry));
        Assert.Null(await BasicGetAsync(topology.OutputQueue, CancellationToken.None));
        Assert.Null(await BasicGetAsync(topology.DeadLetterQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task HandlerExceptionWithExistingRetryCountIsPublishedToMatchingRetryQueue()
    {
        var topology = CreateTopology();
        var firstRetryQueue = _broker.CreateName("retry.1");
        var secondRetryQueue = _broker.CreateName("retry.2");
        var thirdRetryQueue = _broker.CreateName("retry.3");
        _broker.TrackQueue(firstRetryQueue);
        _broker.TrackQueue(secondRetryQueue);
        _broker.TrackQueue(thirdRetryQueue);
        var configuration = BuildConfiguration(topology, new Dictionary<string, string?>
        {
            ["RabbitMq:RetryExchangeType"] = "headers",
            ["RabbitMq:RetryQueues:0:RetryCount"] = "1",
            ["RabbitMq:RetryQueues:0:Queue"] = firstRetryQueue,
            ["RabbitMq:RetryQueues:0:DelayMilliseconds"] = "60000",
            ["RabbitMq:RetryQueues:1:RetryCount"] = "2",
            ["RabbitMq:RetryQueues:1:Queue"] = secondRetryQueue,
            ["RabbitMq:RetryQueues:1:DelayMilliseconds"] = "60000",
            ["RabbitMq:RetryQueues:2:RetryCount"] = "3",
            ["RabbitMq:RetryQueues:2:Queue"] = thirdRetryQueue,
            ["RabbitMq:RetryQueues:2:DelayMilliseconds"] = "60000"
        });
        await using var provider = BuildProvider(configuration);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new ThrowingHandler(), cts.Token);
        await publisher.PublishToInputAsync(
            new RabbitMqMessageEnvelope(
                "retry-input",
                Encoding.UTF8.GetBytes("""{"payload":{"id":"image-1"}}"""),
                Headers: new Dictionary<string, object?>
                {
                    ["x-retry-count"] = 1
                }),
            cts.Token);

        var retry = await WaitForEnvelopeAsync(secondRetryQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        using var document = JsonDocument.Parse(retry.Body);
        Assert.Equal("image-1", document.RootElement.GetProperty("payload").GetProperty("id").GetString());
        Assert.Equal(2, ReadRetryCount(retry));
        Assert.Null(await BasicGetAsync(firstRetryQueue, CancellationToken.None));
        Assert.Null(await BasicGetAsync(thirdRetryQueue, CancellationToken.None));
        Assert.Null(await BasicGetAsync(topology.DeadLetterQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task HandlerExceptionWithInvalidRetryCountHeaderIsDeadLetteredByBroker()
    {
        var topology = CreateTopology();
        await using var provider = BuildProvider(topology);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new ThrowingHandler(), cts.Token);
        await publisher.PublishToInputAsync(
            new RabbitMqMessageEnvelope(
                "bad-retry-count",
                Encoding.UTF8.GetBytes("boom"),
                Headers: new Dictionary<string, object?>
                {
                    ["x-retry-count"] = "bad"
                }),
            cts.Token);

        var deadLetter = await WaitForMessageAsync(topology.DeadLetterQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal("boom", deadLetter);
        Assert.Null(await BasicGetAsync(topology.RetryQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task SuccessWithoutOutputBodyOnlyAcknowledgesInputMessage()
    {
        var topology = CreateTopology();
        await using var provider = BuildProvider(topology);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new AckOnlyHandler(), cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("ack"), cts.Token);

        await WaitForQueueMessageCountAsync(topology.InputQueue, 0, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Null(await BasicGetAsync(topology.OutputQueue, CancellationToken.None));
        Assert.Null(await BasicGetAsync(topology.DeadLetterQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task ConsumerConcurrencyProcessesMessagesAtTheSameTime()
    {
        var topology = CreateTopology();
        var configuration = BuildConfiguration(topology, new Dictionary<string, string?>
        {
            ["RabbitMq:ConsumerConcurrency"] = "2",
            ["RabbitMq:PrefetchCount"] = "1"
        });
        await using var provider = BuildProvider(configuration);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        var handler = new BlockingConcurrencyHandler(expectedConcurrentMessages: 2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(handler, cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("first"), cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("second"), cts.Token);

        await handler.WaitForConcurrentMessagesAsync(cts.Token);
        handler.Release();
        await WaitForQueueMessageCountAsync(topology.InputQueue, 0, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Null(await BasicGetAsync(topology.DeadLetterQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task DefaultExchangeConfigurationRoutesFailureDirectlyToDeadLetterQueue()
    {
        var topology = CreateTopology(useExchanges: false);
        await using var provider = BuildProvider(topology);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new FailureHandler(), cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("default-exchange"), cts.Token);

        var deadLetter = await WaitForMessageAsync(topology.DeadLetterQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal("default-exchange", deadLetter);
    }

    [RabbitMqBrokerFact]
    public async Task HeaderDeadLetterSettingsOverrideExplicitDeadLetterExchangeAndRoutingKey()
    {
        var topology = CreateTopology();
        var headerDeadLetterExchange = _broker.CreateName("header.dlx");
        _broker.TrackExchange(headerDeadLetterExchange);
        var configuration = BuildConfiguration(topology, new Dictionary<string, string?>
        {
            ["RabbitMq:DeadLetterRoutingKey"] = "",
            ["RabbitMq:HeadersArguments:x-dead-letter-exchange"] = headerDeadLetterExchange,
            ["RabbitMq:HeadersArguments:x-dead-letter-routing-key"] = topology.DeadLetterQueue
        });
        await using var provider = BuildProvider(configuration);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(new FailureHandler(), cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("header-dlx"), cts.Token);

        var deadLetter = await WaitForMessageAsync(topology.DeadLetterQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal("header-dlx", deadLetter);
    }

    [RabbitMqBrokerFact]
    public async Task HeadersArgumentsAreAppliedToInputQueue()
    {
        var topology = CreateTopology();
        var configuration = BuildConfiguration(topology, new Dictionary<string, string?>
        {
            ["RabbitMq:HeadersArguments:x-message-ttl"] = "60000"
        });
        await using var provider = BuildProvider(configuration);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();

        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("ttl"));

        var info = await PassiveQueueDeclareAsync(topology.InputQueue);
        var message = await WaitForMessageAsync(topology.InputQueue);

        Assert.Equal((uint)1, info.MessageCount);
        Assert.Equal("ttl", message);
    }

    [RabbitMqBrokerFact]
    public async Task InputBindingArgumentsAreAppliedToQueueBinding()
    {
        var topology = CreateTopology();
        var configuration = BuildConfiguration(topology, new Dictionary<string, string?>
        {
            ["RabbitMq:InputExchangeType"] = "headers",
            ["RabbitMq:InputBindingArguments:message-kind"] = "image"
        });
        await using var provider = BuildProvider(configuration);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();

        await publisher.PublishAsync(
            topology.InputExchange,
            topology.InputQueue,
            new RabbitMqMessageEnvelope(
                "message-1",
                Encoding.UTF8.GetBytes("headers-route"),
                Headers: new Dictionary<string, object?>
                {
                    ["message-kind"] = "image"
                }));

        var message = await WaitForMessageAsync(topology.InputQueue);

        Assert.Equal("headers-route", message);
    }

    private TestTopology CreateTopology(bool useExchanges = true)
    {
        var topology = new TestTopology(
            _broker.CreateName("input"),
            _broker.CreateName("output"),
            _broker.CreateName("retry"),
            _broker.CreateName("dlq"),
            useExchanges ? _broker.CreateName("input.exchange") : string.Empty,
            useExchanges ? _broker.CreateName("output.exchange") : string.Empty,
            useExchanges ? _broker.CreateName("retry.exchange") : string.Empty,
            useExchanges ? _broker.CreateName("dlx") : string.Empty);

        _broker.TrackQueue(topology.InputQueue);
        _broker.TrackQueue(topology.OutputQueue);
        _broker.TrackQueue(topology.RetryQueue);
        _broker.TrackQueue(topology.DeadLetterQueue);
        _broker.TrackExchange(topology.InputExchange);
        _broker.TrackExchange(topology.OutputExchange);
        _broker.TrackExchange(topology.RetryExchange);
        _broker.TrackExchange(topology.DeadLetterExchange);
        return topology;
    }

    private static ServiceProvider BuildProvider(TestTopology topology) =>
        BuildProvider(BuildConfiguration(topology));

    private static ServiceProvider BuildProvider(IConfiguration configuration) =>
        new ServiceCollection()
            .AddSingleton(configuration)
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddRabbitMqClient(configuration)
            .BuildServiceProvider(validateScopes: true);

    private static IConfiguration BuildConfiguration(
        TestTopology topology,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Port"] = "5672",
            ["RabbitMq:Username"] = "admin",
            ["RabbitMq:Password"] = "admin",
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:InputQueue"] = topology.InputQueue,
            ["RabbitMq:OutputQueue"] = topology.OutputQueue,
            ["RabbitMq:RetryQueue"] = topology.RetryQueue,
            ["RabbitMq:DeadLetterQueue"] = topology.DeadLetterQueue,
            ["RabbitMq:InputExchange"] = topology.InputExchange,
            ["RabbitMq:OutputExchange"] = topology.OutputExchange,
            ["RabbitMq:RetryExchange"] = topology.RetryExchange,
            ["RabbitMq:DeadLetterExchange"] = topology.DeadLetterExchange,
            ["RabbitMq:InputExchangeType"] = "direct",
            ["RabbitMq:OutputExchangeType"] = "direct",
            ["RabbitMq:RetryExchangeType"] = "direct",
            ["RabbitMq:DeadLetterExchangeType"] = "direct",
            ["RabbitMq:InputRoutingKey"] = topology.InputQueue,
            ["RabbitMq:OutputRoutingKey"] = topology.OutputQueue,
            ["RabbitMq:RetryRoutingKey"] = topology.RetryQueue,
            ["RabbitMq:DeadLetterRoutingKey"] = topology.DeadLetterQueue,
            ["RabbitMq:PrefetchCount"] = "1",
            ["RabbitMq:ConsumerConcurrency"] = "1",
            ["RabbitMq:PublisherChannelPoolSize"] = "2",
            ["RabbitMq:RetryDelayMilliseconds"] = "60000",
            ["RabbitMq:MaxRetryAttempts"] = "3",
            ["RabbitMq:RetryCountHeader"] = "x-retry-count",
            ["RabbitMq:ReconnectDelaySeconds"] = "1"
        };

        if (extra is not null)
        {
            foreach (var item in extra)
            {
                values[item.Key] = item.Value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<string> WaitForMessageAsync(
        string queue,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (!timeout.IsCancellationRequested)
        {
            var message = await BasicGetAsync(queue, timeout.Token);
            if (message is not null)
            {
                return message;
            }

            await Task.Delay(250, timeout.Token);
        }

        throw new TimeoutException($"Timed out waiting for message in {queue}.");
    }

    private static async Task<RabbitMqMessageEnvelope> WaitForEnvelopeAsync(
        string queue,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (!timeout.IsCancellationRequested)
        {
            var message = await BasicGetEnvelopeAsync(queue, timeout.Token);
            if (message is not null)
            {
                return message;
            }

            await Task.Delay(250, timeout.Token);
        }

        throw new TimeoutException($"Timed out waiting for message in {queue}.");
    }

    private static async Task WaitForQueueMessageCountAsync(
        string queue,
        uint expectedCount,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (!timeout.IsCancellationRequested)
        {
            var info = await PassiveQueueDeclareAsync(queue);
            if (info.MessageCount == expectedCount)
            {
                return;
            }

            await Task.Delay(250, timeout.Token);
        }

        throw new TimeoutException($"Timed out waiting for {queue} to have {expectedCount} messages.");
    }

    private static async Task<string?> BasicGetAsync(string queue, CancellationToken cancellationToken)
    {
        var result = await BasicGetEnvelopeAsync(queue, cancellationToken);
        return result?.BodyAsUtf8();
    }

    private static async Task<RabbitMqMessageEnvelope?> BasicGetEnvelopeAsync(string queue, CancellationToken cancellationToken)
    {
        await using var connection = await CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        var result = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken);
        if (result is null)
        {
            return null;
        }

        return new RabbitMqMessageEnvelope(
            result.BasicProperties.MessageId ?? string.Empty,
            result.Body.ToArray(),
            result.BasicProperties.ContentType ?? "application/octet-stream",
            result.BasicProperties.Headers is null
                ? null
                : new Dictionary<string, object?>(result.BasicProperties.Headers, StringComparer.Ordinal),
            result.BasicProperties.CorrelationId);
    }

    private static int ReadRetryCount(RabbitMqMessageEnvelope message)
    {
        Assert.NotNull(message.Headers);
        Assert.True(message.Headers.TryGetValue("x-retry-count", out var value));

        return value switch
        {
            int retryCount => retryCount,
            byte[] bytes => int.Parse(Encoding.UTF8.GetString(bytes), System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static async Task<QueueDeclareOk> PassiveQueueDeclareAsync(string queue)
    {
        await using var connection = await CreateConnectionAsync(CancellationToken.None);
        await using var channel = await connection.CreateChannelAsync();
        return await channel.QueueDeclarePassiveAsync(queue);
    }

    private static Task<IConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = "localhost",
            Port = 5672,
            UserName = "admin",
            Password = "admin",
            VirtualHost = "/"
        };

        return factory.CreateConnectionAsync(cancellationToken);
    }

    private static async Task StopConsumerAsync(Task consumerTask, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        try
        {
            await consumerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record TestTopology(
        string InputQueue,
        string OutputQueue,
        string RetryQueue,
        string DeadLetterQueue,
        string InputExchange,
        string OutputExchange,
        string RetryExchange,
        string DeadLetterExchange);

    private sealed class SuccessHandler : IRabbitMqMessageHandler
    {
        public Task<RabbitMqMessageProcessingResult> HandleAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            var output = Encoding.UTF8.GetBytes(message.BodyAsUtf8().ToUpperInvariant());
            return Task.FromResult(RabbitMqMessageProcessingResult.Success(output));
        }
    }

    private sealed class AckOnlyHandler : IRabbitMqMessageHandler
    {
        public Task<RabbitMqMessageProcessingResult> HandleAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RabbitMqMessageProcessingResult(true, null, null));
        }
    }

    private sealed class MultiOutputHandler : IRabbitMqMessageHandler
    {
        public Task<RabbitMqMessageProcessingResult> HandleAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            var outputs = new[]
            {
                message with
                {
                    MessageId = $"{message.MessageId}:output:1",
                    Body = Encoding.UTF8.GetBytes("OK-1"),
                    CorrelationId = message.MessageId
                },
                message with
                {
                    MessageId = $"{message.MessageId}:output:2",
                    Body = Encoding.UTF8.GetBytes("OK-2"),
                    CorrelationId = message.MessageId
                }
            };

            return Task.FromResult(RabbitMqMessageProcessingResult.Success(outputs));
        }
    }

    private sealed class FailureHandler : IRabbitMqMessageHandler
    {
        public Task<RabbitMqMessageProcessingResult> HandleAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(RabbitMqMessageProcessingResult.Failure("expected failure"));
        }
    }

    private sealed class BlockingConcurrencyHandler : IRabbitMqMessageHandler
    {
        private readonly int _expectedConcurrentMessages;
        private readonly TaskCompletionSource _concurrencyReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedMessages;

        public BlockingConcurrencyHandler(int expectedConcurrentMessages)
        {
            _expectedConcurrentMessages = expectedConcurrentMessages;
        }

        public async Task<RabbitMqMessageProcessingResult> HandleAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _startedMessages) >= _expectedConcurrentMessages)
            {
                _concurrencyReached.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);
            return new RabbitMqMessageProcessingResult(true, null, null);
        }

        public Task WaitForConcurrentMessagesAsync(CancellationToken cancellationToken) =>
            _concurrencyReached.Task.WaitAsync(cancellationToken);

        public void Release() => _release.TrySetResult();
    }

    private sealed class ThrowingHandler : IRabbitMqMessageHandler
    {
        public Task<RabbitMqMessageProcessingResult> HandleAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("expected exception");
        }
    }
}
