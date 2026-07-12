using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed record ActiveRule(RuleDto Rule, Geometry Geometry);
