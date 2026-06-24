namespace ImagingPipeline.RabbitClient;

public sealed class RabbitClientOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string InputQueue { get; set; } = "tb.input";
    public string OutputQueue { get; set; } = "tb.output";
    public string ErrorQueue { get; set; } = "tb.error";
    public ushort PrefetchCount { get; set; } = 10;
    public int ConsumerCount { get; set; } = 1;
    public int BatchSize { get; set; } = 25;
    public int PublisherChannelPoolSize { get; set; } = 4;
    public int ReconnectDelaySeconds { get; set; } = 5;
    public int MaxRetryCount { get; set; } = 3;
    public bool RequeueOnFailure { get; set; }

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

        if (PublisherChannelPoolSize < 1 || ReconnectDelaySeconds < 1 || MaxRetryCount < 0)
        {
            error = "RabbitMq publisher channel pool and retry settings are outside their valid ranges.";
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

        if (string.IsNullOrWhiteSpace(OutputQueue) || string.IsNullOrWhiteSpace(ErrorQueue))
        {
            error = "RabbitMq OutputQueue and ErrorQueue must not be empty for consumers.";
            return false;
        }

        if (PrefetchCount == 0 || ConsumerCount < 1 || BatchSize < 1)
        {
            error = "RabbitMq retry, prefetch, concurrency, channel pool, and batch settings are outside their valid ranges.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
