namespace ImagingPipeline.RuleEngine.Rules;

/// <summary>
/// The result of matching one image against a set of rules: the rules it matched, and for every
/// other rule the first condition it failed. Answers "why didn't this image reach that pipeline?".
/// </summary>
public sealed record RuleEvaluation(IReadOnlyList<RuleMatchResult> Matches, IReadOnlyList<RuleMiss> Misses);

/// <summary>A rule the image did not match, and the first condition that failed.</summary>
public sealed record RuleMiss(string RuleId, RuleMissReason Reason);

/// <summary>Match conditions, in the order the matcher checks them.</summary>
public enum RuleMissReason
{
    PhotoAge,
    Sensor,
    Resolution,
    Geometry
}
