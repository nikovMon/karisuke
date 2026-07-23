using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Contracts.Messages;

public sealed record GatewayInputMessage(
    string ImageId,
    string SensorName,
    RegistrationQuality RegistrationQuality,
    double Resolution,
    DateTimeOffset? PhotoTime,
    Geometry Geometry
);
