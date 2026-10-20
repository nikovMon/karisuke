namespace ImagingPipeline.Common.Dtos.Rules.Models;

/// <summary>
/// Names of a v2 rule's fields, as stored and as reported in changed fields. Conditions inside
/// <c>match</c> are reported as paths such as <see cref="MatchSensors"/>.
/// </summary>
public static class PipelineRuleFields
{
    public const string RuleName = "ruleName";
    public const string Description = "description";
    public const string IsActive = "isActive";
    public const string MatchAll = "matchAll";
    public const string Match = "match";
    public const string RunParams = "runParams";
    public const string CreationTime = "creationTime";
    public const string UpdateTime = "updateTime";

    public const string Sensors = "sensors";
    public const string Resolution = "resolution";
    public const string LocationWkt = "locationWkt";
    public const string MaxPhotoAgeDays = "maxPhotoAgeDays";
    public const string ModeSar = "modeSAR";
    public const string Location = "location";

    public const string MatchSensors = Match + "." + Sensors;
}
