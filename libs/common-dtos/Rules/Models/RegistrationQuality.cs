using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

[JsonConverter(typeof(RegistrationQualityJsonConverter))]
public enum RegistrationQuality
{
    Accurate,
    Sensor
}

public static class RegistrationQualityExtensions
{
    public static bool TryParseJsonValue(string? value, out RegistrationQuality quality)
    {
        if (string.Equals(value, "Accurate", StringComparison.Ordinal))
        {
            quality = RegistrationQuality.Accurate;
            return true;
        }

        if (string.Equals(value, "Sensor", StringComparison.Ordinal))
        {
            quality = RegistrationQuality.Sensor;
            return true;
        }

        quality = default;
        return false;
    }

    public static string ToJsonValue(this RegistrationQuality quality) =>
        quality switch
        {
            RegistrationQuality.Accurate => "Accurate",
            RegistrationQuality.Sensor => "Sensor",
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unsupported registration quality.")
        };
}

public sealed class RegistrationQualityJsonConverter : JsonConverter<RegistrationQuality>
{
    public override RegistrationQuality Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            RegistrationQualityExtensions.TryParseJsonValue(reader.GetString(), out var quality))
        {
            return quality;
        }

        throw new JsonException("Registration quality must be either 'Accurate' or 'Sensor'.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        RegistrationQuality value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToJsonValue());
}
