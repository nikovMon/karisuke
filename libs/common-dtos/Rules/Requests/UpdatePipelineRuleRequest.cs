using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

/// <summary>
/// A partial update of a v2 rule. Only the fields present in the request change; a field sent as
/// null is cleared. Fields inside <c>match</c> are patched one by one, so changing the sensors
/// keeps the location. <see cref="ProvidedFields"/> lists what the request changes, such as
/// <c>match.sensors</c>.
/// </summary>
public sealed class UpdatePipelineRuleRequest
{
    private readonly HashSet<string> _providedFields = new(StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyCollection<string> ProvidedFields => Match is null
        ? _providedFields
        : _providedFields.Concat(Match.ProvidedFields.Select(name => $"match.{name}")).ToArray();

    [JsonPropertyName("ruleName")]
    public string? RuleName
    {
        get;
        set
        {
            field = value;
            _providedFields.Add("ruleName");
        }
    }

    [JsonPropertyName("description")]
    public string? Description
    {
        get;
        set
        {
            field = value;
            _providedFields.Add("description");
        }
    }

    [JsonPropertyName("isActive")]
    public bool? IsActive
    {
        get;
        set
        {
            field = value;
            _providedFields.Add("isActive");
        }
    }

    [JsonPropertyName("matchAll")]
    public bool? MatchAll
    {
        get;
        set
        {
            field = value;
            _providedFields.Add("matchAll");
        }
    }

    /// <summary>Sent as null, removes every condition.</summary>
    [JsonPropertyName("match")]
    public RuleMatchConditionsPatch? Match
    {
        get;
        set
        {
            field = value;
            if (value is null)
            {
                _providedFields.Add("match");
            }
        }
    }

    [JsonPropertyName("runParams")]
    public List<JsonElement>? RunParams
    {
        get;
        set
        {
            field = value;
            _providedFields.Add("runParams");
        }
    }

    public void ApplyTo(PipelineRuleDocument rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (_providedFields.Contains("ruleName"))
        {
            rule.RuleName = RuleName ?? string.Empty;
        }

        if (_providedFields.Contains("description"))
        {
            rule.Description = Description;
        }

        if (_providedFields.Contains("isActive"))
        {
            rule.IsActive = IsActive.GetValueOrDefault();
        }

        if (_providedFields.Contains("matchAll"))
        {
            rule.MatchAll = MatchAll.GetValueOrDefault();
        }

        if (_providedFields.Contains("match"))
        {
            rule.Match = null;
        }
        else if (Match is not null)
        {
            rule.Match ??= new RuleMatchConditions();
            Match.ApplyTo(rule.Match);
        }

        if (_providedFields.Contains("runParams"))
        {
            rule.RunParams = RunParams;
        }
    }
}

/// <summary>A partial update of a rule's match conditions; a condition sent as null is removed.</summary>
public sealed class RuleMatchConditionsPatch
{
    private readonly HashSet<string> _providedFields = new(StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyCollection<string> ProvidedFields => _providedFields;

    [JsonPropertyName("sensors")]
    public List<SensorConfig>? Sensors
    {
        get;
        set
        {
            field = value;
            _providedFields.Add("sensors");
        }
    }

    [JsonPropertyName("resolution")]
    public ResolutionRange? Resolution
    {
        get;
        set
        {
            field = value;
            _providedFields.Add("resolution");
        }
    }

    [JsonPropertyName("locationWkt")]
    public string? LocationWkt
    {
        get;
        set
        {
            field = value;
            _providedFields.Add("locationWkt");
        }
    }

    [JsonPropertyName("maxPhotoAgeDays")]
    public int? MaxPhotoAgeDays
    {
        get;
        set
        {
            field = value;
            _providedFields.Add("maxPhotoAgeDays");
        }
    }

    internal void ApplyTo(RuleMatchConditions match)
    {
        if (_providedFields.Contains("sensors"))
        {
            match.Sensors = Sensors;
        }

        if (_providedFields.Contains("resolution"))
        {
            match.Resolution = Resolution;
        }

        if (_providedFields.Contains("locationWkt"))
        {
            match.LocationWkt = LocationWkt;
        }

        if (_providedFields.Contains("maxPhotoAgeDays"))
        {
            match.MaxPhotoAgeDays = MaxPhotoAgeDays;
        }
    }
}
