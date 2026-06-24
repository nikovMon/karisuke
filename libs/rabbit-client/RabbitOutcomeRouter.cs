using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitClient;

internal sealed class RabbitOutcomeRouter
{
    private readonly IRabbitPublisher _publisher;
    private readonly RabbitClientOptions _options;
    private readonly ILogger<RabbitOutcomeRouter> _logger;

    public RabbitOutcomeRouter(
        IRabbitPublisher publisher,
        IOptions<RabbitClientOptions> options,
        ILogger<RabbitOutcomeRouter> logger)
    {
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task CompleteAsync(
        IChannel channel,
        RabbitDelivery delivery,
        RabbitMessageProcessingResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            if (result.IsSuccess)
            {
                if (result.OutputBody is not null)
                {
                    var output = delivery.Message with { Body = result.OutputBody };
                    await _publisher.PublishAsync(_options.OutputQueue, output, cancellationToken);
                }

                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
                RabbitClientDiagnostics.AckedMessages.Add(1,
                    RabbitClientDiagnostics.Tag("queue", _options.InputQueue),
                    RabbitClientDiagnostics.Tag("outcome", "success"));
                _logger.LogInformation("Processed RabbitMQ message {MessageId}", delivery.Message.MessageId);
                return;
            }

            var retryCount = RabbitDeliveryFactory.RetryCount(delivery.Message);
            if (_options.RequeueOnFailure && retryCount < _options.MaxRetryCount)
            {
                var retryHeaders = CopyHeaders(delivery.Message);
                retryHeaders["x-imagingpipeline-retry-count"] = retryCount + 1;
                var retry = delivery.Message with { Headers = retryHeaders };
                await _publisher.PublishToInputAsync(retry, cancellationToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
                RabbitClientDiagnostics.RetriedMessages.Add(1, RabbitClientDiagnostics.Tag("queue", _options.InputQueue));
                RabbitClientDiagnostics.AckedMessages.Add(1,
                    RabbitClientDiagnostics.Tag("queue", _options.InputQueue),
                    RabbitClientDiagnostics.Tag("outcome", "retry"));
                _logger.LogWarning("Requeued RabbitMQ message {MessageId}; retry {RetryCount} of {MaxRetryCount}",
                    delivery.Message.MessageId, retryCount + 1, _options.MaxRetryCount);
                return;
            }

            var errorHeaders = CopyHeaders(delivery.Message);
            errorHeaders["x-imagingpipeline-error"] = result.Error ?? "Message processing failed";
            errorHeaders["x-imagingpipeline-retry-count"] = retryCount;
            var failed = delivery.Message with { Headers = errorHeaders };
            await _publisher.PublishAsync(_options.ErrorQueue, failed, cancellationToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
            RabbitClientDiagnostics.ErrorMessages.Add(1, RabbitClientDiagnostics.Tag("queue", _options.ErrorQueue));
            RabbitClientDiagnostics.AckedMessages.Add(1,
                RabbitClientDiagnostics.Tag("queue", _options.InputQueue),
                RabbitClientDiagnostics.Tag("outcome", "error"));
            _logger.LogWarning("Routed RabbitMQ message {MessageId} to error queue {ErrorQueue}",
                delivery.Message.MessageId, _options.ErrorQueue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not safely complete RabbitMQ message {MessageId}; nacking for requeue",
                delivery.Message.MessageId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
            RabbitClientDiagnostics.NackedMessages.Add(1,
                RabbitClientDiagnostics.Tag("queue", _options.InputQueue),
                RabbitClientDiagnostics.Tag("requeue", true));
        }
    }

    private static Dictionary<string, object?> CopyHeaders(RabbitMessageEnvelope message) =>
        message.Headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(message.Headers, StringComparer.Ordinal);
}
