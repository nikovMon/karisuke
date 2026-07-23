using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Contracts.Messages;

public sealed record GatewayInputMessage(
    string ImageId,
    string SensorName,
    string RegistrationQuality,
    double Resolution,
    DateTimeOffset? PhotoTime,
    Geometry Geometry
);
