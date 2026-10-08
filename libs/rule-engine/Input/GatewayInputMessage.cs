using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.RuleEngine.Input;

/// <summary>An incoming image update, validated and ready to match against rules.</summary>
public sealed record GatewayInputMessage(
    string ImageId,
    string SensorName,
    string SensorType,
    RegistrationQuality RegistrationQuality,
    double BestResolution,
    string? AreaOfInterest,
    string ImageUrl,
    int ImageWidth,
    int ImageHeight,
    DateTimeOffset PhotoTime,
    Geometry Geometry,
    string GridType,
    string GridUri);
