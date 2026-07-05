namespace ImagingPipeline.Gateway.Configuration;

public sealed class GatewaySettings
{
    public const string SectionName = "Gateway";

    public string ServiceName { get; set; } = "imaging-pipeline-gateway";
    public int ShutdownTimeoutSeconds { get; set; } = 30;
    public int RuleRefreshIntervalSeconds { get; set; } = 60;

    internal bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            error = "Gateway ServiceName must not be empty.";
            return false;
        }

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

        error = string.Empty;
        return true;
    }
}
