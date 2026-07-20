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
        await DeclareExchangeAsync(channel, options.RetryExchange, options.RetryExchangeType,
            options.RetryExchangeHeaders, cancellationToken);

        await DeclareQueueAsync(channel, options.InputQueue, BuildInputQueueArguments(options), cancellationToken);
        await DeclareQueueAsync(channel, options.OutputQueue, options.OutputQueueHeaders, cancellationToken);
        await DeclareQueueAsync(channel, options.EffectiveDeadLetterQueue, options.DeadLetterQueueHeaders, cancellationToken);
        foreach (var retryQueue in options.EffectiveRetryQueues)
        {
            if (string.IsNullOrWhiteSpace(retryQueue.Queue))
            {
                continue;
            }

            await DeclareQueueAsync(channel, retryQueue.Queue, BuildRetryQueueArguments(options, retryQueue), cancellationToken);
        }

        await BindQueueAsync(channel, options.InputQueue, options.EffectiveInputExchange, options.EffectiveInputRoutingKey,
            options.InputBindingArguments, cancellationToken);
        await BindQueueAsync(channel, options.OutputQueue, options.OutputExchange, options.EffectiveOutputRoutingKey,
            options.OutputBindingArguments, cancellationToken);
        await BindQueueAsync(channel, options.EffectiveDeadLetterQueue, options.EffectiveDeadLetterExchange,
            options.EffectiveDeadLetterRoutingKey, options.DeadLetterBindingArguments, cancellationToken);
        foreach (var retryQueue in options.EffectiveRetryQueues)
        {
            if (string.IsNullOrWhiteSpace(retryQueue.Queue))
            {
                continue;
            }

            await BindQueueAsync(channel, retryQueue.Queue, options.RetryExchange,
                retryQueue.EffectiveRoutingKey, BuildRetryBindingArguments(options, retryQueue), cancellationToken);
        }
    }

    private static Dictionary<string, object?> BuildInputQueueArguments(RabbitMqClientOptions options)
    {
        var headers = new Dictionary<string, object?>(options.HeadersArguments, StringComparer.Ordinal);
        headers.TryAdd("x-dead-letter-exchange", options.EffectiveDeadLetterExchange);
        headers.TryAdd("x-dead-letter-routing-key", options.EffectiveDeadLetterRoutingKey);
        return headers;
    }

    private static Dictionary<string, object?> BuildRetryQueueArguments(
        RabbitMqClientOptions options,
        RabbitMqRetryQueueOptions retryQueue)
    {
        var headers = new Dictionary<string, object?>(options.RetryQueueHeaders, StringComparer.Ordinal);
        foreach (var header in retryQueue.QueueHeaders)
        {
            headers[header.Key] = header.Value;
        }

        headers.TryAdd("x-message-ttl", retryQueue.EffectiveDelayMilliseconds(options.RetryDelayMilliseconds));
        headers.TryAdd("x-dead-letter-exchange", options.EffectiveInputExchange);
        headers.TryAdd("x-dead-letter-routing-key", options.EffectiveInputRoutingKey);
        return headers;
    }

    private static Dictionary<string, object?> BuildRetryBindingArguments(
        RabbitMqClientOptions options,
        RabbitMqRetryQueueOptions retryQueue)
    {
        var arguments = new Dictionary<string, object?>(options.RetryBindingArguments, StringComparer.Ordinal);
        foreach (var argument in retryQueue.BindingArguments)
        {
            arguments[argument.Key] = argument.Value;
        }

        if (string.Equals(options.RetryExchangeType, ExchangeType.Headers, StringComparison.OrdinalIgnoreCase))
        {
            arguments[options.RetryCountHeader] = retryQueue.RetryCount;
        }

        return arguments;
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
        IDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(exchange))
        {
            return Task.CompletedTask;
        }

        return channel.QueueBindAsync(
            queue,
            exchange,
            routingKey,
            arguments: NormalizeArguments(arguments),
            cancellationToken: cancellationToken);
    }
}
