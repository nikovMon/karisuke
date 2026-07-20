namespace ImagingPipeline.Gateway.Configuration;

public sealed class GatewaySettings
{
    public const string SectionName = "Gateway";

    public int ShutdownTimeoutSeconds { get; set; } = 30;
    public int RuleRefreshIntervalSeconds { get; set; } = 60;
    public int RuleRefreshJitterSeconds { get; set; }

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

        error = string.Empty;
        return true;
    }
}
