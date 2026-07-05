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
                case "tenantsInfo":
                    request.ProvidedFields.Add(property.Name);
                    request.TenantsInfo = ReadNullable<List<TenantInfo>>(property.Value, options);
                    break;
                case "minimumResolution":
                    request.ProvidedFields.Add(property.Name);
                    request.MinimumResolution = ReadNullableValue<double>(property.Value, options);
                    break;
                case "maximumResolution":
                    request.ProvidedFields.Add(property.Name);
                    request.MaximumResolution = ReadNullableValue<double>(property.Value, options);
                    break;
                case "area":
                    request.ProvidedFields.Add(property.Name);
                    request.Area = ReadNullable<string>(property.Value, options);
                    break;
                case "locationWkt":
                    request.ProvidedFields.Add(property.Name);
                    request.LocationWkt = ReadNullable<string>(property.Value, options);
                    break;
                case "locationGeoJson":
                    request.ProvidedFields.Add(property.Name);
                    request.LocationGeoJson = property.Value.Clone();
                    break;
                case "isPhotoOld":
                    request.ProvidedFields.Add(property.Name);
                    request.IsPhotoOld = ReadNullableValue<bool>(property.Value, options);
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
        WriteIfProvided(writer, options, value, "tenantsInfo", value.TenantsInfo);
        WriteIfProvided(writer, options, value, "minimumResolution", value.MinimumResolution);
        WriteIfProvided(writer, options, value, "maximumResolution", value.MaximumResolution);
        WriteIfProvided(writer, options, value, "area", value.Area);
        WriteIfProvided(writer, options, value, "locationWkt", value.LocationWkt);
        WriteIfProvided(writer, options, value, "locationGeoJson", value.LocationGeoJson);
        WriteIfProvided(writer, options, value, "isPhotoOld", value.IsPhotoOld);
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
