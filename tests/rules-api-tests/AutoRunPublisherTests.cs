using System.Text.Json;
using ImagingPipeline.Common.Dtos.Historical;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunPublisherTests
{
    [Fact]
    public async Task ConfirmationCreatesVersionedCorrelatedEnvelopeWithStableRunScopedId()
    {
        var transport = new Transport();
        var destination = Destination();
        var publisher = new AutoRunPublisher(transport, Options.Create(new AutoRunOptions { Destination = destination }), NullLogger<AutoRunPublisher>.Instance);
        var payload = """{"id":"overlay-1","photoTime":"2020-01-01T00:00:00Z"}"""u8.ToArray();
        AutoRunResolvedTarget[] targets = [new(
            new() { PipelineId = "pipeline", RuleId = "rule", MaxOverlays = 1 },
            new PipelineRuleDocument { Id = "rule", RuleName = "Saved rule", MatchAll = true, RunParams = [] },
            new("rule", new Dictionary<string, ImagingPipeline.RuleEngine.Rules.SensorMatchCriteria>(), 0, double.MaxValue, null, null, null, []),
            new FindAirPipelineContract())];

        var first = await publisher.PublishAsync("run-1", payload, targets, CancellationToken.None);
        await publisher.PublishAsync("run-1", payload, targets, CancellationToken.None);
        await publisher.PublishAsync("run-2", payload, targets, CancellationToken.None);

        Assert.Equal(AutoRunItemOutcome.Confirmed, first.Outcome);
        Assert.All(transport.Destinations, used => Assert.Same(destination, used));
        Assert.Equal(transport.Messages[0].MessageId, transport.Messages[1].MessageId);
        Assert.NotEqual(transport.Messages[0].MessageId, transport.Messages[2].MessageId);
        Assert.Equal("run-1", transport.Messages[0].CorrelationId);
        Assert.Equal("application/json", transport.Messages[0].ContentType);
        using var body = JsonDocument.Parse(transport.Messages[0].Body);
        Assert.Equal(HistoricalOverlayEnvelope.MessageKind, body.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("run-1", body.RootElement.GetProperty("runId").GetString());
        Assert.Equal("overlay-1", body.RootElement.GetProperty("overlay").GetProperty("id").GetString());
        Assert.Equal("pipeline", Assert.Single(body.RootElement.GetProperty("targets").EnumerateArray()).GetProperty("pipelineId").GetString());
        Assert.Null(transport.Messages[0].Headers);
    }

    [Fact]
    public async Task NoTargetsPublishesExactOrdinaryGatewayBodyAndUpdatedFieldsWithStableIdentity()
    {
        var transport = new Transport();
        var destination = Destination();
        var publisher = new AutoRunPublisher(transport, Options.Create(new AutoRunOptions { Destination = destination }), NullLogger<AutoRunPublisher>.Instance);
        var payload = """{ "id": "overlay-1", "photoTime": "2000-01-01T00:00:00Z", "gridType": "grid", "extra": [1,2] }"""u8.ToArray();

        var first = await publisher.PublishAsync("run-1", payload, [], CancellationToken.None);
        await publisher.PublishAsync("run-1", payload, [], CancellationToken.None);
        await publisher.PublishAsync("run-2", payload, [], CancellationToken.None);

        Assert.Equal(AutoRunItemOutcome.Confirmed, first.Outcome);
        Assert.All(transport.Destinations, used => Assert.Same(destination, used));
        Assert.All(transport.Messages, message =>
        {
            Assert.Equal(payload, message.Body);
            Assert.Equal("application/json", message.ContentType);
            var fields = Assert.IsAssignableFrom<IList<object>>(message.Headers!["x-updated-fields"]);
            Assert.Equal("gridType", Assert.Single(fields));
            using var document = JsonDocument.Parse(message.Body);
            Assert.False(document.RootElement.TryGetProperty("kind", out _));
            Assert.False(document.RootElement.TryGetProperty("targets", out _));
        });
        Assert.Equal("run-1", transport.Messages[0].CorrelationId);
        Assert.Equal(transport.Messages[0].MessageId, transport.Messages[1].MessageId);
        Assert.NotEqual(transport.Messages[0].MessageId, transport.Messages[2].MessageId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KnownBrokerRejectionsAreFailedAndNeverRetried(bool returned)
    {
        var transport = new Transport(new PublishException(1, returned));
        var publisher = new AutoRunPublisher(transport, Options.Create(new AutoRunOptions { Destination = Destination() }), NullLogger<AutoRunPublisher>.Instance);

        var result = await publisher.PublishAsync("run", """{"id":"one"}"""u8.ToArray(), [], CancellationToken.None);

        Assert.Equal(AutoRunItemOutcome.Failed, result.Outcome);
        Assert.Contains(returned ? "unroutable" : "negatively", result.Error!);
        Assert.Single(transport.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostConfirmationOrInterruptedPublicationIsUncertainAndNeverRetried(bool interrupted)
    {
        var transport = new Transport(interrupted
            ? new OperationCanceledException()
            : new IOException("Connection lost after sending."));
        var publisher = new AutoRunPublisher(transport, Options.Create(new AutoRunOptions { Destination = Destination() }), NullLogger<AutoRunPublisher>.Instance);

        var result = await publisher.PublishAsync("run", """{"id":"one"}"""u8.ToArray(), [], CancellationToken.None);

        Assert.Equal(AutoRunItemOutcome.Uncertain, result.Outcome);
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task BrokerFailureLogsOriginalExceptionAndRunWithoutExposingDetailsInResult()
    {
        var error = new OperationInterruptedException(new ShutdownEventArgs(
            ShutdownInitiator.Peer, 406, "PRECONDITION_FAILED - inequivalent queue argument"));
        var transport = new Transport(error);
        var logs = new RecordingLogger<AutoRunPublisher>();
        var publisher = new AutoRunPublisher(transport,
            Options.Create(new AutoRunOptions { Destination = Destination() }), logs);

        var result = await publisher.PublishAsync("run-diagnostic",
            """{"id":"one","imageUrl":"https://image.invalid/?token=private-payload-value"}"""u8.ToArray(),
            [], CancellationToken.None);

        Assert.Equal(AutoRunItemOutcome.Uncertain, result.Outcome);
        Assert.Contains("Check Rules API logs", result.Error!);
        Assert.DoesNotContain("PRECONDITION_FAILED", result.Error!);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(error, entry.Exception);
        Assert.Equal("run-diagnostic", entry.Properties["AutoRunId"]);
        Assert.Equal(Assert.Single(transport.Messages).MessageId, entry.Properties["MessageId"]);
        Assert.DoesNotContain("private-payload-value", entry.Message);
        Assert.DoesNotContain("secret", entry.Message);
    }

    [Fact]
    public async Task BadPayloadAndMissingDestinationFailWithoutEnteringTheBroker()
    {
        var transport = new Transport();
        var publisher = new AutoRunPublisher(transport, Options.Create(new AutoRunOptions { Destination = Destination() }), NullLogger<AutoRunPublisher>.Instance);
        Assert.Equal(AutoRunItemOutcome.Failed,
            (await publisher.PublishAsync("run", "{}"u8.ToArray(), [], CancellationToken.None)).Outcome);
        publisher = new AutoRunPublisher(transport, Options.Create(new AutoRunOptions()), NullLogger<AutoRunPublisher>.Instance);
        Assert.Equal(AutoRunItemOutcome.Failed,
            (await publisher.PublishAsync("run", """{"id":"one"}"""u8.ToArray(), [], CancellationToken.None)).Outcome);
        Assert.Empty(transport.Messages);
    }

    [Fact]
    public void ConfigurationBindsExactTopologyAndNormalizesArgumentTypes()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoRun:Enabled"] = "true",
            ["AutoRun:Provider:BaseUrl"] = "http://provider.invalid",
            ["AutoRun:Destination:Connection:Name"] = "gateway",
            ["AutoRun:Destination:Connection:Host"] = "broker.invalid",
            ["AutoRun:Destination:Connection:Port"] = "5672",
            ["AutoRun:Destination:Connection:Username"] = "user",
            ["AutoRun:Destination:Connection:Password"] = "test",
            ["AutoRun:Destination:Connection:VirtualHost"] = "/historical",
            ["AutoRun:Destination:QueueName"] = "gateway.input",
            ["AutoRun:Destination:ExchangeName"] = "gateway.exchange",
            ["AutoRun:Destination:ExchangeType"] = "topic",
            ["AutoRun:Destination:RoutingKey"] = "overlay.update",
            ["AutoRun:Destination:BindQueueToExchange"] = "true",
            ["AutoRun:Destination:QueueArguments:x-message-ttl"] = "60000",
            ["AutoRun:Destination:QueueArguments:x-queue-type"] = "quorum",
            ["AutoRun:Destination:QueueArguments:x-dead-letter-routing-key"] = "123",
            ["AutoRun:Destination:ExchangeArguments:alternate-exchange"] = "returns",
            ["AutoRun:Destination:BindingArguments:custom-flag"] = "true"
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddAutoRuns(configuration).BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<AutoRunOptions>>().Value;

        Assert.True(options.IsValid());
        Assert.Equal("/historical", options.Destination!.Connection.VirtualHost);
        Assert.Equal("gateway.input", options.Destination.QueueName);
        Assert.Equal("topic", options.Destination.ExchangeType);
        Assert.Equal("overlay.update", options.Destination.RoutingKey);
        Assert.True(options.Destination.BindQueueToExchange);
        Assert.Equal(60000, options.Destination.QueueArguments["x-message-ttl"]);
        Assert.Equal("quorum", options.Destination.QueueArguments["x-queue-type"]);
        Assert.Equal("123", options.Destination.QueueArguments["x-dead-letter-routing-key"]);
        Assert.Equal("returns", options.Destination.ExchangeArguments["alternate-exchange"]);
        Assert.Equal(true, options.Destination.BindingArguments["custom-flag"]);
    }

    private static RabbitMqDestination Destination() => new()
    {
        Connection = new("gateway", "broker.invalid", 5672, "user", "test", "/"),
        QueueName = "gateway.input"
    };

    private sealed class Transport(Exception? error = null) : IRabbitMqDestinationPublisher
    {
        public List<RabbitMqMessageEnvelope> Messages { get; } = [];
        public List<RabbitMqDestination> Destinations { get; } = [];
        public Task PublishAsync(RabbitMqDestination destination, RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            Destinations.Add(destination);
            return error is null ? Task.CompletedTask : Task.FromException(error);
        }
    }
}
