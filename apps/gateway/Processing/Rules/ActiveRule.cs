using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed record ActiveRule(RuleConfigDto Rule, Geometry Geometry);
