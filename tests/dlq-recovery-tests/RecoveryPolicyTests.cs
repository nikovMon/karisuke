using System.Text;
using ImagingPipeline.RabbitMqClient;
using RabbitMQ.Client;

namespace ImagingPipeline.DlqRecovery.Tests;

public sealed class RecoveryPolicyTests
{
    [Theory]
    [InlineData(RabbitMqFailureKind.Transient, true)]
    [InlineData(RabbitMqFailureKind.InvalidData, false)]
    [InlineData(RabbitMqFailureKind.DependencyResponse, false)]
    [InlineData(RabbitMqFailureKind.Configuration, false)]
    [InlineData(RabbitMqFailureKind.Unknown, false)]
    public void OnlyKnownTransientFailuresAreAutomaticallyReplayed(RabbitMqFailureKind kind, bool replay)
    {
        Assert.Equal(replay, RecoveryPolicy.Decide(Delivery(kind), new RecoveryOptions()).Replay);
    }

    [Fact]
    public void LegacyRetryCountAloneNeverProvesRecoveryEligibility()
    {
        var message = new RecoveryDelivery(1, [1, 2], new BasicProperties
        {
            Headers = new Dictionary<string, object?> { ["retry-count"] = 3 }
        });
        Assert.Equal("unknown-failure", RecoveryPolicy.Decide(message, new RecoveryOptions()).Reason);
    }

    [Fact]
    public void ConflictingOrMalformedRedriveCounterRequiresManualHandling()
    {
        var message = Delivery();
        message.Properties.Headers!["redrive-count"] = 1;
        message.Properties.Headers["REDRIVE-COUNT"] = 2;
        Assert.Equal("invalid-redrive-count", RecoveryPolicy.Decide(message, new RecoveryOptions()).Reason);
        message.Properties.Headers.Remove("REDRIVE-COUNT");
        message.Properties.Headers["redrive-count"] = -1;
        Assert.False(RecoveryPolicy.Decide(message, new RecoveryOptions()).Replay);
    }

    [Fact]
    public void ProlongedOutageConsumesAllowanceThenRetainsTheMessage()
    {
        var options = new RecoveryOptions { MaxRedrives = 3 };
        var message = Delivery();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var decision = RecoveryPolicy.Decide(message, options);
            Assert.True(decision.Replay);
            message = RecoveryPolicy.Prepare(message, decision, Mapping(), $"run-{attempt}", DateTimeOffset.UtcNow);
            Assert.Equal(attempt, message.Properties.Headers!["redrive-count"]);
        }
        var exhausted = RecoveryPolicy.Decide(message, options);
        Assert.False(exhausted.Replay);
        Assert.Equal("redrive-limit", exhausted.Reason);
    }

    [Fact]
    public void ReplayPreservesBodyPropertiesAndTypedHeadersAndResetsOnlyShortRetryBudget()
    {
        var message = Delivery();
        var originalBytes = message.Body.ToArray();
        var updateFields = new object[] { Encoding.UTF8.GetBytes("gridType") };
        message.Properties.Headers!["x-updated-fields"] = updateFields;
        message.Properties.Headers["traceparent"] = "original-trace";
        message.Properties.Headers["retry-count"] = 3;
        message.Properties.Headers["redrive-count"] = 1;
        message.Properties.Expiration = "1000";
        message.Properties.Type = "source-type";
        message.Properties.AppId = "source-app";
        message.Properties.UserId = "external-producer";
        var decision = RecoveryPolicy.Decide(message, new RecoveryOptions());
        var output = RecoveryPolicy.Prepare(message, decision, Mapping(), "run", DateTimeOffset.UtcNow);
        Assert.Equal(originalBytes, output.Body);
        Assert.Equal(message.Properties.MessageId, output.Properties.MessageId);
        Assert.Equal(message.Properties.CorrelationId, output.Properties.CorrelationId);
        Assert.Equal("source-type", output.Properties.Type);
        Assert.Equal("source-app", output.Properties.AppId);
        Assert.Null(output.Properties.UserId);
        Assert.Equal("external-producer", output.Properties.Headers!["original-user-id"]);
        Assert.Equal("external-producer", message.Properties.UserId);
        Assert.Same(updateFields, output.Properties.Headers!["x-updated-fields"]);
        Assert.Equal("original-trace", output.Properties.Headers["traceparent"]);
        Assert.Equal(0, output.Properties.Headers["retry-count"]);
        Assert.Equal(2, output.Properties.Headers["redrive-count"]);
        Assert.True(output.Properties.Persistent);
        Assert.Null(output.Properties.Expiration);
        Assert.Equal("1000", output.Properties.Headers["original-expiration"]);
        Assert.Equal(3, message.Properties.Headers["retry-count"]);
        Assert.Equal("1000", message.Properties.Expiration);
    }

    [Fact]
    public void InvalidMessageRetainsItsFullGeometryAndFailureInformation()
    {
        var message = Delivery(RabbitMqFailureKind.InvalidData);
        var output = RecoveryPolicy.Prepare(message, RecoveryPolicy.Decide(message, new RecoveryOptions()),
            Mapping(), "run", DateTimeOffset.UtcNow);
        Assert.Equal(message.Body, output.Body);
        Assert.Equal("invalid-data", output.Properties.Headers!["failure-class"]);
        Assert.Equal("failure", output.Properties.Headers["failure-message"]);
        Assert.Equal("manual-failure", output.Properties.Headers["manual-retention-reason"]);
        Assert.False(output.Properties.Headers.ContainsKey("redrive-count"));
    }

    [Fact]
    public void InvalidDuplicateMappingAndLoopTargetsFailBeforeConsumption()
    {
        var options = Options();
        options.Queues["other"] = Mapping();
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.Queues.Remove("other");
        options.Queues["test"].ReplayQueue = options.Queues["test"].SourceQueue;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("failure-class")]
    [InlineData("LAST-REDRIVE-AT")]
    [InlineData("dependency")]
    public void ShortRetryHeaderCannotOverwriteRecoveryMetadata(string header)
    {
        var options = Options();
        options.Queues["test"].RetryCountHeader = header;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    internal static RecoveryDelivery Delivery(RabbitMqFailureKind kind = RabbitMqFailureKind.Transient)
    {
        var envelope = RabbitMqFailureHeaders.Enrich(RabbitMqMessageEnvelope.FromUtf8(
            "{\"id\":\"test\",\"footprint\":{\"coordinates\":[[1,2],[3,4]]}}", "original-id"),
            new(kind, "test-failure", "failure", "projection-mapper", "mapping", 503), "test-service");
        return new RecoveryDelivery(1, envelope.Body, new BasicProperties
        {
            MessageId = envelope.MessageId,
            CorrelationId = "original-correlation",
            ContentType = "application/json",
            Headers = new Dictionary<string, object?>(envelope.Headers!)
        });
    }

    internal static RecoveryQueueOptions Mapping() => new()
    {
        SourceConnection = "source",
        ReplayConnection = "source",
        SourceQueue = "test.dlq",
        ReplayQueue = "test.input",
        ManualQueue = "test.manual"
    };

    internal static RecoveryOptions Options() => new()
    {
        DryRun = false,
        MessagesPerSecond = 1000,
        Connections = new()
        {
            ["source"] = new() { Host = "localhost", VirtualHost = "/", Username = "test", Password = "test" }
        },
        Queues = new() { ["test"] = Mapping() }
    };
}
