namespace ImagingPipeline.UnifiedGateway.Configuration;

/// <summary>Enable only after deploying a gateway that understands historical envelopes.</summary>
public sealed class HistoricalInputOptions
{
    public const string SectionName = "HistoricalInput";

    public bool Enabled { get; set; }
}
