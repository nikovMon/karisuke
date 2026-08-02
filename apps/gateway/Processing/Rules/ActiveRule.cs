using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed record ActiveRule(
    string Id,
    IReadOnlyList<AlgorithmName> AlgorithmNames,
    IReadOnlyDictionary<string, int> Sensors,
    IReadOnlyList<TenantInfo> TenantsInfo,
    double MinimumResolution,
    double MaximumResolution,
    bool IsPhotoOld,
    Geometry Geometry);
