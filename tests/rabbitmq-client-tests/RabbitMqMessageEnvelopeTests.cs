using System.Security.Cryptography;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqMessageEnvelopeTests
{
    [Fact]
    public void FromUtf8CreatesBodyWithGeneratedMessageId()
    {
        var message = RabbitMqMessageEnvelope.FromUtf8("hello");

        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
        Assert.Equal("application/json", message.ContentType);
        Assert.Equal("hello", message.BodyAsUtf8());
    }

    [Fact]
    public void FromUtf8UsesProvidedMessageId()
    {
        var message = RabbitMqMessageEnvelope.FromUtf8("hello", "message-1");

        Assert.Equal("message-1", message.MessageId);
        Assert.Equal(Encoding.UTF8.GetBytes("hello"), message.Body);
    }

    [Fact]
    public void DeliveryFactoryDerivesStableMessageIdFromBodyWhenMissing()
    {
        var body = Encoding.UTF8.GetBytes("hello");
        var args = new BasicDeliverEventArgs(
            "consumer",
            1,
            redelivered: false,
            exchange: string.Empty,
            routingKey: "input",
            properties: new BasicProperties(),
            body: body,
            cancellationToken: CancellationToken.None);

        var delivery = RabbitMqDeliveryFactory.Create(args);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)), delivery.Message.MessageId);
    }

    [Fact]
    public void DeliveryFactorySeedsPipelineStartHeaderWhenAbsent()
    {
        var args = DeliveryArgs(new BasicProperties());

        var delivery = RabbitMqDeliveryFactory.Create(args);

        Assert.NotNull(delivery.Message.Headers);
        Assert.IsType<long>(delivery.Message.Headers["findair-started-at-unix-ms"]);
        Assert.Null(delivery.PublishedToDeliverySeconds);
    }

    [Fact]
    public void DeliveryFactoryRepairsMalformedPipelineStartHeader()
    {
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["findair-started-at-unix-ms"] = "external-clock"
            }
        };

        var delivery = RabbitMqDeliveryFactory.Create(DeliveryArgs(properties));

        Assert.IsType<long>(delivery.Message.Headers!["findair-started-at-unix-ms"]);
        Assert.Null(delivery.PublishedToDeliverySeconds);
    }

    [Fact]
    public void DeliveryFactoryComputesBrokerDelayFromPerHopTimestamp()
    {
        var publishedAt = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["findair-published-at-unix-ms"] = publishedAt,
                ["findair-started-at-unix-ms"] = publishedAt - 10_000
            }
        };

        var delivery = RabbitMqDeliveryFactory.Create(DeliveryArgs(properties));

        Assert.NotNull(delivery.PublishedToDeliverySeconds);
        Assert.InRange(delivery.PublishedToDeliverySeconds.Value, 0.9, 5);
        Assert.Equal(
            publishedAt - 10_000,
            delivery.Message.Headers!["findair-started-at-unix-ms"]);
    }

    [Theory]
    [InlineData("input.exchange", "input.key", "input.exchange")]
    [InlineData("output.exchange", "output.key", "output.exchange")]
    [InlineData("retry.exchange", "retry.key", "retry.exchange")]
    [InlineData("customer-controlled.exchange", "customer-controlled.key", "other")]
    public void TelemetryDestinationIsBounded(string exchange, string routingKey, string expected)
    {
        var options = new RabbitMqClientOptions
        {
            InputExchange = "input.exchange",
            InputQueue = "input.queue",
            InputRoutingKey = "input.key",
            OutputExchange = "output.exchange",
            OutputQueue = "output.queue",
            OutputRoutingKey = "output.key",
            RetryExchange = "retry.exchange",
            RetryQueue = "retry.queue",
            RetryRoutingKey = "retry.key",
            DeadLetterExchange = "dead.exchange",
            DeadLetterQueue = "dead.queue",
            DeadLetterRoutingKey = "dead.key"
        };

        Assert.Equal(expected, RabbitMqTelemetryDimensions.Destination(options, exchange, routingKey));
    }

    [Fact]
    public void TelemetryDestinationUsesQueueForConfiguredDefaultExchange()
    {
        var options = new RabbitMqClientOptions
        {
            InputExchange = string.Empty,
            InputQueue = "input.queue",
            InputRoutingKey = "input.queue"
        };

        Assert.Equal(
            "input.queue",
            RabbitMqTelemetryDimensions.Destination(options, string.Empty, "input.queue"));
    }

    [Fact]
    public void EnvelopeCanCarryHeadersAndCorrelationId()
    {
        var headers = new Dictionary<string, object?>
        {
            ["tenant"] = "int",
            ["attempt"] = 1
        };

        var message = new RabbitMqMessageEnvelope(
            "message-1",
            Encoding.UTF8.GetBytes("hello"),
            "text/plain",
            headers,
            "correlation-1");

        Assert.Equal("message-1", message.MessageId);
        Assert.Equal("text/plain", message.ContentType);
        Assert.Equal(headers, message.Headers);
        Assert.Equal("correlation-1", message.CorrelationId);
        Assert.Equal("hello", message.BodyAsUtf8());
    }

    [Fact]
    public void RecordWithExpressionPreservesMetadata()
    {
        var original = new RabbitMqMessageEnvelope(
            "message-1",
            Encoding.UTF8.GetBytes("old"),
            "text/plain",
            new Dictionary<string, object?> { ["key"] = "value" },
            "correlation-1");

        var changed = original with { Body = Encoding.UTF8.GetBytes("new") };

        Assert.Equal("new", changed.BodyAsUtf8());
        Assert.Equal(original.MessageId, changed.MessageId);
        Assert.Equal(original.ContentType, changed.ContentType);
        Assert.Equal(original.Headers, changed.Headers);
        Assert.Equal(original.CorrelationId, changed.CorrelationId);
    }

    [Fact]
    public void ProcessingResultSuccessCarriesOutputBody()
    {
        var body = Encoding.UTF8.GetBytes("ok");

        var result = RabbitMqMessageProcessingResult.Success(body);

        Assert.True(result.IsSuccess);
        Assert.Equal(body, result.OutputBody);
        Assert.Null(result.Error);
        Assert.Null(result.OutputMessages);
    }

    [Fact]
    public void ProcessingResultSuccessAllowsEmptyOutputBody()
    {
        var body = Array.Empty<byte>();

        var result = RabbitMqMessageProcessingResult.Success(body);

        Assert.True(result.IsSuccess);
        Assert.Same(body, result.OutputBody);
        Assert.Null(result.Error);
        Assert.Null(result.OutputMessages);
    }

    [Fact]
    public void ProcessingResultSuccessCarriesOutputMessages()
    {
        var outputs = new[]
        {
            RabbitMqMessageEnvelope.FromUtf8("first", "output-1"),
            RabbitMqMessageEnvelope.FromUtf8("second", "output-2")
        };

        var result = RabbitMqMessageProcessingResult.Success(outputs);

        Assert.True(result.IsSuccess);
        Assert.Null(result.OutputBody);
        Assert.Null(result.Error);
        Assert.Same(outputs, result.OutputMessages);
    }

    [Fact]
    public void ProcessingResultFailureCarriesError()
    {
        var result = RabbitMqMessageProcessingResult.Failure("bad");

        Assert.False(result.IsSuccess);
        Assert.Null(result.OutputBody);
        Assert.Null(result.OutputMessages);
        Assert.Equal("bad", result.Error);
        Assert.Equal(RabbitMqMessageFailureAction.DeadLetter, result.FailureAction);
    }

    [Fact]
    public void ProcessingResultRetryableFailureCarriesRetryAction()
    {
        var result = RabbitMqMessageProcessingResult.RetryableFailure("temporary");

        Assert.False(result.IsSuccess);
        Assert.Equal("temporary", result.Error);
        Assert.Equal(RabbitMqMessageFailureAction.Retry, result.FailureAction);
    }

    [Fact]
    public void RetryMessageBuilderAddsRetryCountHeaderWhenMissing()
    {
        var message = RabbitMqMessageEnvelope.FromUtf8("""{"payload":{"id":"image-1"}}""", "message-1");

        var result = RabbitMqRetryMessageBuilder.Build(message, "x-retry-count", maxRetryAttempts: 3);

        Assert.Equal(RabbitMqRetryBuildStatus.Retry, result.Status);
        Assert.Equal(0, result.CurrentRetryCount);
        Assert.Equal(1, result.NextRetryCount);
        Assert.NotNull(result.Message);
        Assert.Equal(message.Body, result.Message!.Body);
        Assert.Equal(1, result.Message.Headers?["x-retry-count"]);
    }

    [Fact]
    public void RetryMessageBuilderIncrementsExistingRetryCount()
    {
        var message = new RabbitMqMessageEnvelope(
            "message-1",
            Encoding.UTF8.GetBytes("""{"payload":{}}"""),
            Headers: new Dictionary<string, object?>
            {
                ["x-retry-count"] = 2
            });

        var result = RabbitMqRetryMessageBuilder.Build(message, "x-retry-count", maxRetryAttempts: 3);

        Assert.Equal(RabbitMqRetryBuildStatus.Retry, result.Status);
        Assert.Equal(2, result.CurrentRetryCount);
        Assert.Equal(3, result.NextRetryCount);
        Assert.Equal(3, result.Message!.Headers?["x-retry-count"]);
    }

    [Fact]
    public void RetryMessageBuilderStopsAtMaxAttempts()
    {
        var message = new RabbitMqMessageEnvelope(
            "message-1",
            Encoding.UTF8.GetBytes("""{"payload":{}}"""),
            Headers: new Dictionary<string, object?>
            {
                ["x-retry-count"] = 3
            });

        var result = RabbitMqRetryMessageBuilder.Build(message, "x-retry-count", maxRetryAttempts: 3);

        Assert.Equal(RabbitMqRetryBuildStatus.AttemptsExhausted, result.Status);
        Assert.Null(result.Message);
        Assert.Equal(3, result.CurrentRetryCount);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("-1")]
    public void RetryMessageBuilderRejectsInvalidRetryCountHeader(string retryCount)
    {
        var message = new RabbitMqMessageEnvelope(
            "message-1",
            Encoding.UTF8.GetBytes("""{"payload":{}}"""),
            Headers: new Dictionary<string, object?>
            {
                ["x-retry-count"] = retryCount
            });

        var result = RabbitMqRetryMessageBuilder.Build(message, "x-retry-count", maxRetryAttempts: 3);

        Assert.Equal(RabbitMqRetryBuildStatus.InvalidMessage, result.Status);
        Assert.Null(result.Message);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void RetryMessageBuilderReadsBrokerByteArrayRetryCountHeader()
    {
        var message = new RabbitMqMessageEnvelope(
            "message-1",
            Encoding.UTF8.GetBytes("""{"payload":{}}"""),
            Headers: new Dictionary<string, object?>
            {
                ["x-retry-count"] = Encoding.UTF8.GetBytes("1")
            });

        var result = RabbitMqRetryMessageBuilder.Build(message, "x-retry-count", maxRetryAttempts: 3);

        Assert.Equal(RabbitMqRetryBuildStatus.Retry, result.Status);
        Assert.Equal(1, result.CurrentRetryCount);
        Assert.Equal(2, result.NextRetryCount);
        Assert.Equal(2, result.Message!.Headers?["x-retry-count"]);
    }

    [Fact]
    public void RetryMessageBuilderReadsCaseInsensitiveHeaderAndWritesOneCanonicalValue()
    {
        var message = new RabbitMqMessageEnvelope(
            "message-1",
            Encoding.UTF8.GetBytes("{}"),
            Headers: new Dictionary<string, object?>
            {
                ["X-RETRY-COUNT"] = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("2"))
            });

        var result = RabbitMqRetryMessageBuilder.Build(message, "x-retry-count", maxRetryAttempts: 3);

        Assert.Equal(RabbitMqRetryBuildStatus.Retry, result.Status);
        Assert.Equal(2, result.CurrentRetryCount);
        Assert.Equal(3, result.NextRetryCount);
        Assert.NotNull(result.Message?.Headers);
        Assert.DoesNotContain("X-RETRY-COUNT", result.Message.Headers.Keys);
        Assert.Equal(3, result.Message.Headers["x-retry-count"]);
    }

    [Fact]
    public void RetryMessageBuilderRejectsConflictingCaseVariantHeaders()
    {
        var message = new RabbitMqMessageEnvelope(
            "message-1",
            Encoding.UTF8.GetBytes("{}"),
            Headers: new Dictionary<string, object?>
            {
                ["x-retry-count"] = 1,
                ["X-RETRY-COUNT"] = 2
            });

        var result = RabbitMqRetryMessageBuilder.Build(message, "x-retry-count", maxRetryAttempts: 3);

        Assert.Equal(RabbitMqRetryBuildStatus.InvalidMessage, result.Status);
        Assert.Null(result.Message);
    }

    [Fact]
    public void OutputPublishRetryResetCanonicalizesCaseAndStartsTheNextServiceAtZero()
    {
        var headers = new Dictionary<string, object?>
        {
            ["x-retry-count"] = 1,
            ["X-RETRY-COUNT"] = Encoding.UTF8.GetBytes("2"),
            ["business-header"] = "preserved"
        };

        RabbitMqPublisher.ResetRetryCount(headers, "x-retry-count");

        Assert.DoesNotContain("X-RETRY-COUNT", headers.Keys);
        Assert.Equal(0, headers["x-retry-count"]);
        Assert.Equal("preserved", headers["business-header"]);
    }

    private static BasicDeliverEventArgs DeliveryArgs(IReadOnlyBasicProperties properties) =>
        new(
            "consumer",
            1,
            redelivered: false,
            exchange: string.Empty,
            routingKey: "input",
            properties,
            body: Encoding.UTF8.GetBytes("hello"),
            cancellationToken: CancellationToken.None);
}
