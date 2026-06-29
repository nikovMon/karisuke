namespace ImagingPipeline.Gateway.Configuration;

public sealed class OutputSettings
{
    public const string SectionName = "Output";

    public string RoutingMetadataPropertyName { get; set; } = "gateway";
    public string FocusedGeometryPropertyName { get; set; } = "focusedGeometry";
    public bool PreserveOriginalMessage { get; set; } = true;

    internal bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(RoutingMetadataPropertyName) ||
            string.IsNullOrWhiteSpace(FocusedGeometryPropertyName))
        {
            error = "Output property names must not be empty.";
            return false;
        }

        if (string.Equals(
                RoutingMetadataPropertyName,
                FocusedGeometryPropertyName,
                StringComparison.Ordinal))
        {
            error = "Output metadata and focused geometry property names must be different.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
