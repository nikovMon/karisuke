using System.Diagnostics;
using System.Threading.Channels;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitMqClient;

internal sealed class RabbitMqConsumer : IRabbitMqConsumer
{
    private readonly IRabbitMqConnectionManager _connections;
    private readonly RabbitMqClientOptions _options;
    private readonly RabbitMqOutcomeRouter _outcomes;
    private readonly IMessageTraceContextPropagator _propagator;
    private readonly ILogger<RabbitMqConsumer> _logger;

    public RabbitMqConsumer(
        IRabbitMqConnectionManager connections,
        IOptions<RabbitMqClientOptions> options,
        RabbitMqOutcomeRouter outcomes,
        IMessageTraceContextPropagator propagator,
        ILogger<RabbitMqConsumer> logger)
    {
        _connections = connections;
        _options = options.Value;
        _outcomes = outcomes;
        _propagator = propagator;
        _logger = logger;
    }

    public async Task ConsumeAsync(IRabbitMqMessageHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var consumers = Enumerable.Range(0, _options.ConsumerConcurrency)
            .Select(index => ConsumeSingleAsync(handler, index, linkedCancellation.Token))
            .ToArray();

        foreach (var consumer in consumers)
        {
            _ = consumer.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Cancel(),
                linkedCancellation,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        await Task.WhenAll(consumers);
    }

    private async Task ConsumeSingleAsync(
        IRabbitMqMessageHandler handler,
        int consumerIndex,
        CancellationToken cancellationToken)
    {
        var connection = await _connections.GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        MessagingTelemetry.AddChannel(MessagingChannelRole.Consumer, 1);

        try
        {
            await RabbitMqTopology.DeclareAsync(channel, _options, cancellationToken);
            await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, args) =>
            {
                var receivedAt = TelemetryTiming.StartTimestamp();
                var delivery = RabbitMqDeliveryFactory.Create(args);
                var (parentContext, baggage) = ExtractTransportContext(delivery.Message.Headers);
                var previousBaggage = Baggage.Current;
                Baggage.Current = baggage;
                try
                {
                    await ProcessDeliveryAsync(
                        channel,
                        handler,
                        delivery,
                        parentContext,
                        receivedAt,
                        cancellationToken);
                }
                finally
                {
                    Baggage.Current = previousBaggage;
                }
            };

            var consumerTag = await channel.BasicConsumeAsync(
                _options.InputQueue,
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: null,
                consumer: consumer,
                cancellationToken: cancellationToken);
            RabbitMqLog.ConsumerStarted(
                _logger,
                consumerIndex + 1,
                _options.ConsumerConcurrency,
                _options.InputQueue,
                _options.PrefetchCount);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RabbitMqLog.ConsumerStopping(_logger, _options.InputQueue);
            }
            finally
            {
                if (channel.IsOpen)
                {
                    await channel.BasicCancelAsync(consumerTag, noWait: false, CancellationToken.None);
                }
            }
        }
        finally
        {
            MessagingTelemetry.AddChannel(MessagingChannelRole.Consumer, -1);
        }
    }

    private async Task ProcessDeliveryAsync(
        IChannel channel,
        IRabbitMqMessageHandler handler,
        RabbitMqDelivery delivery,
        ActivityContext parentContext,
        long receivedAt,
        CancellationToken cancellationToken)
    {
        var retryAttempt = ReadRetryAttempt(delivery.Message.Headers);
        using var activity = StartProcessingActivity("rabbitmq handler", parentContext);
        AddDeliveryTags(activity, delivery, retryAttempt);
        using var logScope = _logger.BeginTelemetryScope(new TelemetryLogContext(
            MessageId: delivery.Message.MessageId,
            CorrelationId: delivery.Message.CorrelationId,
            Destination: _options.InputQueue,
            RetryAttempt: retryAttempt));

        RabbitMqInputTelemetry.RecordConsumed(_options, delivery, retryAttempt);
        MessagingTelemetry.AddInFlight(_options.InputQueue, 1);

        var finalOutcome = TelemetryOutcome.Failure;
        var finalError = TelemetryErrorCategory.Unknown;
        Exception? handlerException = null;
        try
        {
            RabbitMqMessageProcessingResult result;
            try
            {
                result = await handler.HandleAsync(delivery.Message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                handlerException = ex;
                RabbitMqLog.HandlerFailed(_logger, ex, delivery.Message.MessageId);
                // The handler's stage span and this error log already carry the exception.
                activity.SetTelemetryError(TelemetryErrorCategory.Handler, ex, recordException: false);
                result = RabbitMqMessageProcessingResult.RetryableFailure(ex.Message);
            }

            var completion = await _outcomes.CompleteAsync(channel, delivery, result, cancellationToken);
            finalOutcome = completion.Outcome;
            finalError = completion.Error;

            if (completion.Outcome == TelemetryOutcome.Success)
            {
                activity.SetTelemetrySuccess();
            }
            else if (handlerException is null)
            {
                activity.SetTelemetryError(completion.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            finalOutcome = TelemetryOutcome.Cancelled;
            finalError = TelemetryErrorCategory.Cancelled;
            activity.SetTelemetryError(TelemetryErrorCategory.Cancelled, recordException: false);
            throw;
        }
        catch (Exception ex)
        {
            finalOutcome = TelemetryOutcome.Failure;
            finalError = TelemetryErrorCategory.Unknown;
            activity.SetTelemetryError(TelemetryErrorCategory.Unknown, ex, recordException: false);
            throw;
        }
        finally
        {
            MessagingTelemetry.RecordProcessed(
                _options.InputQueue,
                TelemetryTiming.ElapsedSeconds(receivedAt),
                finalOutcome,
                finalError);
            MessagingTelemetry.AddInFlight(_options.InputQueue, -1);
        }
    }

    public async Task ConsumeBatchAsync(
        IRabbitMqBatchMessageHandler handler,
        int batchSize,
        TimeSpan maxWaitTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var connection = await _connections.GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        MessagingTelemetry.AddChannel(MessagingChannelRole.Consumer, 1);

        var capacity = Math.Max(batchSize, _options.PrefetchCount);
        var buffer = Channel.CreateBounded<BufferedDelivery>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        try
        {
            await RabbitMqTopology.DeclareAsync(channel, _options, cancellationToken);
            await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, args) =>
            {
                var receivedAt = TelemetryTiming.StartTimestamp();
                var delivery = RabbitMqDeliveryFactory.Create(args);
                var (parentContext, baggage) = ExtractTransportContext(delivery.Message.Headers);
                var retryAttempt = ReadRetryAttempt(delivery.Message.Headers);
                RabbitMqInputTelemetry.RecordConsumed(_options, delivery, retryAttempt);

                await RabbitMqBatchBufferWriter.WriteAsync(
                    buffer.Writer,
                    new BufferedDelivery(delivery, parentContext, baggage, receivedAt),
                    _options.InputQueue,
                    receivedAt,
                    cancellationToken);
            };

            var consumerTag = await channel.BasicConsumeAsync(
                _options.InputQueue,
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: null,
                consumer: consumer,
                cancellationToken: cancellationToken);
            RabbitMqLog.BatchConsumerStarted(
                _logger,
                _options.InputQueue,
                batchSize,
                _options.PrefetchCount);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var batch = new List<BufferedDelivery>(batchSize);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(maxWaitTime);

                    try
                    {
                        while (batch.Count < batchSize)
                        {
                            batch.Add(await buffer.Reader.ReadAsync(timeout.Token));
                        }
                    }
                    catch (OperationCanceledException) when (
                        timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        // The maximum wait elapsed; process the partial batch.
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        foreach (var item in batch)
                        {
                            MessagingTelemetry.RecordProcessed(
                                _options.InputQueue,
                                TelemetryTiming.ElapsedSeconds(item.ReceivedAt),
                                TelemetryOutcome.Cancelled,
                                TelemetryErrorCategory.Cancelled);
                            MessagingTelemetry.AddInFlight(_options.InputQueue, -1);
                        }

                        throw;
                    }

                    if (batch.Count > 0)
                    {
                        await ProcessBatchAsync(channel, handler, batch, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RabbitMqLog.ConsumerStopping(_logger, _options.InputQueue);
            }
            finally
            {
                buffer.Writer.TryComplete();
                while (buffer.Reader.TryRead(out var pending))
                {
                    MessagingTelemetry.RecordProcessed(
                        _options.InputQueue,
                        TelemetryTiming.ElapsedSeconds(pending.ReceivedAt),
                        TelemetryOutcome.Cancelled,
                        TelemetryErrorCategory.Cancelled);
                    MessagingTelemetry.AddInFlight(_options.InputQueue, -1);
                }

                if (channel.IsOpen)
                {
                    await channel.BasicCancelAsync(consumerTag, noWait: false, CancellationToken.None);
                }
            }
        }
        finally
        {
            MessagingTelemetry.AddChannel(MessagingChannelRole.Consumer, -1);
        }
    }

    private async Task ProcessBatchAsync(
        IChannel channel,
        IRabbitMqBatchMessageHandler handler,
        IReadOnlyList<BufferedDelivery> batch,
        CancellationToken cancellationToken)
    {
        var links = batch
            .Where(static item => item.ParentContext != default)
            .Select(static item => new ActivityLink(item.ParentContext))
            .ToArray();
        using var activity = TelemetrySources.RabbitMq.StartActivity(
            "rabbitmq handler batch",
            ActivityKind.Internal,
            default(ActivityContext),
            tags: null,
            links);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag("messaging.system", "rabbitmq");
            activity.SetTag("messaging.destination.name", _options.InputQueue);
            activity.SetTag("imaging_pipeline.messaging.operation", "handler");
            activity.SetTag("messaging.batch.message_count", batch.Count);
        }

        var previousBaggage = Baggage.Current;
        // A batch has multiple linked parents. Only baggage shared by every message is
        // safe to make ambient for downstream publishes; using batch[0] would leak its
        // tenant/correlation context onto unrelated messages.
        Baggage.Current = CommonBaggage(batch);
        using var logScope = _logger.BeginTelemetryScope(new TelemetryLogContext(
            Destination: _options.InputQueue));

        var completed = 0;
        var fallbackOutcome = TelemetryOutcome.Failure;
        var fallbackError = TelemetryErrorCategory.Unknown;
        var anyFailure = false;
        try
        {
            var envelopes = batch.Select(static item => item.Delivery.Message).ToList();
            IReadOnlyDictionary<string, RabbitMqMessageProcessingResult> results;
            try
            {
                results = await handler.HandleBatchAsync(envelopes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                anyFailure = true;
                RabbitMqLog.BatchHandlerFailed(_logger, ex, batch.Count);
                activity.SetTelemetryError(TelemetryErrorCategory.Handler, ex, recordException: false);
                results = envelopes.ToDictionary(
                    static envelope => envelope.MessageId,
                    static _ => RabbitMqMessageProcessingResult.Failure("Batch handler failed."));
            }

            foreach (var item in batch)
            {
                var delivery = item.Delivery;
                if (!results.TryGetValue(delivery.Message.MessageId, out var messageResult))
                {
                    messageResult = RabbitMqMessageProcessingResult.Failure(
                        "Message was not processed or was missing from handler results.");
                }

                var completion = await _outcomes.CompleteAsync(channel, delivery, messageResult, cancellationToken);
                anyFailure |= completion.Outcome != TelemetryOutcome.Success;
                MessagingTelemetry.RecordProcessed(
                    _options.InputQueue,
                    TelemetryTiming.ElapsedSeconds(item.ReceivedAt),
                    completion.Outcome,
                    completion.Error);
                MessagingTelemetry.AddInFlight(_options.InputQueue, -1);
                completed++;
            }

            if (!anyFailure)
            {
                activity.SetTelemetrySuccess();
            }
            else if (activity?.Status != ActivityStatusCode.Error)
            {
                activity.SetTelemetryError(TelemetryErrorCategory.Handler);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            fallbackOutcome = TelemetryOutcome.Cancelled;
            fallbackError = TelemetryErrorCategory.Cancelled;
            activity.SetTelemetryError(TelemetryErrorCategory.Cancelled, recordException: false);
            throw;
        }
        catch (Exception ex)
        {
            activity.SetTelemetryError(TelemetryErrorCategory.Unknown, ex, recordException: false);
            throw;
        }
        finally
        {
            for (var index = completed; index < batch.Count; index++)
            {
                MessagingTelemetry.RecordProcessed(
                    _options.InputQueue,
                    TelemetryTiming.ElapsedSeconds(batch[index].ReceivedAt),
                    fallbackOutcome,
                    fallbackError);
                MessagingTelemetry.AddInFlight(_options.InputQueue, -1);
            }

            Baggage.Current = previousBaggage;
        }
    }

    private Activity? StartProcessingActivity(string name, ActivityContext parentContext) =>
        parentContext == default
            ? TelemetrySources.RabbitMq.StartActivity(name, ActivityKind.Internal)
            : TelemetrySources.RabbitMq.StartActivity(name, ActivityKind.Internal, parentContext);

    private (ActivityContext ActivityContext, Baggage Baggage) ExtractTransportContext(
        IReadOnlyDictionary<string, object?>? headers)
    {
        if (Activity.Current is { } nativeActivity)
        {
            // RabbitMQ.Client already extracted W3C trace context to create its subscriber
            // activity. Parse only baggage here instead of parsing trace headers twice.
            return (nativeActivity.Context, _propagator.ExtractBaggage(headers));
        }

        var extracted = _propagator.Extract(headers);
        return (extracted.ActivityContext, extracted.Baggage);
    }

    private void AddDeliveryTags(Activity? activity, RabbitMqDelivery delivery, int retryAttempt)
    {
        if (activity is null || !activity.IsAllDataRequested)
        {
            return;
        }

        activity.SetTag("messaging.system", "rabbitmq");
        activity.SetTag("messaging.destination.name", _options.InputQueue);
        activity.SetTag("imaging_pipeline.messaging.operation", "handler");
        activity.SetTag("messaging.message.id", delivery.Message.MessageId);
        activity.SetTag("messaging.message.conversation_id", delivery.Message.CorrelationId);
        activity.SetTag("messaging.message.body.size", delivery.Message.Body.LongLength);
        activity.SetTag("messaging.rabbitmq.message.delivery_tag", delivery.DeliveryTag);
        activity.SetTag("messaging.rabbitmq.message.redelivered", delivery.Redelivered);
        activity.SetTag(TelemetryAttributeNames.RetryAttempt, retryAttempt);
    }

    private int ReadRetryAttempt(IReadOnlyDictionary<string, object?>? headers)
    {
        return RabbitMqRetryMessageBuilder.TryReadRetryCount(
                headers,
                _options.RetryCountHeader,
                out var retryAttempt)
            ? retryAttempt
            : 0;
    }

    private static Baggage CommonBaggage(IReadOnlyList<BufferedDelivery> batch)
    {
        var common = batch[0].Baggage.GetBaggage().ToArray();
        var commonCount = common.Length;

        for (var index = 1; index < batch.Count && commonCount > 0; index++)
        {
            var retainedCount = 0;
            for (var commonIndex = 0; commonIndex < commonCount; commonIndex++)
            {
                var candidate = common[commonIndex];
                if (ContainsBaggage(batch[index].Baggage, candidate))
                {
                    common[retainedCount++] = candidate;
                }
            }

            commonCount = retainedCount;
        }

        if (commonCount == 0)
        {
            return default;
        }

        var commonDictionary = new Dictionary<string, string>(commonCount, StringComparer.Ordinal);
        for (var index = 0; index < commonCount; index++)
        {
            commonDictionary[common[index].Key] = common[index].Value;
        }

        return Baggage.Create(commonDictionary);
    }

    private static bool ContainsBaggage(
        Baggage baggage,
        KeyValuePair<string, string> candidate)
    {
        foreach (var item in baggage.GetBaggage())
        {
            if (string.Equals(item.Key, candidate.Key, StringComparison.Ordinal)
                && string.Equals(item.Value, candidate.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record BufferedDelivery(
        RabbitMqDelivery Delivery,
        ActivityContext ParentContext,
        Baggage Baggage,
        long ReceivedAt);
}

internal static class RabbitMqBatchBufferWriter
{
    public static async ValueTask WriteAsync<T>(
        ChannelWriter<T> writer,
        T item,
        string destination,
        long receivedAt,
        CancellationToken cancellationToken)
    {
        MessagingTelemetry.AddInFlight(destination, 1);
        try
        {
            // A successful write transfers telemetry ownership to the batch reader,
            // which records the terminal outcome and removes the in-flight value.
            await writer.WriteAsync(item, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            RecordTerminalOutcome(
                destination,
                receivedAt,
                TelemetryOutcome.Cancelled,
                TelemetryErrorCategory.Cancelled);
            throw;
        }
        catch
        {
            RecordTerminalOutcome(
                destination,
                receivedAt,
                TelemetryOutcome.Failure,
                TelemetryErrorCategory.Unknown);
            throw;
        }
    }

    private static void RecordTerminalOutcome(
        string destination,
        long receivedAt,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error)
    {
        MessagingTelemetry.RecordProcessed(
            destination,
            TelemetryTiming.ElapsedSeconds(receivedAt),
            outcome,
            error);
        MessagingTelemetry.AddInFlight(destination, -1);
    }
}

internal static class RabbitMqInputTelemetry
{
    public static void RecordConsumed(
        RabbitMqClientOptions options,
        RabbitMqDelivery delivery,
        int retryAttempt)
    {
        // A broker redelivery is not a new publish. Its original timestamp includes
        // prior handler/requeue time and must never be reported as one broker hop.
        var rabbitMqDeliveryDelay = delivery.Redelivered
            ? null
            : delivery.PublishedToDeliverySeconds;
        if (retryAttempt == 0 && options.ForwardedInputStage is { } externalStage)
        {
            if (rabbitMqDeliveryDelay is { } externalStageDuration)
            {
                PipelineTelemetry.RecordExternalStageDuration(externalStage, externalStageDuration);
            }

            // The forwarded timestamp predates the external stage, so it is not a
            // measurement of the final RabbitMQ hop alone. Broker redeliveries also
            // omit the external sample because the initial receipt already measured it.
            rabbitMqDeliveryDelay = null;
        }

        MessagingTelemetry.RecordConsumed(
            options.InputQueue,
            delivery.Message.Body.LongLength,
            rabbitMqDeliveryDelay);
    }
}
