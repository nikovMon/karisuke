namespace ImagingPipeline.Gateway.Configuration;

public sealed class GatewaySettings
{
    public const string SectionName = "Gateway";

    public int ShutdownTimeoutSeconds { get; set; } = 30;
    public int RuleRefreshIntervalSeconds { get; set; } = 60;
    public int RuleRefreshJitterSeconds { get; set; }
    public int MaxPhotoAgeDays { get; set; } = 30;

    internal bool IsValid(out string error)
    {
        if (ShutdownTimeoutSeconds <= 0)
        {
            error = "Gateway ShutdownTimeoutSeconds must be greater than zero.";
            return false;
        }

        if (RuleRefreshIntervalSeconds <= 0)
        {
            error = "Gateway RuleRefreshIntervalSeconds must be greater than zero.";
            return false;
        }

        if (RuleRefreshJitterSeconds < 0)
        {
            error = "Gateway RuleRefreshJitterSeconds must be greater than or equal to zero.";
            return false;
        }

        if (MaxPhotoAgeDays <= 0 || MaxPhotoAgeDays > TimeSpan.MaxValue.TotalDays)
        {
            error = "Gateway MaxPhotoAgeDays must be greater than zero and within the supported TimeSpan range.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
