using System.Text;
using System.Text.Json;
using System.Net;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbPublisher.MessageHandling;
using ImagingPipeline.TbPublisher.Processing;
using ImagingPipeline.TbPublisher.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class TbPublisherIntegrationTests : IClassFixture<RabbitMqBrokerFixture>
{
    private readonly RabbitMqBrokerFixture _broker;
    private const string ValidBody = """
    {
      "taskId": "msg-1",
      "ruleId": "rule-1",
      "algorithmName": ["FindAir", "Rpn"],
      "tenantId": "tenant-1",
      "imageId": "image-1",
      "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
      "photoTime": "2026-07-27T10:00:00Z",
      "sensorType": "EO",
      "imageUrl": "/images/image-1.tiff",
      "imageWidth": 4096,
      "imageHeight": 3072,
      "bestResolution": 100,
      "sensorName": "sensor-1",
      "areaOfInterest": "region-alpha",
      "gridType": "EO",
      "gridURI": "grid://example",
      "tilingConfigs": [
        { "tileSizeWidth": 512, "tileSizeHeight": 512, "tileOverlapWidth": 32, "tileOverlapHeight": 32 }
      ]
    }
    """;

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
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8(ValidBody, "msg-1"), cts.Token);

        var output = await WaitForMessageAsync(topology.OutputQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        var outputMessage = JsonSerializer.Deserialize<TbPublisherOutputMessageDto>(
            output.Body.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("rule-1", outputMessage!.MissionMetadata.Overlay.RuleId);
        Assert.Equal(
            [AlgorithmName.FindAir, AlgorithmName.Rpn],
            outputMessage.MissionMetadata.Overlay.AlgorithmNames);
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

        Assert.Equal("{}", Encoding.UTF8.GetString(deadLetter.Body.Span));
        AssertFailure(deadLetter, RabbitMqFailureKind.InvalidData, "invalid_input");
        Assert.Equal(0, projectionMapper.RequestCount);
        Assert.Null(await BasicGetAsync(topology.OutputQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task ProjectionMapperFailureIsRetriedThenDeadLettered()
    {
        await using var projectionMapper = new FakeProjectionMapperServer(_ => "{}", HttpStatusCode.ServiceUnavailable);

        var topology = CreateTopology();
        await using var provider = BuildProvider(topology, projectionMapper.BaseUrl);
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        var consumer = provider.GetRequiredService<IRabbitMqConsumer>();
        var handler = provider.GetRequiredService<IRabbitMqMessageHandler>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumerTask = consumer.ConsumeAsync(handler, cts.Token);
        await publisher.PublishToInputAsync(RabbitMqMessageEnvelope.FromUtf8(ValidBody, "msg-1"), cts.Token);

        var deadLetter = await WaitForMessageAsync(topology.DeadLetterQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal(ValidBody, Encoding.UTF8.GetString(deadLetter.Body.Span));
        AssertFailure(deadLetter, RabbitMqFailureKind.Transient, "projection_mapper_http_status", 503);
        Assert.Equal("msg-1", deadLetter.BasicProperties.MessageId);
        Assert.True(RabbitMqFailureHeaders.TryReadNonNegativeInt(
            new Dictionary<string, object?>(deadLetter.BasicProperties.Headers!), "retry-count", out var retryCount));
        Assert.Equal(1, retryCount);
        Assert.Equal(2, projectionMapper.RequestCount);
        Assert.Null(await BasicGetAsync(topology.OutputQueue, CancellationToken.None));
    }

    [RabbitMqBrokerFact]
    public async Task InvalidProjectionResponseIsRetainedWithoutRetryingOrChangingTheSource()
    {
        await using var projectionMapper = new FakeProjectionMapperServer(_ => "not valid json");
        var topology = CreateTopology();
        await using var provider = BuildProvider(topology, projectionMapper.BaseUrl);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var consumerTask = provider.GetRequiredService<IRabbitMqConsumer>().ConsumeAsync(
            provider.GetRequiredService<IRabbitMqMessageHandler>(), cts.Token);
        await provider.GetRequiredService<IRabbitMqPublisher>().PublishToInputAsync(
            RabbitMqMessageEnvelope.FromUtf8(ValidBody, "malformed-response"), cts.Token);

        var deadLetter = await WaitForMessageAsync(topology.DeadLetterQueue, cts.Token);
        await StopConsumerAsync(consumerTask, cts);

        Assert.Equal(ValidBody, Encoding.UTF8.GetString(deadLetter.Body.Span));
        AssertFailure(deadLetter, RabbitMqFailureKind.DependencyResponse, "projection_mapper_invalid_json", 200);
        Assert.Equal(1, projectionMapper.RequestCount);
        Assert.Null(await BasicGetAsync(topology.OutputQueue, CancellationToken.None));
        Assert.Null(await BasicGetAsync(topology.RetryQueue, CancellationToken.None));
    }

    private static void AssertFailure(BasicGetResult delivery, RabbitMqFailureKind kind, string code, int? httpStatus = null)
    {
        Assert.NotNull(delivery.BasicProperties.Headers);
        var headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers);
        Assert.True(RabbitMqFailureHeaders.TryReadFailure(headers, out var failure));
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(code, failure.Code);
        Assert.Equal(httpStatus, failure.HttpStatus);
        Assert.True(RabbitMqFailureHeaders.TryReadText(headers, "failure-service", out var service));
        Assert.Equal("tb-publisher", service);
    }

    private TestTopology CreateTopology()
    {
        var topology = new TestTopology(
            _broker.CreateName("input"),
            _broker.CreateName("output"),
            _broker.CreateName("dlq"),
            _broker.CreateName("retry"));

        _broker.TrackQueue(topology.InputQueue);
        _broker.TrackQueue(topology.OutputQueue);
        _broker.TrackQueue(topology.DeadLetterQueue);
        _broker.TrackQueue(topology.RetryQueue);
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
            ["RabbitMq:EnrichedDeadLetterEnabled"] = "true",
            ["RabbitMq:FailureServiceName"] = "tb-publisher",
            ["RabbitMq:RetryQueue"] = topology.RetryQueue,
            ["RabbitMq:RetryDelayMilliseconds"] = "100",
            ["RabbitMq:MaxRetryAttempts"] = "1",
            ["RabbitMq:PrefetchCount"] = "1",
            ["RabbitMq:PublisherChannelPoolSize"] = "2",
            ["RabbitMq:ReconnectDelaySeconds"] = "1",
            ["ProjectionMapper:Host"] = projectionMapperBaseUrl,
            ["ProjectionMapper:Endpoints:G2IMultiPoints"] = new Uri(new Uri(projectionMapperBaseUrl), "/flare/g2i-by-id").AbsoluteUri,
            ["ProjectionMapper:Endpoints:I2GById"] = new Uri(new Uri(projectionMapperBaseUrl), "/flare/i2g-by-id").AbsoluteUri,
            ["ProjectionMapper:Endpoints:I2GByRegistration"] = new Uri(new Uri(projectionMapperBaseUrl), "/flare/i2g-by-registration").AbsoluteUri,
            ["ProjectionMapper:SendingSystem"] = "flare",
            ["ProjectionMapper:TimeoutSeconds"] = "10"
        }).Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddRabbitMqClient(configuration)
            .AddProjectionMapperClient(configuration);

        services.AddSingleton<IInputMessageValidator, InputMessageValidator>();
        services.AddSingleton<TbPublisherGeometryConverter>();
        services.AddSingleton<ITbPublisherOutputMessageBuilder, TbPublisherOutputMessageBuilder>();
        services.AddSingleton<IRabbitMqMessageHandler, TbPublisherMessageHandler>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<BasicGetResult> WaitForMessageAsync(string queue, CancellationToken cancellationToken)
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

    private static async Task<BasicGetResult?> BasicGetAsync(string queue, CancellationToken cancellationToken)
    {
        await using var connection = await CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        return await channel.BasicGetAsync(queue, autoAck: true, cancellationToken);
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
        string DeadLetterQueue,
        string RetryQueue);
}
