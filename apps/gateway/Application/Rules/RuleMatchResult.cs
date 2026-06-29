using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Application.Rules;

public sealed record RuleMatchResult(RuleConfigDto Rule, Geometry IntersectionGeometry);
