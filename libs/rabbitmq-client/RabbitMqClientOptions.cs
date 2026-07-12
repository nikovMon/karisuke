namespace ImagingPipeline.RabbitMqClient;

public sealed class RabbitMqClientOptions
{
    public const string SectionName = "RabbitMq";
    private const string DefaultDeadLetterQueue = "int.algo.gateway_rules.dlq";
    private const string DefaultRetryQueue = "int.algo.gateway_rules.retry";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "admin";
    public string VirtualHost { get; set; } = "/";
    public string InputQueue { get; set; } = "int.algo.gateway_rules";
    public string OutputQueue { get; set; } = "int.algo.gateway_rules.output";
    public string DeadLetterQueue { get; set; } = DefaultDeadLetterQueue;
    public string RetryQueue { get; set; } = DefaultRetryQueue;
    public string InputExchange { get; set; } = string.Empty;
    public string OutputExchange { get; set; } = string.Empty;
    public string DeadLetterExchange { get; set; } = string.Empty;
    public string RetryExchange { get; set; } = string.Empty;
    public string InputExchangeType { get; set; } = "direct";
    public string OutputExchangeType { get; set; } = "direct";
    public string DeadLetterExchangeType { get; set; } = "direct";
    public string RetryExchangeType { get; set; } = "direct";
    public string? InputRoutingKey { get; set; }
    public string? OutputRoutingKey { get; set; }
    public string? DeadLetterRoutingKey { get; set; }
    public string? RetryRoutingKey { get; set; }
    public Dictionary<string, object?> HeadersArguments { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> OutputQueueHeaders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> DeadLetterQueueHeaders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> RetryQueueHeaders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> InputExchangeHeaders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> OutputExchangeHeaders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> DeadLetterExchangeHeaders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> RetryExchangeHeaders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> InputBindingArguments { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> OutputBindingArguments { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> DeadLetterBindingArguments { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> RetryBindingArguments { get; set; } = new(StringComparer.Ordinal);
    public ushort PrefetchCount { get; set; } = 1;
    public ushort ConsumerConcurrency { get; set; } = 1;
    public int PublisherChannelPoolSize { get; set; } = 4;
    public int OutputPublishConcurrency { get; set; } = 4;
    public int RetryDelayMilliseconds { get; set; } = 10000;
    public int MaxRetryAttempts { get; set; } = 3;
    public string RetryCountPath { get; set; } = "retry.count";
    public int ReconnectDelaySeconds { get; set; } = 5;

    internal string EffectiveInputExchange => InputExchange;
    internal string EffectiveInputExchangeType => InputExchangeType;
    internal string EffectiveInputRoutingKey => EffectiveValue(InputRoutingKey, InputQueue);
    internal string EffectiveOutputRoutingKey => string.IsNullOrWhiteSpace(OutputRoutingKey) ? OutputQueue : OutputRoutingKey;
    internal string EffectiveRetryRoutingKey => string.IsNullOrWhiteSpace(RetryRoutingKey) ? RetryQueue : RetryRoutingKey;
    internal string EffectiveDeadLetterExchange => HeadersArguments.TryGetValue("x-dead-letter-exchange", out var exchange)
        ? HeaderToString(exchange)
        : DeadLetterExchange;

    internal string EffectiveDeadLetterRoutingKey => string.IsNullOrWhiteSpace(DeadLetterRoutingKey)
        ? HeaderToString(HeadersArguments.GetValueOrDefault("x-dead-letter-routing-key", DeadLetterQueue))
        : DeadLetterRoutingKey;

    internal string EffectiveDeadLetterQueue => DeadLetterQueue == DefaultDeadLetterQueue
        ? EffectiveDeadLetterRoutingKey
        : DeadLetterQueue;

    internal bool IsPublisherValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535)
        {
            error = "RabbitMq Host and Port must be valid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(InputQueue))
        {
            error = "RabbitMq InputQueue must not be empty.";
            return false;
        }

        if (PublisherChannelPoolSize < 1 || OutputPublishConcurrency < 1 || ReconnectDelaySeconds < 1)
        {
            error = "RabbitMq publisher channel pool, output publish concurrency, and reconnect settings are outside their valid ranges.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal bool IsConsumerValid(out string error)
    {
        if (!IsPublisherValid(out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(OutputQueue) ||
            string.IsNullOrWhiteSpace(EffectiveDeadLetterQueue) ||
            string.IsNullOrWhiteSpace(RetryQueue))
        {
            error = "RabbitMq OutputQueue, DeadLetterQueue, and RetryQueue must not be empty for consumers.";
            return false;
        }

        if (PrefetchCount == 0 || ConsumerConcurrency == 0)
        {
            error = "RabbitMq PrefetchCount and ConsumerConcurrency must be greater than zero for consumers.";
            return false;
        }

        if (RetryDelayMilliseconds <= 0 || MaxRetryAttempts < 0 || string.IsNullOrWhiteSpace(RetryCountPath))
        {
            error = "RabbitMq retry delay, retry attempts, and retry count path are outside their valid ranges.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string EffectiveValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string HeaderToString(object? value) =>
        value switch
        {
            null => string.Empty,
            string text => text,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };
}
