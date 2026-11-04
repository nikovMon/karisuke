using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqEnrichedFailureTests
{
    private static readonly RabbitMqFailure Timeout = new(
        RabbitMqFailureKind.Transient, "pm-timeout", "Projection request timed out.", "projection-mapper", "i2g", 504);

    [Fact]
    public async Task FinalFailureWaitsForConfirmedRetentionBeforeAcknowledgingSource()
    {
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RabbitMqMessageEnvelope? retained = null;
        var publisher = new FailurePublisher(message =>
        {
            retained = message;
            return confirmed.Task;
        });
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicAckAsync(42, false, It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        var original = Input();
        var router = CreateRouter(publisher);

        var completion = router.CompleteAsync(channel.Object, new(42, original, false, null),
            RabbitMqMessageProcessingResult.RetryableFailure(Timeout), CancellationToken.None);

        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(retained);
        Assert.Same(original.Body, retained.Body);
        Assert.Equal(original.MessageId, retained.MessageId);
        Assert.Equal(2, retained.Headers![RabbitMqFailureHeaders.RedriveCount]);
        Assert.Same(original.Headers!["x-updated-fields"], retained.Headers["x-updated-fields"]);
        Assert.True(RabbitMqFailureHeaders.TryReadFailure(retained.Headers, out var failure));
        Assert.Equal(Timeout, failure);
        confirmed.SetResult();
        await completion;
        channel.Verify(c => c.BasicAckAsync(42, false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailedRetentionLeavesSourceUnacknowledgedAndNeverFallsBackToBrokerDeadLettering()
    {
        var publisher = new FailurePublisher(_ => Task.FromException(new IOException("Target unavailable.")));
        var channel = new Mock<IChannel>();

        await Assert.ThrowsAsync<RabbitMqMessageCompletionException>(() => CreateRouter(publisher).CompleteAsync(
            channel.Object, new(42, Input(), false, null), RabbitMqMessageProcessingResult.RetryableFailure(Timeout), CancellationToken.None));

        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShortRetryCarriesFailureHistoryAndRedriveBudgetWithoutChangingBody()
    {
        var publisher = new Mock<IRabbitMqPublisher>();
        RabbitMqMessageEnvelope? retry = null;
        publisher.Setup(p => p.PublishAsync("retry-x", "retry", It.IsAny<RabbitMqMessageEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, RabbitMqMessageEnvelope, CancellationToken>((_, _, message, _) => retry = message)
            .Returns(Task.CompletedTask);
        var options = Settings();
        var input = Input() with
        {
            Headers = new Dictionary<string, object?>
            {
                [RabbitMqFailureHeaders.RedriveCount] = 2,
                [RabbitMqFailureHeaders.FirstFailureAt] = "2026-10-01T00:00:00.0000000+00:00",
                ["retry-count"] = 1
            }
        };
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicAckAsync(42, false, It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        var router = new RabbitMqOutcomeRouter(publisher.Object, Options.Create(options), NullLogger<RabbitMqOutcomeRouter>.Instance);

        await router.CompleteAsync(channel.Object, new(42, input, false, null),
            RabbitMqMessageProcessingResult.RetryableFailure(Timeout), CancellationToken.None);

        Assert.NotNull(retry);
        Assert.Same(input.Body, retry.Body);
        Assert.Equal(2, retry.Headers!["retry-count"]);
        Assert.Equal(2, retry.Headers[RabbitMqFailureHeaders.RedriveCount]);
        Assert.Equal(input.Headers![RabbitMqFailureHeaders.FirstFailureAt], retry.Headers[RabbitMqFailureHeaders.FirstFailureAt]);
        Assert.Equal("transient", retry.Headers[RabbitMqFailureHeaders.FailureClass]);
    }

    [Fact]
    public async Task FailureDestinationSelectsExplicitRemoteBrokerWithoutDeclaringTopology()
    {
        var options = Settings();
        options.InputCluster = new() { Host = "input.example", VirtualHost = "/input" };
        options.FailureDestination = new()
        {
            Queue = "remote.dlq",
            Connection = new() { Host = "failure.example", Port = 5673, VirtualHost = "/failures", Username = "retainer", Password = "example" }
        };
        var transport = new Mock<IRabbitMqDestinationPublisher>();
        RabbitMqDestination? actual = null;
        transport.Setup(p => p.PublishAsync(It.IsAny<RabbitMqDestination>(), It.IsAny<RabbitMqMessageEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<RabbitMqDestination, RabbitMqMessageEnvelope, CancellationToken>((destination, _, _) => actual = destination)
            .Returns(Task.CompletedTask);

        await new RabbitMqFailurePublisher(transport.Object, Options.Create(options)).PublishAsync(Input(), CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal("failure.example", actual.Connection.Host);
        Assert.Equal("/failures", actual.Connection.VirtualHost);
        Assert.Equal("remote.dlq", actual.QueueName);
        Assert.Equal(string.Empty, actual.ExchangeName);
        Assert.False(actual.DeclareTopology);
    }

    [Fact]
    public async Task DefaultFailureDestinationUsesInputClusterAndConfiguredDlq()
    {
        var options = Settings();
        options.Host = "output.example";
        options.InputCluster = new() { Host = "input.example", VirtualHost = "/input" };
        var transport = new Mock<IRabbitMqDestinationPublisher>();
        transport.Setup(p => p.PublishAsync(It.Is<RabbitMqDestination>(d =>
                d.Connection.Host == "input.example" && d.Connection.VirtualHost == "/input" && d.QueueName == "dlq" && !d.DeclareTopology),
            It.IsAny<RabbitMqMessageEnvelope>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask).Verifiable();

        await new RabbitMqFailurePublisher(transport.Object, Options.Create(options)).PublishAsync(Input(), CancellationToken.None);
        transport.Verify();
    }

    [Fact]
    public void MalformedOrConflictingRedriveCountersAreRejected()
    {
        Assert.False(RabbitMqFailureHeaders.TryReadNonNegativeInt(new Dictionary<string, object?> { ["redrive-count"] = null }, "redrive-count", out _));
        Assert.False(RabbitMqFailureHeaders.TryReadNonNegativeInt(new Dictionary<string, object?> { ["redrive-count"] = 1, ["REDRIVE-COUNT"] = 2 }, "redrive-count", out _));
        Assert.True(RabbitMqFailureHeaders.TryReadNonNegativeInt(new Dictionary<string, object?> { ["redrive-count"] = Encoding.UTF8.GetBytes("3") }, "redrive-count", out var value));
        Assert.Equal(3, value);
    }

    [Fact]
    public void OldStringFailuresAreConservativelyUnknown()
    {
        Assert.Equal(RabbitMqFailureKind.Unknown, RabbitMqMessageProcessingResult.RetryableFailure("timeout").FailureDetails!.Kind);
        Assert.Equal(RabbitMqFailureKind.Unknown, RabbitMqMessageProcessingResult.NonRetryableFailure("bad").FailureDetails!.Kind);
    }

    private static RabbitMqOutcomeRouter CreateRouter(IRabbitMqFailurePublisher publisher) =>
        new(Mock.Of<IRabbitMqPublisher>(), Options.Create(Settings()), NullLogger<RabbitMqOutcomeRouter>.Instance, publisher);

    private sealed class FailurePublisher(Func<RabbitMqMessageEnvelope, Task> publish) : IRabbitMqFailurePublisher
    {
        public Task PublishAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken) => publish(message);
    }

    private static RabbitMqClientOptions Settings() => new()
    {
        InputQueue = "input",
        DeadLetterQueue = "dlq",
        RetryExchange = "retry-x",
        RetryQueue = "retry",
        EnrichedDeadLetterEnabled = true,
        FailureServiceName = "test-service"
    };

    private static RabbitMqMessageEnvelope Input() => new("original", "{\"geometry\":[1,2],\"valid\":true}"u8.ToArray(), Headers: new Dictionary<string, object?>
    {
        ["retry-count"] = 3,
        [RabbitMqFailureHeaders.RedriveCount] = 2,
        ["x-updated-fields"] = new List<object> { Encoding.UTF8.GetBytes("gridType") }
    });
}
