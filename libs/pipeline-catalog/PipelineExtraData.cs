using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace ImagingPipeline.PipelineCatalog;

/// <summary>An owned, immutable JSON object. Property names and JSON value types are preserved.</summary>
[TypeConverter(typeof(PipelineExtraDataConverter))]
public sealed class PipelineExtraData
{
    public static PipelineExtraData Empty { get; } = Parse("{}");

    public JsonElement Value { get; }

    public PipelineExtraData(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Pipeline ExtraData must be a JSON object.", nameof(value));
        Value = value.Clone();
    }

    public static PipelineExtraData Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Pipeline ExtraData must be a JSON object.");
            return new(document.RootElement);
        }
        catch (JsonException)
        {
            // Configuration errors must not echo arbitrary body data or include parser excerpts.
            throw new FormatException("Pipeline ExtraData must contain a valid JSON object.");
        }
    }

    public override string ToString() => "PipelineExtraData { Values = [redacted] }";
}

public sealed class PipelineExtraDataConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string json ? PipelineExtraData.Parse(json) : base.ConvertFrom(context, culture, value);
}
