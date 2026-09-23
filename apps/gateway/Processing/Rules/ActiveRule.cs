using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Processing.Rules;

public readonly record struct SensorMatchCriteria(
    int RegistrationQualityMask,
    IReadOnlySet<string>? AllowedGridTypes);

public sealed record ActiveRule(
    string Id,
    IReadOnlyList<AlgorithmName> AlgorithmNames,
    IReadOnlyDictionary<string, SensorMatchCriteria> Sensors,
    IReadOnlyList<TenantInfo> TenantsInfo,
    double MinimumResolution,
    double MaximumResolution,
    bool IsPhotoOld,
    Geometry Geometry);
