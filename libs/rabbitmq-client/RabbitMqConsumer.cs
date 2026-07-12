using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;

namespace ImagingPipeline.RabbitMqClient;

internal sealed class RabbitMqConsumer : IRabbitMqConsumer
{
    private readonly IRabbitMqConnectionManager _connections;
    private readonly RabbitMqClientOptions _options;
    private readonly RabbitMqOutcomeRouter _outcomes;
    private readonly ILogger<RabbitMqConsumer> _logger;

    public RabbitMqConsumer(
        IRabbitMqConnectionManager connections,
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

        var consumer = new AsyncEventingBasicConsumer(channel);
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
                result = RabbitMqMessageProcessingResult.Failure(ex.Message);
            }
            finally
            {
                RabbitMqClientDiagnostics.ProcessingDurationMs.Record(
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    RabbitMqClientDiagnostics.Tag("queue", _options.InputQueue));
            }

            await _outcomes.CompleteAsync(channel, delivery, result, cancellationToken);
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

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Stopping RabbitMQ consumer for {InputQueue}", _options.InputQueue);
        }
        finally
        {
            if (channel.IsOpen) await channel.BasicCancelAsync(consumerTag, noWait: false, CancellationToken.None);
        }
    }
}
