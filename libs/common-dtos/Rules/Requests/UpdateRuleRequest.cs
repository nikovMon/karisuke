using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

public sealed class UpdateRuleRequest
{
    private string? _ruleName;
    private string? _description;
    private List<AlgorithmName>? _algorithmNames;
    private Dictionary<string, List<RegistrationQuality>>? _sensors;
    private bool? _isActive;
    private List<TenantInfo>? _tenantsInfo;
    private double? _minimumResolution;
    private double? _maximumResolution;
    private string? _area;
    private string? _locationWkt;
    private JsonElement? _locationGeoJson;
    private bool? _isPhotoOld;

    [JsonIgnore]
    public ISet<string> ProvidedFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("ruleName")]
    public string? RuleName
    {
        get => _ruleName;
        set
        {
            ProvidedFields.Add("ruleName");
            _ruleName = value;
        }
    }

    [JsonPropertyName("description")]
    public string? Description
    {
        get => _description;
        set
        {
            ProvidedFields.Add("description");
            _description = value;
        }
    }

    [JsonPropertyName("algorithmName")]
    public List<AlgorithmName>? AlgorithmNames
    {
        get => _algorithmNames;
        set
        {
            ProvidedFields.Add("algorithmName");
            _algorithmNames = value;
        }
    }

    [JsonPropertyName("sensors")]
    public Dictionary<string, List<RegistrationQuality>>? Sensors
    {
        get => _sensors;
        set
        {
            ProvidedFields.Add("sensors");
            _sensors = value;
        }
    }

    [JsonPropertyName("isActive")]
    public bool? IsActive
    {
        get => _isActive;
        set
        {
            ProvidedFields.Add("isActive");
            _isActive = value;
        }
    }

    [JsonPropertyName("tenantsInfo")]
    public List<TenantInfo>? TenantsInfo
    {
        get => _tenantsInfo;
        set
        {
            ProvidedFields.Add("tenantsInfo");
            _tenantsInfo = value;
        }
    }

    [JsonPropertyName("minimumResolution")]
    public double? MinimumResolution
    {
        get => _minimumResolution;
        set
        {
            ProvidedFields.Add("minimumResolution");
            _minimumResolution = value;
        }
    }

    [JsonPropertyName("maximumResolution")]
    public double? MaximumResolution
    {
        get => _maximumResolution;
        set
        {
            ProvidedFields.Add("maximumResolution");
            _maximumResolution = value;
        }
    }

    [JsonPropertyName("area")]
    public string? Area
    {
        get => _area;
        set
        {
            ProvidedFields.Add("area");
            _area = value;
        }
    }

    [JsonPropertyName("locationWkt")]
    public string? LocationWkt
    {
        get => _locationWkt;
        set
        {
            ProvidedFields.Add("locationWkt");
            _locationWkt = value;
        }
    }

    [JsonPropertyName("locationGeoJson")]
    public JsonElement? LocationGeoJson
    {
        get => _locationGeoJson;
        set
        {
            ProvidedFields.Add("locationGeoJson");
            _locationGeoJson = value;
        }
    }

    [JsonPropertyName("isPhotoOld")]
    public bool? IsPhotoOld
    {
        get => _isPhotoOld;
        set
        {
            ProvidedFields.Add("isPhotoOld");
            _isPhotoOld = value;
        }
    }

    public bool HasField(string jsonPropertyName) => ProvidedFields.Contains(jsonPropertyName);
}
