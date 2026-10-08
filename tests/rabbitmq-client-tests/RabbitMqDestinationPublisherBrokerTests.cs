using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqDestinationPublisherBrokerTests : IClassFixture<RabbitMqBrokerFixture>
{
    private static readonly RabbitMqDestinationConnection LocalBroker =
        new("local", "localhost", 5672, "admin", "admin", "/");

    private readonly RabbitMqBrokerFixture _broker;

    public RabbitMqDestinationPublisherBrokerTests(RabbitMqBrokerFixture broker)
    {
        _broker = broker;
    }

    [RabbitMqBrokerFact]
    public async Task PublishDeclaresQueueAndDeliversThroughDefaultExchange()
    {
        var queue = _broker.CreateName("destination");
        _broker.TrackQueue(queue);
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IRabbitMqDestinationPublisher>();

        await publisher.PublishAsync(
            new RabbitMqDestination
            {
                Connection = LocalBroker,
                QueueName = queue,
                QueueArguments = new Dictionary<string, object?> { ["x-max-priority"] = "5" }
            },
            new RabbitMqMessageEnvelope(
                "dispatch-1",
                Encoding.UTF8.GetBytes("hello"),
                Headers: new Dictionary<string, object?> { ["x-pipeline-specific"] = "kept" }));

        var delivered = await BasicGetAsync(queue);
        Assert.NotNull(delivered);
        Assert.Equal("hello", Encoding.UTF8.GetString(delivered.Body.Span));
        Assert.Equal("dispatch-1", delivered.BasicProperties.MessageId);
        Assert.Equal("kept", Encoding.UTF8.GetString((byte[])delivered.BasicProperties.Headers!["x-pipeline-specific"]!));
    }

    [RabbitMqBrokerFact]
    public async Task PublishBindsQueueToNamedExchangeAndRoutesByRoutingKey()
    {
        var queue = _broker.CreateName("destination");
        var exchange = _broker.CreateName("destination.x");
        _broker.TrackQueue(queue);
        _broker.TrackExchange(exchange);
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IRabbitMqDestinationPublisher>();

        await publisher.PublishAsync(
            new RabbitMqDestination
            {
                Connection = LocalBroker,
                QueueName = queue,
                ExchangeName = exchange,
                ExchangeType = ExchangeType.Topic,
                RoutingKey = "asd.work",
                BindQueueToExchange = true
            },
            RabbitMqMessageEnvelope.FromUtf8("routed", "dispatch-2"));

        var delivered = await BasicGetAsync(queue);
        Assert.NotNull(delivered);
        Assert.Equal("routed", Encoding.UTF8.GetString(delivered.Body.Span));
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddLogging();
        services.AddRabbitMqDestinationPublisher(configuration);
        return services.BuildServiceProvider();
    }

    private static async Task<BasicGetResult?> BasicGetAsync(string queue)
    {
        var factory = new ConnectionFactory
        {
            HostName = LocalBroker.Host,
            Port = LocalBroker.Port,
            UserName = LocalBroker.Username,
            Password = LocalBroker.Password,
            VirtualHost = LocalBroker.VirtualHost
        };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return await channel.BasicGetAsync(queue, autoAck: true);
    }
}
