using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitClient;

internal sealed class RabbitBatchConsumer : IRabbitBatchConsumer
{
    private readonly IRabbitConnectionManager _connections;
    private readonly RabbitClientOptions _options;
    private readonly RabbitOutcomeRouter _outcomes;
    private readonly ILogger<RabbitBatchConsumer> _logger;

    public RabbitBatchConsumer(
        IRabbitConnectionManager connections,
        IOptions<RabbitClientOptions> options,
        RabbitOutcomeRouter outcomes,
        ILogger<RabbitBatchConsumer> logger)
    {
        _connections = connections;
        _options = options.Value;
        _outcomes = outcomes;
        _logger = logger;
    }

    public async Task ConsumeAsync(IRabbitBatchMessageHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var connection = await _connections.GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(_options.InputQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, cancellationToken);

        var deliveries = Channel.CreateBounded<RabbitDelivery>(new BoundedChannelOptions(_options.PrefetchCount)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
            await deliveries.Writer.WriteAsync(RabbitDeliveryFactory.Create(args), cancellationToken);

        var consumerTag = await channel.BasicConsumeAsync(
            _options.InputQueue, autoAck: false, consumerTag: string.Empty, noLocal: false, exclusive: false,
            arguments: null, consumer: consumer, cancellationToken: cancellationToken);
        _logger.LogInformation("Batch-consuming RabbitMQ queue {InputQueue} in batches up to {BatchSize}",
            _options.InputQueue, _options.BatchSize);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = new List<RabbitDelivery>(_options.BatchSize)
                {
                    await deliveries.Reader.ReadAsync(cancellationToken)
                };
                var batchWindow = Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                while (batch.Count < _options.BatchSize)
                {
                    while (batch.Count < _options.BatchSize && deliveries.Reader.TryRead(out var next)) batch.Add(next);
                    if (batch.Count >= _options.BatchSize || batchWindow.IsCompleted) break;
                    var available = deliveries.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    await Task.WhenAny(available, batchWindow);
                    if (batchWindow.IsCompleted) break;
                }

                await ProcessBatchAsync(channel, handler, batch, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Stopping RabbitMQ batch consumer for {InputQueue}", _options.InputQueue);
        }
        finally
        {
            deliveries.Writer.TryComplete();
            if (channel.IsOpen) await channel.BasicCancelAsync(consumerTag, noWait: false, CancellationToken.None);
        }
    }

    private async Task ProcessBatchAsync(
        IChannel channel,
        IRabbitBatchMessageHandler handler,
        IReadOnlyList<RabbitDelivery> batch,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RabbitMessageProcessingResult> results;
        try
        {
            results = await handler.HandleBatchAsync(batch.Select(item => item.Message).ToArray(), cancellationToken);
            if (results.Count != batch.Count)
            {
                throw new InvalidOperationException("The batch handler must return one result for every input message.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RabbitMQ batch handler failed for {BatchCount} messages", batch.Count);
            results = batch.Select(_ => RabbitMessageProcessingResult.Failure(ex.Message)).ToArray();
        }

        for (var index = 0; index < batch.Count; index++)
        {
            await _outcomes.CompleteAsync(channel, batch[index], results[index], cancellationToken);
        }
    }
}

