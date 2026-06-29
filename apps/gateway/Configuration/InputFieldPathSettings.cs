namespace ImagingPipeline.Gateway.Configuration;

public sealed class InputFieldPathSettings
{
    public const string SectionName = "InputFieldPaths";

    public string ImageIdPath { get; set; } = "overlay.id";
    public string SensorNamePath { get; set; } = "overlay.sensorName";
    public string SensorTypePath { get; set; } = "overlay.sensor";
    public string ResolutionPath { get; set; } = "overlay.bestResolution";
    public string AcquisitionTimePath { get; set; } = "overlay.photoTime";
    public string GeometryWktPath { get; set; } = "overlay.roiFootprintWkt";
    public string GeometryGeoJsonPath { get; set; } = "overlay.roiFootprint";

    internal bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(ImageIdPath) ||
            string.IsNullOrWhiteSpace(SensorNamePath) ||
            string.IsNullOrWhiteSpace(ResolutionPath))
        {
            error = "InputFieldPaths required scalar paths must not be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(GeometryWktPath) &&
            string.IsNullOrWhiteSpace(GeometryGeoJsonPath))
        {
            error = "At least one input geometry path must be configured.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
