namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

/// <summary>Provider-specific wire configuration, separate from the public auto-run filters.</summary>
public sealed class ElasticProviderOptions
{
    public const string SectionName = "AutoRun:Provider";
    public string BaseUrl { get; set; } = "";
    public string TableEntitiesPath { get; set; } = "/ErisImages/v2.0/ErisImages/Images/TableEntities";
    public int TimeoutSeconds { get; set; } = 30;
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ProviderRequestFields RequestFields { get; set; } = new();
    public ProviderResponseFields ResponseFields { get; set; } = new();
}

/// <summary>
/// SENSOR, geo, date and CREATED are confirmed request aliases. Other aliases remain provisional
/// deployment defaults; response property names are configured separately.
/// </summary>
public sealed class ProviderRequestFields
{
    public string Id { get; set; } = "GlobalId";
    public string SensorName { get; set; } = "SENSOR";
    public string SensorType { get; set; } = "IMAGINGTECHNIQUE";
    public string GridType { get; set; } = "SOLUTIONTYPE";
    public string RegistrationQuality { get; set; } = "REGISTRATIONQUALITY";
    public string SortieId { get; set; } = "SORTIE";
    public string IsRealtime { get; set; } = "ISREALTIME";
    public string CreatedTime { get; set; } = "CREATED";
    public string PhotoTime { get; set; } = "date";
    public string Resolution { get; set; } = "RESOLUTION";
    public string Geometry { get; set; } = "geo";
    public string SortDate { get; set; } = "date";
    // Unverified on the provider. Until configured, SAR conditions are enforced by the local rule matcher.
    public string? ModeSar { get; set; }
}

public sealed class ProviderResponseFields
{
    public string Id { get; set; } = "GlobalId";
    public string ImageUrl { get; set; } = "Url";
    public string GridUri { get; set; } = "GridPath";
    public string SensorName { get; set; } = "Sensor";
    public string SensorType { get; set; } = "ImagingTechnique";
    public string GridType { get; set; } = "SolutionType";
    public string SortieId { get; set; } = "Sortie";
    public string Resolution { get; set; } = "Resolution";
    public string RegistrationQuality { get; set; } = "RegistrationQuality";
    public string PhotoTime { get; set; } = "PhotoTime";
    public string CreatedTime { get; set; } = "Created";
    public string IsRealtime { get; set; } = "IsRealTime";
    public string Width { get; set; } = "ImageWidth";
    public string Height { get; set; } = "ImageHeight";
    public string? ModeSar { get; set; }
    public string? AreaOfInterest { get; set; }
    public string? LegId { get; set; }
    public string? PrevOverlayId { get; set; }
    public string? NextOverlayId { get; set; }
}
