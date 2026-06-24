using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;

namespace ImagingPipeline.RabbitClient;

internal sealed class RabbitConsumer : IRabbitConsumer
{
    private readonly IRabbitConnectionManager _connections;
    private readonly RabbitClientOptions _options;
    private readonly RabbitOutcomeRouter _outcomes;
    private readonly ILogger<RabbitConsumer> _logger;

    public RabbitConsumer(
        IRabbitConnectionManager connections,
        IOptions<RabbitClientOptions> options,
        RabbitOutcomeRouter outcomes,
        ILogger<RabbitConsumer> logger)
    {
        _connections = connections;
        _options = options.Value;
        _outcomes = outcomes;
        _logger = logger;
    }

    public async Task ConsumeAsync(IRabbitMessageHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_options.ConsumerCount == 1)
        {
            await ConsumeSingleAsync(handler, consumerIndex: 0, cancellationToken);
            return;
        }

        var consumers = Enumerable.Range(0, _options.ConsumerCount)
            .Select(index => ConsumeSingleAsync(handler, index, cancellationToken))
            .ToArray();
        await Task.WhenAll(consumers);
    }

    private async Task ConsumeSingleAsync(
        IRabbitMessageHandler handler,
        int consumerIndex,
        CancellationToken cancellationToken)
    {
        var connection = await _connections.GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(_options.InputQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(_options.OutputQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(_options.ErrorQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            var delivery = RabbitDeliveryFactory.Create(args);
            RabbitMessageProcessingResult result;
            using var activity = RabbitClientDiagnostics.ActivitySource.StartActivity("rabbitmq consume", ActivityKind.Consumer);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination.name", _options.InputQueue);
            activity?.SetTag("messaging.message.id", delivery.Message.MessageId);
            activity?.SetTag("messaging.rabbitmq.consumer_index", consumerIndex);
            var started = Stopwatch.GetTimestamp();
            RabbitClientDiagnostics.ConsumedMessages.Add(1, RabbitClientDiagnostics.Tag("queue", _options.InputQueue));

            try
            {
                result = await handler.HandleAsync(delivery.Message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RabbitClientDiagnostics.HandlerFailures.Add(1, RabbitClientDiagnostics.Tag("queue", _options.InputQueue));
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Handler failed for RabbitMQ message {MessageId}", delivery.Message.MessageId);
                result = RabbitMessageProcessingResult.Failure(ex.Message);
            }
            finally
            {
                RabbitClientDiagnostics.ProcessingDurationMs.Record(
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    RabbitClientDiagnostics.Tag("queue", _options.InputQueue));
            }

            await _outcomes.CompleteAsync(channel, delivery, result, cancellationToken);
        };

        var consumerTag = await channel.BasicConsumeAsync(
            _options.InputQueue, autoAck: false, consumerTag: string.Empty, noLocal: false, exclusive: false,
            arguments: null, consumer: consumer, cancellationToken: cancellationToken);
        _logger.LogInformation(
            "Consuming RabbitMQ queue {InputQueue} with prefetch {PrefetchCount}; consumer {ConsumerIndex} of {ConsumerCount}",
            _options.InputQueue, _options.PrefetchCount, consumerIndex + 1, _options.ConsumerCount);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Stopping RabbitMQ consumer {ConsumerIndex} for {InputQueue}",
                consumerIndex + 1, _options.InputQueue);
        }
        finally
        {
            if (channel.IsOpen) await channel.BasicCancelAsync(consumerTag, noWait: false, CancellationToken.None);
        }
    }
}
