using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Messaging;

public class OverlayDto
{
    [JsonPropertyName("image_id")] 
    public string ImageId { get; set; } = string.Empty;
    
    [JsonPropertyName("image_url")] 
    public string ImageUrl { get; set; } = string.Empty;
    
    [JsonPropertyName("ruleId")] 
    public string RuleId { get; set; } = string.Empty;
    
    [JsonPropertyName("best_resolution")]
    public double BestResolution { get; set; }

    [JsonPropertyName("area_of_interest")]
    public string AreaOfInterest { get; set; } = string.Empty;

    [JsonPropertyName("algorithm_name")] 
    public IReadOnlyList<AlgorithmName> AlgorithmNames { get; set; } = [];
    
    [JsonPropertyName("image_width")] 
    public int ImageWidth { get; set; }
    
    [JsonPropertyName("image_height")] 
    public int ImageHeight { get; set; }
    
    [JsonPropertyName("roifootprint")] 
    public JsonElement RoiFootprint { get; set; }
    
    [JsonPropertyName("image_time")] 
    public DateTimeOffset ImageTime { get; set; }
    
    [JsonPropertyName("sensor_name")] 
    public string SensorName { get; set; } = string.Empty;
    
    [JsonPropertyName("sensor_type")]
    public string SensorType { get; set; } = string.Empty;

    [JsonPropertyName("grid_type")]
    public string GridType { get; set; } = string.Empty;

    [JsonPropertyName("grid_uri")]
    public string GridUri { get; set; } = string.Empty;
}
