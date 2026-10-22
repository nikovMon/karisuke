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
        : _providedFields.Concat(Match.ProvidedFields.Select(name => $"{PipelineRuleFields.Match}.{name}")).ToArray();

    [JsonPropertyName(PipelineRuleFields.RuleName)]
    public string? RuleName
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.RuleName);
        }
    }

    [JsonPropertyName(PipelineRuleFields.Description)]
    public string? Description
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.Description);
        }
    }

    [JsonPropertyName(PipelineRuleFields.IsActive)]
    public bool? IsActive
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.IsActive);
        }
    }

    [JsonPropertyName(PipelineRuleFields.MatchAll)]
    public bool? MatchAll
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.MatchAll);
        }
    }

    /// <summary>Sent as null, removes every condition.</summary>
    [JsonPropertyName(PipelineRuleFields.Match)]
    public RuleMatchConditionsPatch? Match
    {
        get;
        set
        {
            field = value;
            if (value is null)
            {
                _providedFields.Add(PipelineRuleFields.Match);
            }
        }
    }

    [JsonPropertyName(PipelineRuleFields.RunParams)]
    public List<JsonElement>? RunParams
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.RunParams);
        }
    }

    public void ApplyTo(PipelineRuleDocument rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (_providedFields.Contains(PipelineRuleFields.RuleName))
        {
            rule.RuleName = RuleName ?? string.Empty;
        }

        if (_providedFields.Contains(PipelineRuleFields.Description))
        {
            rule.Description = Description;
        }

        if (_providedFields.Contains(PipelineRuleFields.IsActive))
        {
            rule.IsActive = IsActive.GetValueOrDefault();
        }

        if (_providedFields.Contains(PipelineRuleFields.MatchAll))
        {
            rule.MatchAll = MatchAll.GetValueOrDefault();
        }

        if (_providedFields.Contains(PipelineRuleFields.Match))
        {
            rule.Match = null;
        }
        else if (Match is not null)
        {
            rule.Match ??= new RuleMatchConditions();
            Match.ApplyTo(rule.Match);
        }

        if (_providedFields.Contains(PipelineRuleFields.RunParams))
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

    [JsonPropertyName(PipelineRuleFields.Sensors)]
    public List<SensorConfig>? Sensors
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.Sensors);
        }
    }

    [JsonPropertyName(PipelineRuleFields.Resolution)]
    public ResolutionRange? Resolution
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.Resolution);
        }
    }

    [JsonPropertyName(PipelineRuleFields.LocationWkt)]
    public string? LocationWkt
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.LocationWkt);
        }
    }

    [JsonPropertyName(PipelineRuleFields.MaxPhotoAgeDays)]
    public int? MaxPhotoAgeDays
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.MaxPhotoAgeDays);
        }
    }

    [JsonPropertyName(PipelineRuleFields.ModeSar)]
    public List<string>? ModeSar
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.ModeSar);
        }
    }

    [JsonPropertyName(PipelineRuleFields.SensorTypes)]
    public List<string>? SensorTypes
    {
        get;
        set
        {
            field = value;
            _providedFields.Add(PipelineRuleFields.SensorTypes);
        }
    }

    internal void ApplyTo(RuleMatchConditions match)
    {
        if (_providedFields.Contains(PipelineRuleFields.Sensors))
        {
            match.Sensors = Sensors;
        }

        if (_providedFields.Contains(PipelineRuleFields.Resolution))
        {
            match.Resolution = Resolution;
        }

        if (_providedFields.Contains(PipelineRuleFields.LocationWkt))
        {
            match.LocationWkt = LocationWkt;
        }

        if (_providedFields.Contains(PipelineRuleFields.MaxPhotoAgeDays))
        {
            match.MaxPhotoAgeDays = MaxPhotoAgeDays;
        }

        if (_providedFields.Contains(PipelineRuleFields.ModeSar))
        {
            match.ModeSar = ModeSar;
        }

        if (_providedFields.Contains(PipelineRuleFields.SensorTypes))
        {
            match.SensorTypes = SensorTypes;
        }
    }
}
