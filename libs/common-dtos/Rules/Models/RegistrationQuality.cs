using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

[JsonConverter(typeof(RegistrationQualityJsonConverter))]
public enum RegistrationQuality
{
    Accurate = 0,
    Sensor = 1
}

public static class RegistrationQualityContract
{
    private const int MaskBitCapacity = sizeof(int) * 8;
    private static readonly Metadata Cached = CreateMetadata();

    public static string AllowedJsonValues => Cached.AllowedJsonValues;

    public static int Count => Cached.NamesByOrdinal.Length;

    public static void EnsureValid() => _ = Cached;

    public static int GetValidatedOrdinal(RegistrationQuality quality)
    {
        var ordinal = (int)quality;
        if ((uint)ordinal >= (uint)Cached.ValuesByOrdinal.Length ||
            Cached.ValuesByOrdinal[ordinal] != quality)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quality),
                quality,
                "Unsupported registration quality.");
        }

        return ordinal;
    }

    internal static bool TryParseJsonValue(string? value, out RegistrationQuality quality)
    {
        quality = default;
        return value is not null && Cached.ByJsonValue.TryGetValue(value, out quality);
    }

    internal static string GetJsonValue(RegistrationQuality quality) =>
        Cached.NamesByOrdinal[GetValidatedOrdinal(quality)];

    private static Metadata CreateMetadata()
    {
        var names = Enum.GetNames<RegistrationQuality>();
        if (names.Length == 0)
        {
            throw InvalidDefinition("At least one value is required.");
        }

        if (names.Length > MaskBitCapacity)
        {
            throw InvalidDefinition(
                $"At most {MaskBitCapacity} values can be represented by the registration-quality mask.");
        }

        var valuesByOrdinal = new RegistrationQuality[names.Length];
        var namesByOrdinal = new string?[names.Length];
        var byJsonValue = new Dictionary<string, RegistrationQuality>(
            names.Length,
            StringComparer.Ordinal);

        foreach (var name in names)
        {
            var value = Enum.Parse<RegistrationQuality>(name, ignoreCase: false);
            var ordinal = (int)value;

            if ((uint)ordinal >= (uint)names.Length || namesByOrdinal[ordinal] is not null)
            {
                throw InvalidDefinition(
                    "Values must be unique, contiguous, and explicitly numbered from zero.");
            }

            valuesByOrdinal[ordinal] = value;
            namesByOrdinal[ordinal] = name;
            byJsonValue.Add(name, value);
        }

        if (namesByOrdinal.Any(name => name is null))
        {
            throw InvalidDefinition(
                "Values must be unique, contiguous, and explicitly numbered from zero.");
        }

        var validatedNames = namesByOrdinal.Select(name => name!).ToArray();
        return new Metadata(
            byJsonValue.ToFrozenDictionary(StringComparer.Ordinal),
            validatedNames,
            valuesByOrdinal,
            BuildAllowedJsonValues(validatedNames));
    }

    private static string BuildAllowedJsonValues(IReadOnlyList<string> names) =>
        names.Count switch
        {
            1 => $"'{names[0]}'",
            2 => $"'{names[0]}' or '{names[1]}'",
            _ => $"{string.Join(", ", names.Take(names.Count - 1).Select(name => $"'{name}'"))}, or '{names[^1]}'"
        };

    private static InvalidOperationException InvalidDefinition(string reason) =>
        new($"Invalid {nameof(RegistrationQuality)} definition. {reason}");

    private sealed record Metadata(
        FrozenDictionary<string, RegistrationQuality> ByJsonValue,
        string[] NamesByOrdinal,
        RegistrationQuality[] ValuesByOrdinal,
        string AllowedJsonValues);
}

public static class RegistrationQualityExtensions
{
    public static bool TryParseJsonValue(string? value, out RegistrationQuality quality) =>
        RegistrationQualityContract.TryParseJsonValue(value, out quality);

    public static string ToJsonValue(this RegistrationQuality quality) =>
        RegistrationQualityContract.GetJsonValue(quality);
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

        throw new JsonException(
            $"Registration quality must be either {RegistrationQualityContract.AllowedJsonValues}.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        RegistrationQuality value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToJsonValue());
}
