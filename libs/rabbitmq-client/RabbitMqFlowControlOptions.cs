namespace ImagingPipeline.RabbitMqClient;

public sealed class RabbitMqFlowControlOptions
{
    public const string SectionName = "RabbitMq:FlowControl";

    public int PollIntervalSeconds { get; set; } = 5;
    public int ManagementPort { get; set; } = 15672;
    public string Host { get; set; } = "localhost";
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public int MaxWaitSeconds { get; set; } = 1500;
    public List<WatchedQueueOptions> WatchedQueues { get; set; } = [];

    internal Uri ManagementUri => new($"http://{Host}:{ManagementPort}");

    internal bool IsValid(out string error)
    {
        if (WatchedQueues.Count == 0)
        {
            error = string.Empty;
            return true;
        }

        if (PollIntervalSeconds <= 0)
        {
            error = "FlowControl PollIntervalSeconds must be greater than zero.";
            return false;
        }

        if (MaxWaitSeconds < 0)
        {
            error = "FlowControl MaxWaitSeconds must not be negative.";
            return false;
        }

        if (ManagementPort is < 1 or > 65535)
        {
            error = "FlowControl ManagementPort must be a valid port number.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            error = "FlowControl Host must not be empty.";
            return false;
        }

        foreach (var watched in WatchedQueues)
        {
            if (string.IsNullOrWhiteSpace(watched.Queue))
            {
                error = "FlowControl WatchedQueues entries must have a non-empty Queue.";
                return false;
            }

            if (watched.HighWatermark <= 0)
            {
                error = $"FlowControl WatchedQueues entry '{watched.Queue}' must have a HighWatermark greater than zero.";
                return false;
            }

            if (watched.LowWatermark < 0 || watched.LowWatermark >= watched.HighWatermark)
            {
                error = $"FlowControl WatchedQueues entry '{watched.Queue}' must have a LowWatermark >= 0 and less than HighWatermark.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}

public sealed class WatchedQueueOptions
{
    public string Queue { get; set; } = string.Empty;
    public long HighWatermark { get; set; }
    public long LowWatermark { get; set; }
}
