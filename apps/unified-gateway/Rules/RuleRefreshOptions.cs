namespace ImagingPipeline.UnifiedGateway.Rules;

/// <summary>How often each pipeline's rules are reloaded. Jitter keeps replicas from refreshing together.</summary>
public sealed class RuleRefreshOptions
{
    public const string SectionName = "RuleRefresh";

    public int IntervalSeconds { get; set; } = 60;
    public int JitterSeconds { get; set; } = 5;

    internal bool IsValid() => IntervalSeconds > 0 && JitterSeconds >= 0;
}
