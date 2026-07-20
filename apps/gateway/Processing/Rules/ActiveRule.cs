using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed record ActiveRule(
    string Id,
    AlgorithmName AlgorithmName,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Sensors,
    IReadOnlyList<TenantInfo> TenantsInfo,
    double MinimumResolution,
    double MaximumResolution,
    Geometry Geometry);
