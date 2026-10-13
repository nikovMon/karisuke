using ImagingPipeline.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient;

internal readonly record struct RabbitMqCompletionResult(
    TelemetryOutcome Outcome,
    TelemetryErrorCategory Error);

internal sealed class RabbitMqMessageCompletionException : Exception
{
    public RabbitMqMessageCompletionException(string messageId, Exception innerException)
        : base($"Could not safely complete RabbitMQ message '{messageId}'.", innerException)
    {
    }
}

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

    public async Task<RabbitMqCompletionResult> CompleteAsync(
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

                await AckAsync(channel, delivery.DeliveryTag, TelemetryOutcome.Success, cancellationToken);
                return new RabbitMqCompletionResult(TelemetryOutcome.Success, TelemetryErrorCategory.None);
            }

            if (result.FailureAction == RabbitMqMessageFailureAction.Retry)
            {
                return await RetryOrDeadLetterAsync(channel, delivery, cancellationToken);
            }

            return await DeadLetterAsync(
                channel,
                delivery,
                "non-retryable failure",
                TelemetryErrorCategory.Handler,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            using (BeginMessageScope(delivery))
            {
                RabbitMqLog.CompletionFailed(_logger, ex);
            }

            throw new RabbitMqMessageCompletionException(delivery.Message.MessageId, ex);
        }
    }

    private async Task<RabbitMqCompletionResult> RetryOrDeadLetterAsync(
        IChannel channel,
        RabbitMqDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (_options.MaxRetryAttempts == 0)
        {
            return await DeadLetterAsync(
                channel,
                delivery,
                "retry disabled",
                TelemetryErrorCategory.Handler,
                cancellationToken);
        }

        var retry = RabbitMqRetryMessageBuilder.Build(
            delivery.Message,
            _options.RetryCountHeader,
            _options.MaxRetryAttempts);

        if (retry.Status == RabbitMqRetryBuildStatus.InvalidMessage)
        {
            return await DeadLetterAsync(
                channel,
                delivery,
                retry.Error ?? "retry count header could not be updated",
                TelemetryErrorCategory.Validation,
                cancellationToken);
        }

        if (retry.Status == RabbitMqRetryBuildStatus.AttemptsExhausted)
        {
            MessagingTelemetry.RecordRetry(
                _options.InputQueue,
                retry.CurrentRetryCount,
                TelemetryOutcome.Exhausted,
                TelemetryErrorCategory.Handler);
            return await DeadLetterAsync(
                channel,
                delivery,
                "retry attempts exhausted",
                TelemetryErrorCategory.Handler,
                cancellationToken);
        }

        await _publisher.PublishAsync(
            _options.RetryExchange,
            _options.EffectiveRetryRoutingKey,
            retry.Message!,
            cancellationToken);
        await AckAsync(channel, delivery.DeliveryTag, TelemetryOutcome.Retry, cancellationToken);
        MessagingTelemetry.RecordRetry(
            _options.InputQueue,
            retry.NextRetryCount,
            TelemetryOutcome.Retry,
            TelemetryErrorCategory.Handler);
        using (BeginMessageScope(delivery))
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RetryAttempt, retry.NextRetryCount),
                   new(TelemetryAttributeNames.RetryMaxAttempts, _options.MaxRetryAttempts),
                   new(TelemetryAttributeNames.MessagingDestinationName, _options.RetryExchange)
               }))
        {
            RabbitMqLog.RetryScheduled(_logger);
        }

        return new RabbitMqCompletionResult(TelemetryOutcome.Retry, TelemetryErrorCategory.Handler);
    }

    private async Task<RabbitMqCompletionResult> DeadLetterAsync(
        IChannel channel,
        RabbitMqDelivery delivery,
        string reason,
        TelemetryErrorCategory error,
        CancellationToken cancellationToken)
    {
        await NackAsync(
            channel,
            delivery.DeliveryTag,
            requeue: false,
            TelemetryOutcome.DeadLetter,
            cancellationToken);
        using (BeginMessageScope(delivery))
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.MessagingDestinationName, _options.EffectiveDeadLetterQueue),
                   new(TelemetryAttributeNames.DeadLetterReason, reason)
               }))
        {
            RabbitMqLog.DeadLettered(_logger);
        }

        return new RabbitMqCompletionResult(TelemetryOutcome.DeadLetter, error);
    }

    private async Task AckAsync(
        IChannel channel,
        ulong deliveryTag,
        TelemetryOutcome outcome,
        CancellationToken cancellationToken)
    {
        using var telemetry = RabbitMqSettlementTelemetry.Begin(
            _options.InputQueue, MessagingOperation.Ack, outcome, cancellationToken);
        await channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken);
        telemetry.Settled();
    }

    private async Task NackAsync(
        IChannel channel,
        ulong deliveryTag,
        bool requeue,
        TelemetryOutcome outcome,
        CancellationToken cancellationToken)
    {
        using var telemetry = RabbitMqSettlementTelemetry.Begin(
            _options.InputQueue, MessagingOperation.Nack, outcome, cancellationToken);
        await channel.BasicNackAsync(deliveryTag, multiple: false, requeue, cancellationToken);
        telemetry.Settled();
    }

    private IDisposable? BeginMessageScope(RabbitMqDelivery delivery) =>
        _logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new(TelemetryAttributeNames.MessagingMessageId, delivery.Message.MessageId)
        });

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
