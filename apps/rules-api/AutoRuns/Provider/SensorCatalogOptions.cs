namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

public sealed class SensorCatalogOptions
{
    public const string SectionName = "AutoRun:SensorCatalog";
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public string BaseUrl { get; set; } = "";
    public string Path { get; set; } = "/sensors/api/v1/sensors/list/sensor/";
    public int RefreshIntervalMinutes { get; set; } = 1440;
    public int TimeoutSeconds { get; set; } = 30;

    public bool IsValid() =>
        RefreshIntervalMinutes is > 0 and <= 43_200 && TimeoutSeconds is > 0 and <= 86_400 &&
        !string.IsNullOrWhiteSpace(Path) && Path.StartsWith('/') && !Path.StartsWith("//", StringComparison.Ordinal) &&
        !Path.Any(character => char.IsControl(character) || character is '\\' or '?' or '#') &&
        (string.IsNullOrWhiteSpace(BaseUrl) || Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment));
}
