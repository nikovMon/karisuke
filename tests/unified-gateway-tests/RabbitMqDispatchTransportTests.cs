using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.UnifiedGateway.Dispatch;
using static ImagingPipeline.UnifiedGateway.Tests.DispatchTestData;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class RabbitMqDispatchTransportTests
{
    [Fact]
    public async Task DestinationIsBuiltFromThePipelinesCatalogOutputAndNamedConnection()
    {
        var pipeline = RabbitMqPipeline("asd", output: new RabbitMqQueueOptions
        {
            QueueName = "asd.output",
            Arguments = new Dictionary<string, object?> { ["x-dead-letter-routing-key"] = "asd.output.dlq" },
            ExchangeSettings = new RabbitMqExchangeOptions
            {
                ShouldBindToExchange = true,
                ExchangeName = "asd.events",
                ExchangeType = "topic",
                RoutingKey = "asd.work",
                Arguments = new Dictionary<string, object?> { ["alternate-exchange"] = "asd.unrouted" },
                BindingArguments = new Dictionary<string, object?> { ["x-match"] = "all" }
            }
        });
        var publisher = new RecordingPublisher();
        var transport = CreateTransport(publisher, pipeline);

        var outcome = await transport.SendAsync(Unit(pipeline), CancellationToken.None);

        Assert.Equal(DispatchStatus.Delivered, outcome.Status);
        var destination = Assert.Single(publisher.Published).Destination;
        Assert.Equal(new RabbitMqDestinationConnection("asd-output", "broker-a", 5673, "user", "secret", "/asd"), destination.Connection);
        Assert.Equal("asd.output", destination.QueueName);
        Assert.Equal("asd.output.dlq", destination.QueueArguments["x-dead-letter-routing-key"]);
        Assert.Equal("asd.events", destination.ExchangeName);
        Assert.Equal("topic", destination.ExchangeType);
        Assert.Equal("asd.work", destination.RoutingKey);
        Assert.True(destination.BindQueueToExchange);
        Assert.Equal("asd.unrouted", destination.ExchangeArguments["alternate-exchange"]);
        Assert.Equal("all", destination.BindingArguments["x-match"]);
    }

    [Fact]
    public async Task DefaultExchangeOutputRoutesByQueueName()
    {
        var pipeline = RabbitMqPipeline("asd");
        var publisher = new RecordingPublisher();

        await CreateTransport(publisher, pipeline).SendAsync(Unit(pipeline), CancellationToken.None);

        var destination = Assert.Single(publisher.Published).Destination;
        Assert.Equal("", destination.ExchangeName);
        Assert.Equal("asd.output", destination.RoutingKey);
        Assert.False(destination.BindQueueToExchange);
    }

    [Fact]
    public async Task EnvelopeCarriesDispatchIdSourceIdPayloadAndContractHeaders()
    {
        var pipeline = RabbitMqPipeline("asd");
        var publisher = new RecordingPublisher();
        var unit = Unit(
            pipeline,
            payload: Payload(
                new Dictionary<string, string> { ["algorithmName"] = "FindAir", ["tenantId"] = "tenant-a", ["findair-contract-version"] = "text" },
                new Dictionary<string, object?> { ["findair-contract-version"] = 1 }),
            sourceHeaders: new Dictionary<string, object?>
            {
                ["FINDAIR-STARTED-AT-UNIX-MS"] = 1_700_000_000_000L,
                [FindAirMessageHeaders.TraceParent] = "00-source-trace",
                ["x-updated-fields"] = "gridType",
                ["retry-count"] = 2
            });

        await CreateTransport(publisher, pipeline).SendAsync(unit, CancellationToken.None);

        var message = Assert.Single(publisher.Published).Message;
        Assert.Equal(unit.DispatchId, message.MessageId);
        Assert.Equal("source-1", message.CorrelationId);
        Assert.Equal("application/json", message.ContentType);
        Assert.Same(unit.Work.Payload.Body, message.Body);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                [FindAirMessageHeaders.StartedAtUnixMilliseconds] = 1_700_000_000_000L,
                ["algorithmName"] = "FindAir",
                ["tenantId"] = "tenant-a",
                ["findair-contract-version"] = 1
            },
            message.Headers);
    }

    [Fact]
    public async Task MalformedSourceStartTimestampIsNotForwarded()
    {
        var pipeline = RabbitMqPipeline("asd");
        var publisher = new RecordingPublisher();
        var unit = Unit(pipeline, sourceHeaders: new Dictionary<string, object?>
        {
            [FindAirMessageHeaders.StartedAtUnixMilliseconds] = "not-a-timestamp"
        });

        await CreateTransport(publisher, pipeline).SendAsync(unit, CancellationToken.None);

        Assert.False(Assert.Single(publisher.Published).Message.Headers!
            .ContainsKey(FindAirMessageHeaders.StartedAtUnixMilliseconds));
    }

    [Fact]
    public async Task PublishFailureIsRetryableAndCarriesTheException()
    {
        var pipeline = RabbitMqPipeline("asd");
        var failure = new InvalidOperationException("broker unavailable");
        var publisher = new RecordingPublisher { Failure = failure };

        var outcome = await CreateTransport(publisher, pipeline).SendAsync(Unit(pipeline), CancellationToken.None);

        Assert.Equal(DispatchStatus.Retryable, outcome.Status);
        Assert.Same(failure, outcome.Exception);
        Assert.Equal(TelemetryErrorCategory.Publish, outcome.Error);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var pipeline = RabbitMqPipeline("asd");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var publisher = new RecordingPublisher { Failure = new OperationCanceledException(cancellation.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateTransport(publisher, pipeline).SendAsync(Unit(pipeline), cancellation.Token));
    }

    [Fact]
    public async Task UnitForPipelineWithoutAnEnabledRabbitMqDestinationIsRejected()
    {
        var disabled = RabbitMqPipeline("disabled", enabled: false);
        var http = HttpPipeline("algo");
        var publisher = new RecordingPublisher();
        var transport = CreateTransport(publisher, disabled, http);

        var disabledOutcome = await transport.SendAsync(Unit(disabled), CancellationToken.None);
        var httpOutcome = await transport.SendAsync(Unit(http), CancellationToken.None);

        Assert.Equal(DispatchStatus.Rejected, disabledOutcome.Status);
        Assert.Equal(DispatchStatus.Rejected, httpOutcome.Status);
        Assert.Empty(publisher.Published);
    }

    private static RabbitMqDispatchTransport CreateTransport(
        RecordingPublisher publisher,
        params PipelineDefinition[] pipelines)
    {
        var catalog = CreateCatalog(pipelines);
        return new RabbitMqDispatchTransport(catalog, catalog, publisher);
    }

    private sealed class RecordingPublisher : IRabbitMqDestinationPublisher
    {
        public List<(RabbitMqDestination Destination, RabbitMqMessageEnvelope Message)> Published { get; } = [];
        public Exception? Failure { get; init; }

        public Task PublishAsync(
            RabbitMqDestination destination,
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            Published.Add((destination, message));
            return Task.CompletedTask;
        }
    }
}
