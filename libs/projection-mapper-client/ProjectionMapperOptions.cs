namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperOptions
{
    public const string SectionName = "ProjectionMapper";

    public string Host { get; set; } = string.Empty;
    public Dictionary<string, string> Endpoints { get; set; } = new(StringComparer.Ordinal);
    public string SendingSystem { get; set; } = string.Empty;
    public bool UseCache { get; set; }
    public int TimeoutSeconds { get; set; } = 10;

    public bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(Host) || !Uri.TryCreate(Host, UriKind.Absolute, out _))
        {
            error = "ProjectionMapper Host must be a valid absolute URL.";
            return false;
        }

        if (TimeoutSeconds < 1)
        {
            error = "ProjectionMapper TimeoutSeconds must be greater than 0.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SendingSystem))
        {
            error = "ProjectionMapper SendingSystem must not be empty.";
            return false;
        }

        if (!Endpoints.TryGetValue(ProjectionMapperEndpointKeys.G2IMultiPoints, out var endpoint) ||
            string.IsNullOrWhiteSpace(endpoint))
        {
            error = $"ProjectionMapper Endpoints must include a non-empty '{ProjectionMapperEndpointKeys.G2IMultiPoints}' entry.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
