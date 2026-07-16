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
                    await _publisher.PublishToOutputAsync(ResetRetryCountHeader(output), cancellationToken);
                }

                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
                RabbitMqClientDiagnostics.AckedMessages.Add(1,
                    RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue),
                    RabbitMqClientDiagnostics.Tag("outcome", "success"));
                _logger.LogDebug("Processed RabbitMQ message {MessageId}", delivery.Message.MessageId);
                return;
            }

            if (result.FailureAction == RabbitMqMessageFailureAction.Retry)
            {
                await RetryOrDeadLetterAsync(channel, delivery, result, cancellationToken);
                return;
            }

            await DeadLetterAsync(channel, delivery, result, "non-retryable failure", cancellationToken);
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

    private async Task RetryOrDeadLetterAsync(
        IChannel channel,
        RabbitMqDelivery delivery,
        RabbitMqMessageProcessingResult result,
        CancellationToken cancellationToken)
    {
        if (_options.MaxRetryAttempts == 0)
        {
            await DeadLetterAsync(channel, delivery, result, "retry disabled", cancellationToken);
            return;
        }

        var retry = RabbitMqRetryMessageBuilder.Build(
            delivery.Message,
            _options.RetryCountHeader,
            _options.MaxRetryAttempts);

        if (retry.Status == RabbitMqRetryBuildStatus.InvalidMessage)
        {
            await DeadLetterAsync(channel, delivery, result, retry.Error ?? "retry count header could not be updated", cancellationToken);
            return;
        }

        if (retry.Status == RabbitMqRetryBuildStatus.AttemptsExhausted)
        {
            RabbitMqClientDiagnostics.RetryExhaustedMessages.Add(1,
                RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue));
            await DeadLetterAsync(channel, delivery, result, "retry attempts exhausted", cancellationToken);
            return;
        }

        await _publisher.PublishAsync(
            _options.RetryExchange,
            _options.EffectiveRetryRoutingKey,
            retry.Message!,
            cancellationToken);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        RabbitMqClientDiagnostics.RetriedMessages.Add(1,
            RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue),
            RabbitMqClientDiagnostics.Tag("retry_exchange", _options.RetryExchange));
        RabbitMqClientDiagnostics.AckedMessages.Add(1,
            RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue),
            RabbitMqClientDiagnostics.Tag("outcome", "retry"));
        _logger.LogWarning(
            "Retried RabbitMQ message {MessageId} through retry exchange {RetryExchange}; attempt {RetryAttempt}/{MaxRetryAttempts}. Error: {Error}",
            delivery.Message.MessageId,
            _options.RetryExchange,
            retry.NextRetryCount,
            _options.MaxRetryAttempts,
            result.Error ?? "Message processing failed");
    }

    private async Task DeadLetterAsync(
        IChannel channel,
        RabbitMqDelivery delivery,
        RabbitMqMessageProcessingResult result,
        string reason,
        CancellationToken cancellationToken)
    {
        await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken);
        RabbitMqClientDiagnostics.DeadLetteredMessages.Add(1,
            RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue),
            RabbitMqClientDiagnostics.Tag("dead_letter_queue", _options.EffectiveDeadLetterQueue));
        RabbitMqClientDiagnostics.NackedMessages.Add(1,
            RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue),
            RabbitMqClientDiagnostics.Tag("requeue", false));
        _logger.LogWarning(
            "Rejected RabbitMQ message {MessageId}; reason {Reason}; broker will route it through DLX {DeadLetterExchange} to {DeadLetterQueue}. Error: {Error}",
            delivery.Message.MessageId,
            reason,
            _options.EffectiveDeadLetterExchange,
            _options.EffectiveDeadLetterQueue,
            result.Error ?? "Message processing failed");
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
                await _publisher.PublishToOutputAsync(ResetRetryCountHeader(output), cancellationToken);
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
                await _publisher.PublishToOutputAsync(ResetRetryCountHeader(output), token);
            });
    }

    private RabbitMqMessageEnvelope ResetRetryCountHeader(RabbitMqMessageEnvelope message)
    {
        if (string.IsNullOrWhiteSpace(_options.RetryCountHeader))
        {
            return message;
        }

        var headers = RabbitMqHeaders.Clone(message.Headers);
        headers[_options.RetryCountHeader] = 0;
        return message with { Headers = headers };
    }
}
