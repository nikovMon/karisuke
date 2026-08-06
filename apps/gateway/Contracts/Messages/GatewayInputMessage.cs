using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Contracts.Messages;

public sealed record GatewayInputMessage(
    string ImageId,
    string SensorName,
    string SensorType,
    RegistrationQuality RegistrationQuality,
    double BestResolution,
    string AreaOfInterest,
    string ImageUrl,
    int ImageWidth,
    int ImageHeight,
    DateTimeOffset PhotoTime,
    Geometry Geometry,
    string GridType,
    string GridUri
);
