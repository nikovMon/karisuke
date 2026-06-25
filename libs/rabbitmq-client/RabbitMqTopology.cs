using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient;

internal static class RabbitMqTopology
{
    public static async Task DeclareAsync(
        IChannel channel,
        RabbitMqClientOptions options,
        CancellationToken cancellationToken)
    {
        await DeclareExchangeAsync(channel, options.EffectiveInputExchange, options.EffectiveInputExchangeType,
            options.InputExchangeHeaders, cancellationToken);
        await DeclareExchangeAsync(channel, options.OutputExchange, options.OutputExchangeType,
            options.OutputExchangeHeaders, cancellationToken);
        await DeclareExchangeAsync(channel, options.EffectiveDeadLetterExchange, options.DeadLetterExchangeType,
            options.DeadLetterExchangeHeaders, cancellationToken);

        await DeclareQueueAsync(channel, options.InputQueue, BuildInputQueueArguments(options), cancellationToken);
        await DeclareQueueAsync(channel, options.OutputQueue, options.OutputQueueHeaders, cancellationToken);
        await DeclareQueueAsync(channel, options.EffectiveDeadLetterQueue, options.DeadLetterQueueHeaders, cancellationToken);

        await BindQueueAsync(channel, options.InputQueue, options.EffectiveInputExchange, options.EffectiveInputRoutingKey,
            cancellationToken);
        await BindQueueAsync(channel, options.OutputQueue, options.OutputExchange, options.EffectiveOutputRoutingKey,
            cancellationToken);
        await BindQueueAsync(channel, options.EffectiveDeadLetterQueue, options.EffectiveDeadLetterExchange,
            options.EffectiveDeadLetterRoutingKey, cancellationToken);
    }

    private static Dictionary<string, object?> BuildInputQueueArguments(RabbitMqClientOptions options)
    {
        var headers = new Dictionary<string, object?>(options.HeadersArguments, StringComparer.Ordinal);
        headers.TryAdd("x-dead-letter-exchange", options.EffectiveDeadLetterExchange);
        headers.TryAdd("x-dead-letter-routing-key", options.EffectiveDeadLetterRoutingKey);
        return headers;
    }

    private static Task DeclareExchangeAsync(
        IChannel channel,
        string exchange,
        string exchangeType,
        IDictionary<string, object?> headers,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(exchange))
        {
            return Task.CompletedTask;
        }

        return channel.ExchangeDeclareAsync(
            exchange,
            exchangeType,
            durable: true,
            autoDelete: false,
            arguments: NormalizeArguments(headers),
            cancellationToken: cancellationToken);
    }

    private static Task DeclareQueueAsync(
        IChannel channel,
        string queue,
        IDictionary<string, object?> headers,
        CancellationToken cancellationToken)
    {
        return channel.QueueDeclareAsync(
            queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: NormalizeArguments(headers),
            cancellationToken: cancellationToken);
    }

    private static Dictionary<string, object?>? NormalizeArguments(IDictionary<string, object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        return arguments.ToDictionary(
            item => item.Key,
            item => NormalizeArgumentValue(item.Value),
            StringComparer.Ordinal);
    }

    private static object? NormalizeArgumentValue(object? value)
    {
        if (value is string text)
        {
            if (int.TryParse(text, out var number))
            {
                return number;
            }

            if (bool.TryParse(text, out var boolean))
            {
                return boolean;
            }
        }

        return value;
    }

    private static Task BindQueueAsync(
        IChannel channel,
        string queue,
        string exchange,
        string routingKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(exchange))
        {
            return Task.CompletedTask;
        }

        return channel.QueueBindAsync(queue, exchange, routingKey, cancellationToken: cancellationToken);
    }
}
