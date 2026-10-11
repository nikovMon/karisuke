using NetTopologySuite.Geometries;

namespace ImagingPipeline.RuleEngine.Rules;

/// <summary>A matched rule and the part of the image it covers, which becomes the downstream ROI.</summary>
public sealed record RuleMatchResult(ActiveRule Rule, Geometry IntersectionGeometry);
