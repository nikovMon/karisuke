namespace ImagingPipeline.Common.Dtos.Gateway.Messages;

public static class GatewayInputMessageSchema
{
    public const string ImageId = "overlay.id";
    public const string SensorName = "sensorName";
    public const string SensorType = "sensorType";
    public const string Resolution = "bestResolution";
    public const string PhotoTime = "photoTime";
    public const string GeometryWkt = "intersectionArea";
    public const string GeometryGeoJson = "roiFootprint";
}
