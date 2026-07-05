using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Contracts.Messages;

public sealed record GatewayInputMessage(
    string ImageId,
    string SensorName,
    string? SensorType,
    double Resolution,
    DateTimeOffset? AcquisitionTime,
    Geometry Geometry);
