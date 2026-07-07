using System.Text;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbPublisher.Application;
using ImagingPipeline.TbPublisher.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class TbPublisherIntegrationTests : IClassFixture<RabbitMqBrokerFixture>
{
    private readonly RabbitMqBrokerFixture _broker;

    public TbPublisherIntegrationTests(RabbitMqBrokerFixture broker)
    {
        _broker = broker;
    }

    [RabbitMqBrokerFact]
    public async Task ValidMessageIsProjectedAndPublishedToTilingConfigQueue()
    {
        await using var projectionMapper = new FakeProjectionMapperServer(_ => """
        { "coordinates": [[0, 0], [1, 0], [1, 1], [0, 1]] }
        """);

        var topology = CreateTopology();
        await using var provider = BuildProvider(topology, projectionMapper.BaseUrl);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        var handler = provider.GetRequiredService<IRabbitMqMessageHandler>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(handler, cts.Token);
        var body = """
        {
          "ruleId": "rule-1",
          "algorithmName": "FindAir",
          "tenantId": "tenant-1",
          "imageId": "image-1",
          "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
          "tilingConfigs": [
            { "tileSizeWidth": 512, "tileSizeHeight": 512, "tileOverlapWidth": 32, "tileOverlapHeight": 32 }
          ]
        }
        """;
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8(body, "msg-1"), cts.Token);

        var output = await WaitForMessageAsync(topology.OutputQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        var outputMessage = JsonSerializer.Deserialize<TbPublisherOutputMessageDto>(
            output, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("rule-1", outputMessage!.MissionMetadata.Overlay.RuleId);
        Assert.Equal("tenant-1", outputMessage.MissionMetadata.TenantId);
        Assert.Equal("image-1", outputMessage.MissionMetadata.Overlay.ImageId);
        Assert.Equal(512, outputMessage.ModelMetadata.TbCropSizeX);
        Assert.StartsWith("POLYGON", outputMessage.FocusedPxWkt);
        Assert.Null(await BasicGetAsync(topology.DeadLetterQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task InvalidMessageIsDeadLettered()
    {
        await using var projectionMapper = new FakeProjectionMapperServer(_ => """{ "coordinates": [] }""");

        var topology = CreateTopology();
        await using var provider = BuildProvider(topology, projectionMapper.BaseUrl);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        var handler = provider.GetRequiredService<IRabbitMqMessageHandler>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(handler, cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8("{}"), cts.Token);

        var deadLetter = await WaitForMessageAsync(topology.DeadLetterQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal("{}", deadLetter);
        Assert.Null(await BasicGetAsync(topology.OutputQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task ProjectionMapperFailureLeavesMessageDeadLettered()
    {
        await using var projectionMapper = new FakeProjectionMapperServer(_ => "not valid json");

        var topology = CreateTopology();
        await using var provider = BuildProvider(topology, projectionMapper.BaseUrl);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        var handler = provider.GetRequiredService<IRabbitMqMessageHandler>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(handler, cts.Token);
        var body = """
        {
          "ruleId": "rule-1",
          "algorithmName": "FindAir",
          "tenantId": "tenant-1",
          "imageId": "image-1",
          "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
          "tilingConfigs": [
            { "tileSizeWidth": 512, "tileSizeHeight": 512, "tileOverlapWidth": 32, "tileOverlapHeight": 32 }
          ]
        }
        """;
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8(body, "msg-1"), cts.Token);

        var deadLetter = await WaitForMessageAsync(topology.DeadLetterQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal(body, deadLetter);
        Assert.Null(await BasicGetAsync(topology.OutputQueue, CancellationToken.None));
    }

    private TestTopology CreateTopology()
    {
        var topology = new TestTopology(
            _broker.CreateName("input"),
            _broker.CreateName("output"),
            _broker.CreateName("dlq"));

        _broker.TrackQueue(topology.InputQueue);
        _broker.TrackQueue(topology.OutputQueue);
        _broker.TrackQueue(topology.DeadLetterQueue);
        return topology;
    }

    private static ServiceProvider BuildProvider(TestTopology topology, string projectionMapperBaseUrl)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Port"] = "5672",
            ["RabbitMq:Username"] = "admin",
            ["RabbitMq:Password"] = "admin",
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:InputQueue"] = topology.InputQueue,
            ["RabbitMq:OutputQueue"] = topology.OutputQueue,
            ["RabbitMq:DeadLetterQueue"] = topology.DeadLetterQueue,
            ["RabbitMq:PrefetchCount"] = "1",
            ["RabbitMq:PublisherChannelPoolSize"] = "2",
            ["RabbitMq:ReconnectDelaySeconds"] = "1",
            ["ProjectionMapper:Host"] = projectionMapperBaseUrl,
            ["ProjectionMapper:Endpoints:g2iMultiPoints"] = "/flare/g2i-by-id",
            ["ProjectionMapper:SendingSystem"] = "flare",
            ["ProjectionMapper:TimeoutSeconds"] = "10"
        }).Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddRabbitMqClient(configuration)
            .AddProjectionMapperClient(configuration);

        services.AddSingleton<ITbMessageValidator, TbMessageValidator>();
        services.AddSingleton<ITbPublisherOutputMessageBuilder, TbPublisherOutputMessageBuilder>();
        services.AddSingleton<IRabbitMqMessageHandler, TbPublisherMessageHandler>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<string> WaitForMessageAsync(string queue, CancellationToken cancellationToken)
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

    private static async Task<string?> BasicGetAsync(string queue, CancellationToken cancellationToken)
    {
        await using var connection = await CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        var result = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken);
        return result is null ? null : Encoding.UTF8.GetString(result.Body.Span);
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

    private sealed record TestTopology(string InputQueue, string OutputQueue, string DeadLetterQueue);
}
