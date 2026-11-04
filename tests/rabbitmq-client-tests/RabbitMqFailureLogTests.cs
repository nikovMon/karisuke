using System.Text.Json;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqFailureLogTests
{
    private static readonly RabbitMqFailure Failure = new(RabbitMqFailureKind.Transient, "pm-timeout", "Timed out.", "projection-mapper", "i2g");

    [Fact]
    public void ExcludesConsumerFocusedAndRuleWktWithoutLosingMetadata()
    {
        var message = RabbitMqMessageEnvelope.FromUtf8(
            """{"metadata":{"focusedPxWkt":"POLYGON ((1 2,3 4,1 2))","ruleLocationWkt":"POLYGON ((5 6,7 8,5 6))","sensorName":"sensor","tileId":"tile-1"}}""");

        var json = RabbitMqFailureLog.CreateDiagnosticJson(message, Failure, "consumer", "input", "manual");

        using var document = JsonDocument.Parse(json);
        var metadata = document.RootElement.GetProperty("original").GetProperty("metadata");
        Assert.False(metadata.TryGetProperty("focusedPxWkt", out _));
        Assert.False(metadata.TryGetProperty("ruleLocationWkt", out _));
        Assert.Equal("sensor", metadata.GetProperty("sensorName").GetString());
        Assert.Equal("tile-1", metadata.GetProperty("tileId").GetString());
        Assert.DoesNotContain("POLYGON", json);
    }

    [Fact]
    public void FiltersHeavyFieldsRecursivelyWithoutLosingOtherPropertiesOrMutatingOriginal()
    {
        var body = "{\"name\":\"sample\",\"footprint\":\"POLYGON secret\",\"children\":[{\"roiFootprint\":[1,2],\"value\":7}],\"many\":[" + string.Join(',', Enumerable.Range(0, 100)) + "]}";
        var message = RabbitMqMessageEnvelope.FromUtf8(body, "id");
        var json = RabbitMqFailureLog.CreateDiagnosticJson(message, Failure, "consumer", "input", "manual");
        using var document = JsonDocument.Parse(json);

        var original = document.RootElement.GetProperty("original");
        Assert.Equal("sample", original.GetProperty("name").GetString());
        Assert.Equal(100, original.GetProperty("many").GetArrayLength());
        Assert.Equal(7, original.GetProperty("children")[0].GetProperty("value").GetInt32());
        Assert.False(original.TryGetProperty("footprint", out _));
        Assert.False(original.GetProperty("children")[0].TryGetProperty("roiFootprint", out _));
        Assert.Equal(body, message.BodyAsUtf8());
        Assert.DoesNotContain("POLYGON secret", json);
    }

    [Fact]
    public void MalformedJsonLogsOnlyDiagnosticMetadataAndHash()
    {
        var message = RabbitMqMessageEnvelope.FromUtf8("{malformed-polygon-secret", "id");
        var json = RabbitMqFailureLog.CreateDiagnosticJson(message, Failure, "consumer", "input", "manual");
        using var document = JsonDocument.Parse(json);
        Assert.Equal("invalid-json", document.RootElement.GetProperty("parseFailure").GetString());
        Assert.Equal(64, document.RootElement.GetProperty("originalSha256").GetString()!.Length);
        Assert.False(document.RootElement.TryGetProperty("original", out _));
        Assert.DoesNotContain("malformed-polygon-secret", json);
    }

    [Fact]
    public void SanitizesJsonEmbeddedInFailureMessage()
    {
        var failure = Failure with { Message = "PM returned: {\"footprint\":\"secret\",\"code\":\"shape-error\"}" };
        var json = RabbitMqFailureLog.CreateDiagnosticJson(RabbitMqMessageEnvelope.FromUtf8("{}"), failure, "consumer", "input", "manual");
        Assert.DoesNotContain("secret", json);
        Assert.Contains("shape-error", json);
    }

    [Fact]
    public void LargeNonHeavyFieldsSurviveReconstructableBoundedLogChunks()
    {
        var body = JsonSerializer.Serialize(new { name = new string('x', 20000), values = Enumerable.Range(0, 100) });
        var message = RabbitMqMessageEnvelope.FromUtf8(body, "id");
        var logger = new ChunkLogger();
        RabbitMqFailureLog.Log(logger, message, Failure, "consumer", "input", "manual");

        Assert.True(logger.Chunks.Count > 1);
        var json = string.Concat(logger.Chunks.Select(chunk => (string)chunk.Attributes["failure_log_json_chunk"]!));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new string('x', 20000), document.RootElement.GetProperty("original").GetProperty("name").GetString());
        Assert.Equal(100, document.RootElement.GetProperty("original").GetProperty("values").GetArrayLength());
        Assert.All(logger.Chunks, chunk => Assert.True(((string)chunk.Attributes["failure_log_json_chunk"]!).Length <= 2048));
        Assert.All(logger.Chunks, chunk => Assert.Equal(logger.Chunks.Count, chunk.Attributes["failure_log_chunk_count"]));
    }

    [Fact]
    public void BrokenLoggerNeverBreaksRetentionCompletion()
    {
        RabbitMqFailureLog.Log(new ThrowingLogger(), RabbitMqMessageEnvelope.FromUtf8("{}"), Failure, "consumer", "input", "manual");
    }

    private sealed class ChunkLogger : ILogger
    {
        public List<LosslessJsonLogChunk> Chunks { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Chunks.Add(Assert.IsType<LosslessJsonLogChunk>(state));
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new IOException("Logging transport failed.");
    }
}
