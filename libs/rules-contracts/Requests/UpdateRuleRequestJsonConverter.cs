using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Rules.Contracts.Models;

namespace ImagingPipeline.Rules.Contracts.Requests;

public sealed class UpdateRuleRequestJsonConverter : JsonConverter<UpdateRuleRequest>
{
    public override UpdateRuleRequest Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var request = new UpdateRuleRequest();

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return request;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            request.ProvidedFields.Add(property.Name);

            switch (property.Name)
            {
                case "ruleName":
                    request.RuleName = ReadNullable<string>(property.Value, options);
                    break;
                case "description":
                    request.Description = ReadNullable<string>(property.Value, options);
                    break;
                case "algorithmName":
                    request.AlgorithmName = ReadNullable<AlgorithmName>(property.Value, options);
                    break;
                case "sensors":
                    request.Sensors = ReadNullable<Dictionary<string, List<string>>>(property.Value, options);
                    break;
                case "isActive":
                    request.IsActive = ReadNullable<bool>(property.Value, options);
                    break;
                case "tenants":
                    request.Tenants = ReadNullable<List<TenantConfigDto>>(property.Value, options);
                    break;
                case "minResulution":
                    request.MinResolution = ReadNullable<double>(property.Value, options);
                    break;
                case "maxResulution":
                    request.MaxResolution = ReadNullable<double>(property.Value, options);
                    break;
                case "area":
                    request.Area = ReadNullable<string>(property.Value, options);
                    break;
                case "wkt":
                    request.Wkt = ReadNullable<string>(property.Value, options);
                    break;
                case "geoJson":
                    request.GeoJson = property.Value.Clone();
                    break;
                case "maxLookBackDay":
                    request.MaxLookBackDay = ReadNullable<int>(property.Value, options);
                    break;
            }
        }

        return request;
    }

    public override void Write(
        Utf8JsonWriter writer,
        UpdateRuleRequest value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteIfProvided(writer, options, value, "ruleName", value.RuleName);
        WriteIfProvided(writer, options, value, "description", value.Description);
        WriteIfProvided(writer, options, value, "algorithmName", value.AlgorithmName);
        WriteIfProvided(writer, options, value, "sensors", value.Sensors);
        WriteIfProvided(writer, options, value, "isActive", value.IsActive);
        WriteIfProvided(writer, options, value, "tenants", value.Tenants);
        WriteIfProvided(writer, options, value, "minResulution", value.MinResolution);
        WriteIfProvided(writer, options, value, "maxResulution", value.MaxResolution);
        WriteIfProvided(writer, options, value, "area", value.Area);
        WriteIfProvided(writer, options, value, "wkt", value.Wkt);
        WriteIfProvided(writer, options, value, "geoJson", value.GeoJson);
        WriteIfProvided(writer, options, value, "maxLookBackDay", value.MaxLookBackDay);
        writer.WriteEndObject();
    }

    private static T? ReadNullable<T>(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
        {
            return default;
        }

        return value.Deserialize<T>(options);
    }

    private static void WriteIfProvided<T>(
        Utf8JsonWriter writer,
        JsonSerializerOptions options,
        UpdateRuleRequest request,
        string propertyName,
        T value)
    {
        if (!request.HasField(propertyName))
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        JsonSerializer.Serialize(writer, value, options);
    }
}
