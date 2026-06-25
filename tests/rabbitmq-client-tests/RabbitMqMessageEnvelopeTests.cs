using System.Text;

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
    }

    [Fact]
    public void ProcessingResultSuccessAllowsEmptyOutputBody()
    {
        var body = Array.Empty<byte>();

        var result = RabbitMqMessageProcessingResult.Success(body);

        Assert.True(result.IsSuccess);
        Assert.Same(body, result.OutputBody);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ProcessingResultFailureCarriesError()
    {
        var result = RabbitMqMessageProcessingResult.Failure("bad");

        Assert.False(result.IsSuccess);
        Assert.Null(result.OutputBody);
        Assert.Equal("bad", result.Error);
    }
}
