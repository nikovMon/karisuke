using ImagingPipeline.Observability;

namespace ImagingPipeline.RabbitMqClient;

public sealed class RabbitMqClientOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "admin";
    public string VirtualHost { get; set; } = "/";
    /// <summary>
    /// When set, the consumer connection, input/retry/dead-letter topology, and any
    /// publish targeting the input/retry side (PublishToInputAsync, retry republish)
    /// connect to this cluster instead of Host/Port/.../VirtualHost above. OutputQueue
    /// publishing always uses the primary Host/Port/.../VirtualHost. Leave unset for the
    /// default single-cluster behavior.
    /// </summary>
    public RabbitMqRemoteClusterOptions? InputCluster { get; set; }
    public string InputQueue { get; set; } = string.Empty;
    public string OutputQueue { get; set; } = string.Empty;
    public string DeadLetterQueue { get; set; } = string.Empty;
    public string RetryQueue { get; set; } = string.Empty;
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
    public string RetryCountHeader { get; set; } = "retry-count";
    /// <summary>
    /// Identifies an external processor that forwards the upstream publication timestamp
    /// unchanged. On the first delivery, its elapsed time is recorded as external-stage
    /// transit rather than as RabbitMQ-only delivery delay. Retry publishes are still
    /// measured as normal RabbitMQ hops because this client refreshes their timestamp.
    /// </summary>
    public PipelineStage? ForwardedInputStage { get; set; }
    public List<RabbitMqRetryQueueOptions> RetryQueues { get; set; } = [];
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

    internal string EffectiveDeadLetterQueue => string.IsNullOrWhiteSpace(DeadLetterQueue)
        ? EffectiveDeadLetterRoutingKey
        : DeadLetterQueue;

    internal IReadOnlyList<RabbitMqRetryQueueOptions> EffectiveRetryQueues =>
        RetryQueues.Count == 0
            ? [CreateLegacyRetryQueueOptions(1)]
            : RetryQueues;

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
            string.IsNullOrWhiteSpace(EffectiveDeadLetterQueue))
        {
            error = "RabbitMq OutputQueue and DeadLetterQueue must not be empty for consumers.";
            return false;
        }

        if (PrefetchCount == 0 || ConsumerConcurrency == 0)
        {
            error = "RabbitMq PrefetchCount and ConsumerConcurrency must be greater than zero for consumers.";
            return false;
        }

        if (RetryDelayMilliseconds <= 0 ||
            MaxRetryAttempts < 0 ||
            string.IsNullOrWhiteSpace(RetryCountHeader))
        {
            error = "RabbitMq retry delay, retry attempts, and retry count header are outside their valid ranges.";
            return false;
        }

        if (ForwardedInputStage is { } stage && !Enum.IsDefined(stage))
        {
            error = "RabbitMq ForwardedInputStage must be a known pipeline stage.";
            return false;
        }

        if (!IsRetryQueueConfigurationValid(out error))
        {
            return false;
        }

        if (InputCluster is { } inputCluster &&
            (string.IsNullOrWhiteSpace(inputCluster.Host) ||
             inputCluster.Port is < 1 or > 65535 ||
             string.IsNullOrWhiteSpace(inputCluster.Username) ||
             string.IsNullOrWhiteSpace(inputCluster.Password)))
        {
            error = "RabbitMq InputCluster Host, Port, Username, and Password must all be explicitly configured.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool IsRetryQueueConfigurationValid(out string error)
    {
        if (MaxRetryAttempts == 0)
        {
            error = string.Empty;
            return true;
        }

        if (RetryQueues.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(RetryQueue))
            {
                error = "RabbitMq RetryQueue must not be empty when no attempt-specific RetryQueues are configured.";
                return false;
            }

            if (string.Equals(RetryExchangeType, RabbitMQ.Client.ExchangeType.Headers, StringComparison.OrdinalIgnoreCase))
            {
                error = "RabbitMq RetryExchangeType cannot be headers unless attempt-specific RetryQueues are configured.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(RetryExchange) ||
            !string.Equals(RetryExchangeType, RabbitMQ.Client.ExchangeType.Headers, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(EffectiveRetryRoutingKey))
        {
            error = "RabbitMq RetryQueues require a headers RetryExchange and a non-empty retry publish routing key.";
            return false;
        }

        var retryCounts = new HashSet<int>();
        foreach (var retryQueue in RetryQueues)
        {
            if (retryQueue.RetryCount < 1 ||
                retryQueue.RetryCount > MaxRetryAttempts ||
                !retryCounts.Add(retryQueue.RetryCount) ||
                string.IsNullOrWhiteSpace(retryQueue.Queue) ||
                retryQueue.EffectiveDelayMilliseconds(RetryDelayMilliseconds) <= 0)
            {
                error = "RabbitMq RetryQueues must contain one valid queue per retry attempt.";
                return false;
            }
        }

        for (var retryCount = 1; retryCount <= MaxRetryAttempts; retryCount++)
        {
            if (!retryCounts.Contains(retryCount))
            {
                error = "RabbitMq RetryQueues must contain one valid queue per retry attempt.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private RabbitMqRetryQueueOptions CreateLegacyRetryQueueOptions(int retryCount) =>
        new()
        {
            RetryCount = retryCount,
            Queue = RetryQueue,
            RoutingKey = EffectiveRetryRoutingKey,
            DelayMilliseconds = RetryDelayMilliseconds
        };

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

/// <summary>
/// Deliberately has no defaults (unlike RabbitMqClientOptions' primary connection fields):
/// this only ever activates when a caller sets InputCluster, so a missing field should
/// fail startup validation instead of silently falling back to a guessed broker/credential.
/// </summary>
public sealed class RabbitMqRemoteClusterOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string VirtualHost { get; set; } = "/";
}

public sealed class RabbitMqRetryQueueOptions
{
    public int RetryCount { get; set; }
    public string Queue { get; set; } = string.Empty;
    public string? RoutingKey { get; set; }
    public int? DelayMilliseconds { get; set; }
    public Dictionary<string, object?> QueueHeaders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> BindingArguments { get; set; } = new(StringComparer.Ordinal);

    internal string EffectiveRoutingKey => string.IsNullOrWhiteSpace(RoutingKey) ? Queue : RoutingKey;

    internal int EffectiveDelayMilliseconds(int fallback) => DelayMilliseconds ?? fallback;
}
