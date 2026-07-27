using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;

namespace ImagingPipeline.RabbitMqClient;

internal sealed class RabbitMqConsumer : IRabbitMqConsumer
{
    private readonly IRabbitMqConsumerConnectionManager _connections;
    private readonly RabbitMqClientOptions _options;
    private readonly RabbitMqOutcomeRouter _outcomes;
    private readonly ILogger<RabbitMqConsumer> _logger;

    public RabbitMqConsumer(
        IRabbitMqConsumerConnectionManager connections,
        IOptions<RabbitMqClientOptions> options,
        RabbitMqOutcomeRouter outcomes,
        ILogger<RabbitMqConsumer> logger)
    {
        _connections = connections;
        _options = options.Value;
        _outcomes = outcomes;
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
        await RabbitMqTopology.DeclareAsync(channel, _options, cancellationToken);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, cancellationToken);

        var lifetime = new RabbitMqConsumerLifetime();
        var consumer = new AsyncEventingBasicConsumer(channel);

        Task OnConsumerUnregisteredAsync(object _, ConsumerEventArgs args)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lifetime.ConsumerCancelled(args.ConsumerTags, connection.IsOpen);
            }

            return Task.CompletedTask;
        }

        Task OnChannelShutdownAsync(object _, ShutdownEventArgs args)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lifetime.ChannelShutdown(args, connection.IsOpen);
            }

            return Task.CompletedTask;
        }

        Task OnCallbackExceptionAsync(object _, CallbackExceptionEventArgs args)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lifetime.CallbackFailed(args.Exception);
            }

            return Task.CompletedTask;
        }

        consumer.UnregisteredAsync += OnConsumerUnregisteredAsync;
        channel.ChannelShutdownAsync += OnChannelShutdownAsync;
        channel.CallbackExceptionAsync += OnCallbackExceptionAsync;
        consumer.ReceivedAsync += async (_, args) =>
        {
            var delivery = RabbitMqDeliveryFactory.Create(args);
            RabbitMqMessageProcessingResult result;
            using var activity = RabbitMqClientDiagnostics.ActivitySource.StartActivity("rabbitmq consume", ActivityKind.Consumer);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination.name", _options.InputQueue);
            activity?.SetTag("messaging.message.id", delivery.Message.MessageId);
            var started = Stopwatch.GetTimestamp();
            RabbitMqClientDiagnostics.ConsumedMessages.Add(1, RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue));

            try
            {
                result = await handler.HandleAsync(delivery.Message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RabbitMqClientDiagnostics.HandlerFailures.Add(1, RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue));
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Handler failed for RabbitMQ message {MessageId}", delivery.Message.MessageId);
                result = RabbitMqMessageProcessingResult.RetryableFailure(ex.Message);
            }
            finally
            {
                RabbitMqClientDiagnostics.ProcessingDurationMs.Record(
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue));
            }

            try
            {
                await _outcomes.CompleteAsync(channel, delivery, result, cancellationToken);
            }
            catch (RabbitMqMessageCompletionException ex)
            {
                lifetime.CompletionFailed(ex);
            }
        };

        var consumerTag = await channel.BasicConsumeAsync(
            _options.InputQueue, autoAck: false, consumerTag: string.Empty, noLocal: false, exclusive: false,
            arguments: null, consumer: consumer, cancellationToken: cancellationToken);
        _logger.LogInformation(
            "Consuming RabbitMQ queue {InputQueue} with consumer {ConsumerIndex}/{ConsumerConcurrency} and prefetch {PrefetchCount}",
            _options.InputQueue,
            consumerIndex + 1,
            _options.ConsumerConcurrency,
            _options.PrefetchCount);

        var faulted = false;
        try
        {
            await lifetime.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Stopping RabbitMQ consumer for {InputQueue}", _options.InputQueue);
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            consumer.UnregisteredAsync -= OnConsumerUnregisteredAsync;
            channel.ChannelShutdownAsync -= OnChannelShutdownAsync;
            channel.CallbackExceptionAsync -= OnCallbackExceptionAsync;
            await StopConsumerChannelAsync(channel, consumerTag, faulted);
        }
    }

    public async Task ConsumeBatchAsync(IRabbitMqBatchMessageHandler handler, int batchSize, TimeSpan maxWaitTime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var connection = await _connections.GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await RabbitMqTopology.DeclareAsync(channel, _options, cancellationToken);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, cancellationToken);

        var buffer = System.Threading.Channels.Channel.CreateUnbounded<RabbitMqDelivery>();
        var lifetime = new RabbitMqConsumerLifetime();
        var consumer = new AsyncEventingBasicConsumer(channel);

        Task OnConsumerUnregisteredAsync(object _, ConsumerEventArgs args)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lifetime.ConsumerCancelled(args.ConsumerTags, connection.IsOpen);
            }

            return Task.CompletedTask;
        }

        Task OnChannelShutdownAsync(object _, ShutdownEventArgs args)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lifetime.ChannelShutdown(args, connection.IsOpen);
            }

            return Task.CompletedTask;
        }

        Task OnCallbackExceptionAsync(object _, CallbackExceptionEventArgs args)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lifetime.CallbackFailed(args.Exception);
            }

            return Task.CompletedTask;
        }

        consumer.UnregisteredAsync += OnConsumerUnregisteredAsync;
        channel.ChannelShutdownAsync += OnChannelShutdownAsync;
        channel.CallbackExceptionAsync += OnCallbackExceptionAsync;
        consumer.ReceivedAsync += async (_, args) =>
        {
            var delivery = RabbitMqDeliveryFactory.Create(args);
            await buffer.Writer.WriteAsync(delivery, cancellationToken);
        };

        var consumerTag = await channel.BasicConsumeAsync(
            _options.InputQueue, autoAck: false, consumerTag: string.Empty, noLocal: false, exclusive: false,
            arguments: null, consumer: consumer, cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Consuming RabbitMQ queue {InputQueue} in batches of {BatchSize} with prefetch {PrefetchCount}",
            _options.InputQueue, batchSize, _options.PrefetchCount);

        var faulted = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = new List<RabbitMqDelivery>(batchSize);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(maxWaitTime);

                try
                {
                    while (batch.Count < batchSize)
                    {
                        var delivery = await ReadDeliveryAsync(buffer.Reader, lifetime.Completion, cts.Token);
                        batch.Add(delivery);
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // timeout reached, proceed with whatever is in the batch
                }

                if (batch.Count > 0)
                {
                    await ProcessBatchAsync(channel, handler, batch, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Stopping RabbitMQ batch consumer for {InputQueue}", _options.InputQueue);
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            consumer.UnregisteredAsync -= OnConsumerUnregisteredAsync;
            channel.ChannelShutdownAsync -= OnChannelShutdownAsync;
            channel.CallbackExceptionAsync -= OnCallbackExceptionAsync;
            await StopConsumerChannelAsync(channel, consumerTag, faulted);
        }
    }

    private static async Task<RabbitMqDelivery> ReadDeliveryAsync(
        System.Threading.Channels.ChannelReader<RabbitMqDelivery> reader,
        Task consumerTermination,
        CancellationToken cancellationToken)
    {
        var read = reader.ReadAsync(cancellationToken).AsTask();
        var completed = await Task.WhenAny(read, consumerTermination);
        if (ReferenceEquals(completed, consumerTermination))
        {
            await consumerTermination;
            throw new UnreachableException();
        }

        return await read;
    }

    private async Task StopConsumerChannelAsync(IChannel channel, string consumerTag, bool faulted)
    {
        if (!channel.IsOpen)
        {
            return;
        }

        if (!faulted)
        {
            await channel.BasicCancelAsync(consumerTag, noWait: false, CancellationToken.None);
            return;
        }

        try
        {
            // Closing the channel outside the delivery callback causes RabbitMQ to requeue unacknowledged deliveries.
            await channel.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RabbitMQ consumer channel was already unavailable while it was being replaced");
        }
    }

    private async Task ProcessBatchAsync(IChannel channel, IRabbitMqBatchMessageHandler handler, IReadOnlyList<RabbitMqDelivery> batch, CancellationToken cancellationToken)
    {
        using var activity = RabbitMqClientDiagnostics.ActivitySource.StartActivity("rabbitmq consume batch", ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", _options.InputQueue);
        activity?.SetTag("messaging.batch.message_count", batch.Count);
        
        var started = Stopwatch.GetTimestamp();
        RabbitMqClientDiagnostics.ConsumedMessages.Add(batch.Count, RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue));

        var envelopes = batch.Select(d => d.Message).ToList();
        IReadOnlyDictionary<string, RabbitMqMessageProcessingResult> results;

        try
        {
            results = await handler.HandleBatchAsync(envelopes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RabbitMqClientDiagnostics.HandlerFailures.Add(1, RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Batch Handler failed for {Count} messages", batch.Count);
            
            // If the handler crashes entirely, assume all failed
            results = envelopes.ToDictionary(e => e.MessageId, e => RabbitMqMessageProcessingResult.Failure(ex.Message));
        }
        finally
        {
            RabbitMqClientDiagnostics.ProcessingDurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue));
        }

        foreach (var delivery in batch)
        {
            if (!results.TryGetValue(delivery.Message.MessageId, out var msgResult))
            {
                msgResult = RabbitMqMessageProcessingResult.Failure("Message was not processed or missing from handler results.");
            }
            await _outcomes.CompleteAsync(channel, delivery, msgResult, cancellationToken);
        }
    }
}
