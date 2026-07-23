namespace ImagingPipeline.Common.Dtos.Gateway.Messages;

public static class GatewayInputMessageSchema
{
    public const string ImageId = "overlay.id";
    public const string SensorName = "overlay.sensorName";
    public const string RegistrationQuality = "overlay.registrationQuality";
    public const string Resolution = "overlay.bestResolution";
    public const string PhotoTime = "overlay.photoTime";
    public const string GeometryWkt = "intersectionArea";
    public const string GeometryGeoJson = "overlay.roiFootprint";
}
