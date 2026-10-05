using System.Text;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqDestinationPublisherTests
{
    private static readonly RabbitMqDestinationConnection AsdBroker =
        new("asd-output", "broker-a", 5672, "user", "secret", "/");

    [Fact]
    public async Task DefaultExchangeDestinationDeclaresQueueAndPublishesByQueueName()
    {
        var connections = new FakeConnections();
        await using var publisher = CreatePublisher(connections);
        var destination = new RabbitMqDestination
        {
            Connection = AsdBroker,
            QueueName = "int.algo.tb_publisher",
            QueueArguments = new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = "int.algo.tb_publisher.dlq"
            }
        };

        await publisher.PublishAsync(destination, Message("dispatch-1"));

        var channel = connections.Single().Channel;
        var declare = Invocation(channel, nameof(IChannel.QueueDeclareAsync));
        Assert.Equal("int.algo.tb_publisher", declare.Arguments[0]);
        Assert.Equal(true, declare.Arguments[1]);
        var arguments = Assert.IsAssignableFrom<IDictionary<string, object?>>(declare.Arguments[4]);
        Assert.Equal("int.algo.tb_publisher.dlq", arguments["x-dead-letter-routing-key"]);
        Assert.DoesNotContain(channel.Invocations, call => call.Method.Name == nameof(IChannel.ExchangeDeclareAsync));
        Assert.DoesNotContain(channel.Invocations, call => call.Method.Name == nameof(IChannel.QueueBindAsync));

        var publish = Invocation(channel, nameof(IChannel.BasicPublishAsync));
        Assert.Equal("", publish.Arguments[0]);
        Assert.Equal("int.algo.tb_publisher", publish.Arguments[1]);
        Assert.Equal(true, publish.Arguments[2]);
    }

    [Fact]
    public async Task BoundExchangeDestinationDeclaresExchangeQueueAndBindingAndPublishesToExchange()
    {
        var connections = new FakeConnections();
        await using var publisher = CreatePublisher(connections);
        var destination = new RabbitMqDestination
        {
            Connection = AsdBroker,
            QueueName = "asd.output",
            ExchangeName = "asd.events",
            ExchangeType = ExchangeType.Headers,
            RoutingKey = "",
            BindQueueToExchange = true,
            BindingArguments = new Dictionary<string, object?> { ["x-match"] = "all", ["tenantId"] = "alpha" }
        };

        await publisher.PublishAsync(destination, Message("dispatch-1"));

        var channel = connections.Single().Channel;
        var exchange = Invocation(channel, nameof(IChannel.ExchangeDeclareAsync));
        Assert.Equal("asd.events", exchange.Arguments[0]);
        Assert.Equal(ExchangeType.Headers, exchange.Arguments[1]);
        var bind = Invocation(channel, nameof(IChannel.QueueBindAsync));
        Assert.Equal(new object[] { "asd.output", "asd.events", "" }, bind.Arguments.Take(3));
        var bindingArguments = Assert.IsAssignableFrom<IDictionary<string, object?>>(bind.Arguments[3]);
        Assert.Equal("alpha", bindingArguments["tenantId"]);

        var publish = Invocation(channel, nameof(IChannel.BasicPublishAsync));
        Assert.Equal("asd.events", publish.Arguments[0]);
        Assert.Equal("", publish.Arguments[1]);
    }

    [Fact]
    public async Task UnboundExchangeDestinationPublishesWithRoutingKeyWithoutBinding()
    {
        var connections = new FakeConnections();
        await using var publisher = CreatePublisher(connections);
        var destination = new RabbitMqDestination
        {
            Connection = AsdBroker,
            QueueName = "asd.output",
            ExchangeName = "asd.events",
            ExchangeType = ExchangeType.Topic,
            RoutingKey = "asd.work"
        };

        await publisher.PublishAsync(destination, Message("dispatch-1"));

        var channel = connections.Single().Channel;
        Assert.DoesNotContain(channel.Invocations, call => call.Method.Name == nameof(IChannel.QueueBindAsync));
        var publish = Invocation(channel, nameof(IChannel.BasicPublishAsync));
        Assert.Equal("asd.events", publish.Arguments[0]);
        Assert.Equal("asd.work", publish.Arguments[1]);
    }

    [Fact]
    public async Task PublishSendsCallerHeadersAsGivenAndAddsTimingHeaders()
    {
        var connections = new FakeConnections();
        await using var publisher = CreatePublisher(connections);
        var message = new RabbitMqMessageEnvelope(
            "dispatch-1",
            Encoding.UTF8.GetBytes("{}"),
            Headers: new Dictionary<string, object?>
            {
                [FindAirMessageHeaders.ContractVersion] = 7,
                ["x-pipeline-specific"] = "kept"
            },
            CorrelationId: "source-1");

        await publisher.PublishAsync(Queue("asd.output"), message);

        var publish = Invocation(connections.Single().Channel, nameof(IChannel.BasicPublishAsync));
        var properties = Assert.IsType<BasicProperties>(publish.Arguments[3]);
        Assert.Equal("dispatch-1", properties.MessageId);
        Assert.Equal("source-1", properties.CorrelationId);
        Assert.Equal("application/json", properties.ContentType);
        Assert.True(properties.Persistent);
        var headers = properties.Headers!;
        Assert.Equal(7, headers[FindAirMessageHeaders.ContractVersion]);
        Assert.Equal("kept", headers["x-pipeline-specific"]);
        Assert.True(headers.ContainsKey(FindAirMessageHeaders.StartedAtUnixMilliseconds));
        Assert.True(headers.ContainsKey(FindAirMessageHeaders.PublishedAtUnixMilliseconds));
        Assert.False(headers.ContainsKey("retry-count"));
        Assert.Equal(2, message.Headers!.Count);
    }

    [Fact]
    public async Task DestinationIsDeclaredOncePerChannel()
    {
        var connections = new FakeConnections();
        await using var publisher = CreatePublisher(connections);

        await publisher.PublishAsync(Queue("asd.output"), Message("dispatch-1"));
        await publisher.PublishAsync(Queue("asd.output"), Message("dispatch-2"));
        await publisher.PublishAsync(Queue("asd.other"), Message("dispatch-3"));

        var channel = connections.Single().Channel;
        Assert.Equal(2, channel.Invocations.Count(call => call.Method.Name == nameof(IChannel.QueueDeclareAsync)));
        Assert.Equal(3, channel.Invocations.Count(call => call.Method.Name == nameof(IChannel.BasicPublishAsync)));
    }

    [Fact]
    public async Task EachDistinctConnectionGetsItsOwnConnectionManager()
    {
        var connections = new FakeConnections();
        await using var publisher = CreatePublisher(connections);
        var otherBroker = AsdBroker with { Name = "other-output", Host = "broker-b" };

        await publisher.PublishAsync(Queue("asd.output"), Message("dispatch-1"));
        await publisher.PublishAsync(Queue("asd.output") with { Connection = AsdBroker with { } }, Message("dispatch-2"));
        await publisher.PublishAsync(Queue("other.output") with { Connection = otherBroker }, Message("dispatch-3"));

        Assert.Equal(2, connections.Count);
        Assert.Equal(AsdBroker, connections[0].Connection);
        Assert.Equal(otherBroker, connections[1].Connection);
    }

    [Fact]
    public async Task DisposeAsyncDisposesCreatedConnectionManagersAndRejectsLaterPublishes()
    {
        var connections = new FakeConnections();
        var publisher = CreatePublisher(connections);
        await publisher.PublishAsync(Queue("asd.output"), Message("dispatch-1"));

        await publisher.DisposeAsync();

        Assert.True(connections.Single().IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => publisher.PublishAsync(Queue("asd.output"), Message("dispatch-2")));
    }

    [Fact]
    public async Task InvalidDestinationIsRejectedBeforeConnecting()
    {
        var connections = new FakeConnections();
        await using var publisher = CreatePublisher(connections);

        await Assert.ThrowsAsync<ArgumentException>(
            () => publisher.PublishAsync(Queue(" "), Message("dispatch-1")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => publisher.PublishAsync(Queue("asd.output") with { BindQueueToExchange = true }, Message("dispatch-1")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => publisher.PublishAsync(
                Queue("asd.output") with { Connection = AsdBroker with { Host = "" } },
                Message("dispatch-1")));
        Assert.Empty(connections);
    }

    [Fact]
    public void ConnectionToStringRedactsCredentials()
    {
        var text = AsdBroker.ToString();

        Assert.Contains("broker-a", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("user", text);
    }

    [Fact]
    public async Task AddRabbitMqDestinationPublisherRegistersPublisherWithoutRabbitMqSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:DestinationPublisher:PublisherChannelPoolSize"] = "8"
            })
            .Build();
        var services = new ServiceCollection().AddLogging();

        services.AddRabbitMqDestinationPublisher(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<RabbitMqDestinationPublisher>(provider.GetRequiredService<IRabbitMqDestinationPublisher>());
        Assert.Equal(8, provider.GetRequiredService<IOptions<RabbitMqDestinationPublisherOptions>>().Value.PublisherChannelPoolSize);
        Assert.Null(provider.GetService<IRabbitMqPublisher>());
    }

    [Fact]
    public async Task InvalidDestinationPublisherOptionsFailWhenOptionsAreResolved()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:DestinationPublisher:PublisherChannelPoolSize"] = "0"
            })
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddRabbitMqDestinationPublisher(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RabbitMqDestinationPublisherOptions>>().Value);
    }

    private static RabbitMqDestinationPublisher CreatePublisher(FakeConnections connections) =>
        new(Options.Create(new RabbitMqDestinationPublisherOptions()), connections.Create);

    private static RabbitMqDestination Queue(string queueName) =>
        new() { Connection = AsdBroker, QueueName = queueName };

    private static RabbitMqMessageEnvelope Message(string messageId) =>
        new(messageId, Encoding.UTF8.GetBytes("{}"));

    private static IInvocation Invocation(Mock<IChannel> channel, string methodName) =>
        Assert.Single(channel.Invocations, call => call.Method.Name == methodName);

    private sealed class FakeConnections : List<FakeConnectionManager>
    {
        public IRabbitMqConnectionManager Create(RabbitMqDestinationConnection connection)
        {
            var manager = new FakeConnectionManager(connection);
            Add(manager);
            return manager;
        }
    }

    private sealed class FakeConnectionManager : IRabbitMqConnectionManager
    {
        private readonly Mock<IConnection> _connection = new();

        public FakeConnectionManager(RabbitMqDestinationConnection connection)
        {
            Connection = connection;
            Channel.SetupGet(channel => channel.IsOpen).Returns(true);
            _connection
                .Setup(value => value.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Channel.Object);
        }

        public RabbitMqDestinationConnection Connection { get; }
        public Mock<IChannel> Channel { get; } = new();
        public bool IsDisposed { get; private set; }

        public Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_connection.Object);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
