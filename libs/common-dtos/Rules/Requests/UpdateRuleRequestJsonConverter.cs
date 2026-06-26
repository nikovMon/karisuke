using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

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
            switch (property.Name)
            {
                case "ruleName":
                    request.ProvidedFields.Add(property.Name);
                    request.RuleName = ReadNullable<string>(property.Value, options);
                    break;
                case "description":
                    request.ProvidedFields.Add(property.Name);
                    request.Description = ReadNullable<string>(property.Value, options);
                    break;
                case "algorithmName":
                    request.ProvidedFields.Add(property.Name);
                    request.AlgorithmName = ReadNullableValue<AlgorithmName>(property.Value, options);
                    break;
                case "sensors":
                    request.ProvidedFields.Add(property.Name);
                    request.Sensors = ReadNullable<Dictionary<string, List<string>>>(property.Value, options);
                    break;
                case "isActive":
                    request.ProvidedFields.Add(property.Name);
                    request.IsActive = ReadNullableValue<bool>(property.Value, options);
                    break;
                case "tenants":
                    request.ProvidedFields.Add(property.Name);
                    request.Tenants = ReadNullable<List<TenantConfigDto>>(property.Value, options);
                    break;
                case "minResolution":
                    request.ProvidedFields.Add(property.Name);
                    request.MinResolution = ReadNullableValue<double>(property.Value, options);
                    break;
                case "maxResolution":
                    request.ProvidedFields.Add(property.Name);
                    request.MaxResolution = ReadNullableValue<double>(property.Value, options);
                    break;
                case "area":
                    request.ProvidedFields.Add(property.Name);
                    request.Area = ReadNullable<string>(property.Value, options);
                    break;
                case "wkt":
                    request.ProvidedFields.Add(property.Name);
                    request.Wkt = ReadNullable<string>(property.Value, options);
                    break;
                case "geoJson":
                    request.ProvidedFields.Add(property.Name);
                    request.GeoJson = property.Value.Clone();
                    break;
                case "maxLookBackDay":
                    request.ProvidedFields.Add(property.Name);
                    request.MaxLookBackDay = ReadNullableValue<int>(property.Value, options);
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
        WriteIfProvided(writer, options, value, "minResolution", value.MinResolution);
        WriteIfProvided(writer, options, value, "maxResolution", value.MaxResolution);
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

    private static T? ReadNullableValue<T>(JsonElement value, JsonSerializerOptions options)
        where T : struct
    {
        if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
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
