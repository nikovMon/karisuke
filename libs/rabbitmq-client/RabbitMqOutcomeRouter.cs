using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient;

internal sealed class RabbitMqOutcomeRouter
{
    private readonly IRabbitMqPublisher _publisher;
    private readonly RabbitMqClientOptions _options;
    private readonly ILogger<RabbitMqOutcomeRouter> _logger;

    public RabbitMqOutcomeRouter(
        IRabbitMqPublisher publisher,
        IOptions<RabbitMqClientOptions> options,
        ILogger<RabbitMqOutcomeRouter> logger)
    {
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task CompleteAsync(
        IChannel channel,
        RabbitMqDelivery delivery,
        RabbitMqMessageProcessingResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            if (result.IsSuccess)
            {
                if (result.OutputMessages is not null)
                {
                    await PublishOutputMessagesAsync(result.OutputMessages, cancellationToken);
                }
                else if (result.OutputBody is not null)
                {
                    var output = delivery.Message with { Body = result.OutputBody };
                    await _publisher.PublishToOutputAsync(output, cancellationToken);
                }

                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
                RabbitMqClientDiagnostics.AckedMessages.Add(1,
                    RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue),
                    RabbitMqClientDiagnostics.Tag("outcome", "success"));
                _logger.LogDebug("Processed RabbitMQ message {MessageId}", delivery.Message.MessageId);
                return;
            }

            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken);
            RabbitMqClientDiagnostics.DeadLetteredMessages.Add(1,
                RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue),
                RabbitMqClientDiagnostics.Tag("dead_letter_queue", _options.EffectiveDeadLetterQueue));
            RabbitMqClientDiagnostics.NackedMessages.Add(1,
                RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue),
                RabbitMqClientDiagnostics.Tag("requeue", false));
            _logger.LogWarning(
                "Rejected RabbitMQ message {MessageId}; broker will route it through DLX {DeadLetterExchange} to {DeadLetterQueue}. Error: {Error}",
                delivery.Message.MessageId,
                _options.EffectiveDeadLetterExchange,
                _options.EffectiveDeadLetterQueue,
                result.Error ?? "Message processing failed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not safely complete RabbitMQ message {MessageId}; nacking for requeue",
                delivery.Message.MessageId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
            RabbitMqClientDiagnostics.NackedMessages.Add(1,
                RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue),
                RabbitMqClientDiagnostics.Tag("requeue", true));
        }
    }

    private async Task PublishOutputMessagesAsync(
        IReadOnlyList<RabbitMqMessageEnvelope> outputMessages,
        CancellationToken cancellationToken)
    {
        if (outputMessages.Count == 0)
        {
            return;
        }

        if (outputMessages.Count == 1 || _options.OutputPublishConcurrency == 1)
        {
            foreach (var output in outputMessages)
            {
                await _publisher.PublishToOutputAsync(output, cancellationToken);
            }

            return;
        }

        await Parallel.ForEachAsync(
            outputMessages,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(_options.OutputPublishConcurrency, outputMessages.Count)
            },
            async (output, token) =>
            {
                await _publisher.PublishToOutputAsync(output, token);
            });
    }
}
