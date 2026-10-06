using System.Reflection;
using System.Text;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqOutcomeRouterTests
{
    [Fact]
    public async Task CompletionInfrastructureFailureEscapesWithoutImmediateRequeueNack()
    {
        var options = Options.Create(
            new RabbitMqClientOptions
            {
                InputQueue = "input",
                OutputQueue = "output",
                DeadLetterQueue = "dlq",
                RetryQueue = "retry"
            });
        var router = new RabbitMqOutcomeRouter(
            new ThrowingPublisher(),
            options,
            NullLogger<RabbitMqOutcomeRouter>.Instance);
        var delivery = new RabbitMqDelivery(
            42,
            RabbitMqMessageEnvelope.FromUtf8("input", "message-1"),
            Redelivered: false,
            PublishedToDeliverySeconds: null);

        var exception = await Assert.ThrowsAsync<RabbitMqMessageCompletionException>(
            () => router.CompleteAsync(
                channel: null!,
                delivery,
                RabbitMqMessageProcessingResult.Success("output"u8.ToArray()),
                CancellationToken.None));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task RetryRepublishPreservesInboundHeadersOfTheRetriedMessage()
    {
        var options = Options.Create(RetryOptions());
        var channel = DispatchProxy.Create<IChannel, PublishRecordingChannel>();
        var recorder = (PublishRecordingChannel)channel;
        await using var pool = new RabbitMqPublisherChannelPool(new FixedChannelConnectionManager(channel), options);
        var router = new RabbitMqOutcomeRouter(
            new RabbitMqPublisher(pool, options),
            options,
            NullLogger<RabbitMqOutcomeRouter>.Instance);
        var delivery = new RabbitMqDelivery(
            42,
            new RabbitMqMessageEnvelope(
                "message-1",
                Encoding.UTF8.GetBytes("{}"),
                Headers: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-updated-fields"] = new List<object> { Encoding.UTF8.GetBytes("gridType") },
                    ["business-header"] = "kept-for-retry",
                    ["x-retry-count"] = 1
                }),
            Redelivered: false,
            PublishedToDeliverySeconds: null);

        var completion = await router.CompleteAsync(
            channel,
            delivery,
            RabbitMqMessageProcessingResult.RetryableFailure("transient"),
            CancellationToken.None);

        Assert.Equal(TelemetryOutcome.Retry, completion.Outcome);
        var published = Assert.Single(recorder.Published);
        Assert.Equal("retry-x", published.Exchange);
        Assert.Equal("retry", published.RoutingKey);
        var headers = Assert.IsAssignableFrom<IDictionary<string, object?>>(published.Properties.Headers);
        var updatedFields = Assert.IsAssignableFrom<IList<object>>(headers["x-updated-fields"]);
        Assert.Equal("gridType", Encoding.UTF8.GetString(Assert.IsType<byte[]>(Assert.Single(updatedFields))));
        Assert.Equal("kept-for-retry", headers["business-header"]);
        Assert.Equal(2, headers["x-retry-count"]);
        Assert.Equal([42UL], recorder.Acked);
    }

    [Fact]
    public async Task OutputPublishStillDropsHeadersOutsideTheOutboundWhitelist()
    {
        var options = Options.Create(RetryOptions());
        var channel = DispatchProxy.Create<IChannel, PublishRecordingChannel>();
        var recorder = (PublishRecordingChannel)channel;
        await using var pool = new RabbitMqPublisherChannelPool(new FixedChannelConnectionManager(channel), options);
        var router = new RabbitMqOutcomeRouter(
            new RabbitMqPublisher(pool, options),
            options,
            NullLogger<RabbitMqOutcomeRouter>.Instance);
        var delivery = new RabbitMqDelivery(
            42,
            new RabbitMqMessageEnvelope(
                "message-1",
                Encoding.UTF8.GetBytes("{}"),
                Headers: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-updated-fields"] = new List<object> { Encoding.UTF8.GetBytes("gridType") },
                    ["business-header"] = "drop-me",
                    ["x-retry-count"] = 1
                }),
            Redelivered: false,
            PublishedToDeliverySeconds: null);

        await router.CompleteAsync(
            channel,
            delivery,
            RabbitMqMessageProcessingResult.Success("output"u8.ToArray()),
            CancellationToken.None);

        var published = Assert.Single(recorder.Published);
        Assert.Equal("output-x", published.Exchange);
        var headers = Assert.IsAssignableFrom<IDictionary<string, object?>>(published.Properties.Headers);
        Assert.DoesNotContain("x-updated-fields", headers.Keys);
        Assert.DoesNotContain("business-header", headers.Keys);
        Assert.Equal(0, headers["x-retry-count"]);
    }

    private static RabbitMqClientOptions RetryOptions() =>
        new()
        {
            InputQueue = "input",
            OutputQueue = "output",
            OutputExchange = "output-x",
            DeadLetterQueue = "dlq",
            RetryQueue = "retry",
            RetryExchange = "retry-x",
            RetryCountHeader = "x-retry-count",
            MaxRetryAttempts = 3
        };

    internal sealed record PublishedMessage(string Exchange, string RoutingKey, IReadOnlyBasicProperties Properties);

    private sealed class FixedChannelConnectionManager(IChannel channel) : IRabbitMqPublisherConnectionManager
    {
        public Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = DispatchProxy.Create<IConnection, FixedChannelConnection>();
            ((FixedChannelConnection)connection).Channel = channel;
            return Task.FromResult(connection);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public class FixedChannelConnection : DispatchProxy
    {
        public IChannel? Channel { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IConnection.CreateChannelAsync)
                ? Task.FromResult(Channel!)
                : throw new NotSupportedException($"Unexpected connection call {targetMethod?.Name}.");
    }

    public class PublishRecordingChannel : DispatchProxy
    {
        internal List<PublishedMessage> Published { get; } = [];
        public List<ulong> Acked { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            switch (targetMethod.Name)
            {
                case nameof(IChannel.ExchangeDeclareAsync):
                case nameof(IChannel.QueueBindAsync):
                case nameof(IChannel.CloseAsync):
                    return Task.CompletedTask;
                case nameof(IChannel.QueueDeclareAsync):
                    return Task.FromResult(new QueueDeclareOk((string)args![0]!, 0, 0));
                case nameof(IChannel.BasicPublishAsync):
                    Published.Add(new((string)args![0]!, (string)args[1]!, (IReadOnlyBasicProperties)args[3]!));
                    return ValueTask.CompletedTask;
                case nameof(IChannel.BasicAckAsync):
                    Acked.Add((ulong)args![0]!);
                    return ValueTask.CompletedTask;
                case "get_IsOpen":
                    return true;
                case nameof(IDisposable.Dispose):
                    return null;
                default:
                    throw new NotSupportedException($"Unexpected channel call {targetMethod.Name}.");
            }
        }
    }

    private sealed class ThrowingPublisher : IRabbitMqPublisher
    {
        public Task PublishAsync(
            string exchange,
            string routingKey,
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("RabbitMQ publisher unavailable"));

        public Task PublishToInputAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("RabbitMQ publisher unavailable"));

        public Task PublishToOutputAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("RabbitMQ publisher unavailable"));
    }
}
