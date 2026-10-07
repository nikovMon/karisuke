using ImagingPipeline.Common.Dtos.Rules.Models;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.RuleEngine.Rules;

/// <summary>
/// A rule prepared for matching: geometry parsed and sensor qualities turned into bitmasks once,
/// when the snapshot is built, so matching a message allocates nothing per rule.
/// </summary>
public sealed record ActiveRule(
    string Id,
    IReadOnlyList<AlgorithmName> AlgorithmNames,
    IReadOnlyDictionary<string, SensorMatchCriteria> Sensors,
    IReadOnlyList<TenantInfo> TenantsInfo,
    double MinimumResolution,
    double MaximumResolution,
    TimeSpan? MaxPhotoAge,
    Geometry Geometry);

/// <summary>
/// What one sensor must satisfy. A zero mask or a null grid-type set means no constraint.
/// </summary>
public readonly record struct SensorMatchCriteria(
    int RegistrationQualityMask,
    IReadOnlySet<string>? AllowedGridTypes);
