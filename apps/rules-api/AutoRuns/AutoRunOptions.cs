using ImagingPipeline.RabbitMqClient;

namespace ImagingPipeline.Rules.Api.AutoRuns;

public sealed class AutoRunOptions
{
    public const string SectionName = "AutoRun";
    public const int DefaultFilterMaxOverlays = 100;
    public bool Enabled { get; set; }
    public int MaxSelectedOverlays { get; set; } = 100_000;
    public int MaxScannedRecords { get; set; } = 1_000_000;
    public int MaxCandidates { get; set; } = 200_000;
    public long PayloadBudgetBytes { get; set; } = 512L * 1024 * 1024;
    public AutoRunMemoryOptions Memory { get; set; } = new();
    public AutoRunUiOptions Ui { get; set; } = new();
    public int ReadyLifetimeMinutes { get; set; } = 30;
    public int SummaryLifetimeMinutes { get; set; } = 60;
    public int PublishTimeoutSeconds { get; set; } = 30;
    public int PublisherConcurrency { get; set; } = 4;
    public int MaxRetainedJobs { get; set; } = 100;
    public AutoRunPublication Publication { get; set; } = new();
    public RabbitMqDestination? Destination { get; set; }

    public bool IsValid() =>
        MaxSelectedOverlays is > 0 and <= 100_000 &&
        MaxScannedRecords is > 0 and <= 1_000_000 &&
        MaxCandidates is > 0 and <= 200_000 &&
        PayloadBudgetBytes is > 0 and <= 536_870_912 && Memory is not null && Memory.IsValid() &&
        Ui is not null && Ui.IsValid() && ReadyLifetimeMinutes > 0 &&
        SummaryLifetimeMinutes > 0 && PublishTimeoutSeconds > 0 &&
        PublisherConcurrency is >= 1 and <= 100 && MaxRetainedJobs > 0 &&
        Publication is { BatchSize: >= 1 and <= 100, DelayMs: >= 0 and <= 60_000 } &&
        (!Enabled || Destination is
        {
            Connection: { Name.Length: > 0, Host.Length: > 0, Port: > 0 and <= 65535, VirtualHost: not null },
            QueueName.Length: > 0, ExchangeName: not null, ExchangeType: not null, RoutingKey: not null,
            QueueArguments: not null, ExchangeArguments: not null, BindingArguments: not null
        } && (!Destination.BindQueueToExchange || !string.IsNullOrWhiteSpace(Destination.ExchangeName)));
}

public sealed class AutoRunUiOptions
{
    public string? MapUrl { get; set; }

    public bool IsValid() => string.IsNullOrWhiteSpace(MapUrl) ||
        Uri.TryCreate(MapUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo);
}
