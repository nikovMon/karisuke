using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed record RuleMatchResult(ActiveRule Rule, Geometry IntersectionGeometry);
