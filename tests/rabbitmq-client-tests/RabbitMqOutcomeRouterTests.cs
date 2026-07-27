using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
            RabbitMqMessageEnvelope.FromUtf8("input", "message-1"));

        var exception = await Assert.ThrowsAsync<RabbitMqMessageCompletionException>(
            () => router.CompleteAsync(
                channel: null!,
                delivery,
                RabbitMqMessageProcessingResult.Success("output"u8.ToArray()),
                CancellationToken.None));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
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
