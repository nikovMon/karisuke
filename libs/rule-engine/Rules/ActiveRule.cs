using System.Text.Json;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.RuleEngine.Rules;

/// <summary>
/// A rule prepared for matching: geometry parsed and sensor qualities turned into bitmasks once,
/// when the snapshot is built, so matching a message allocates nothing per rule. Absent conditions
/// are stored as "no constraint": no sensors, an open resolution range, no photo age, no geometry.
/// </summary>
public sealed record ActiveRule(
    string Id,
    IReadOnlyDictionary<string, SensorMatchCriteria> Sensors,
    double MinimumResolution,
    double MaximumResolution,
    TimeSpan? MaxPhotoAge,
    Geometry? Geometry,
    IReadOnlyList<JsonElement> RunParams);

/// <summary>
/// What one sensor must satisfy. A zero mask or a null grid-type set means no constraint.
/// </summary>
public readonly record struct SensorMatchCriteria(
    int RegistrationQualityMask,
    IReadOnlySet<string>? AllowedGridTypes);
