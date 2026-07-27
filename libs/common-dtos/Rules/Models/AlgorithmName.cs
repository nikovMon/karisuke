using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

[JsonConverter(typeof(AlgorithmNameJsonConverter))]
public enum AlgorithmName
{
    FindAir,
    Rpn
}

public static class AlgorithmNameContract
{
    public const string AllowedJsonValues = "'FindAir' or 'Rpn'";

    public static bool IsDefined(AlgorithmName value) =>
        value is AlgorithmName.FindAir or AlgorithmName.Rpn;

    internal static bool TryParseJsonValue(string? value, out AlgorithmName algorithmName)
    {
        switch (value)
        {
            case nameof(AlgorithmName.FindAir):
                algorithmName = AlgorithmName.FindAir;
                return true;
            case nameof(AlgorithmName.Rpn):
                algorithmName = AlgorithmName.Rpn;
                return true;
            default:
                algorithmName = default;
                return false;
        }
    }

    internal static string GetJsonValue(AlgorithmName value) =>
        value switch
        {
            AlgorithmName.FindAir => nameof(AlgorithmName.FindAir),
            AlgorithmName.Rpn => nameof(AlgorithmName.Rpn),
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unsupported algorithm name.")
        };
}

public sealed class AlgorithmNameJsonConverter : JsonConverter<AlgorithmName>
{
    public override AlgorithmName Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            AlgorithmNameContract.TryParseJsonValue(reader.GetString(), out var algorithmName))
        {
            return algorithmName;
        }

        throw new JsonException(
            $"Algorithm name must be either {AlgorithmNameContract.AllowedJsonValues}.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        AlgorithmName value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(AlgorithmNameContract.GetJsonValue(value));
}
