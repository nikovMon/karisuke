using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Application.Rules;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Dtos.Messages;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Application.Messages;

public sealed class JsonOutputBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OutputSettings _settings;
    private readonly GeometryExtractor _geometryExtractor;

    public JsonOutputBuilder(
        IOptions<OutputSettings> settings,
        GeometryExtractor geometryExtractor)
    {
        _settings = settings.Value;
        _geometryExtractor = geometryExtractor;
    }

    public IReadOnlyList<byte[]> BuildOutputs(
        ValidatedInputMessage input,
        IReadOnlyList<RuleMatchResult> matches)
    {
        var outputs = new List<byte[]>();

        foreach (var match in matches)
        {
            foreach (var tenant in match.Rule.Tenants)
            {
                outputs.Add(BuildOutput(input, match, tenant));
            }
        }

        return outputs;
    }

    private byte[] BuildOutput(
        ValidatedInputMessage input,
        RuleMatchResult match,
        TenantConfigDto tenant)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            if (_settings.PreserveOriginalMessage)
            {
                CopyOriginalProperties(input.OriginalPayload, writer);
            }

            WriteGatewayMetadata(writer, match.Rule, tenant);
            WriteFocusedGeometry(writer, match);

            if (!_settings.PreserveOriginalMessage)
            {
                writer.WritePropertyName("payload");
                input.OriginalPayload.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private void CopyOriginalProperties(JsonElement original, Utf8JsonWriter writer)
    {
        foreach (var property in original.EnumerateObject())
        {
            if (string.Equals(property.Name, _settings.RoutingMetadataPropertyName, StringComparison.Ordinal) ||
                string.Equals(property.Name, _settings.FocusedGeometryPropertyName, StringComparison.Ordinal))
            {
                continue;
            }

            property.WriteTo(writer);
        }
    }

    private void WriteGatewayMetadata(
        Utf8JsonWriter writer,
        RuleConfigDto rule,
        TenantConfigDto tenant)
    {
        var metadata = new GatewayMatchedOutputDto
        {
            RuleId = rule.Id,
            RuleName = rule.RuleName,
            Description = rule.Description,
            AlgorithmName = rule.AlgorithmName?.ToString() ?? string.Empty,
            Area = rule.Area,
            TenantName = tenant.TenantName,
            TilingConfig = tenant.TilingConfig,
            MatchedAt = DateTimeOffset.UtcNow
        };

        writer.WritePropertyName(_settings.RoutingMetadataPropertyName);
        JsonSerializer.Serialize(writer, metadata, JsonOptions);
    }

    private void WriteFocusedGeometry(Utf8JsonWriter writer, RuleMatchResult match)
    {
        writer.WritePropertyName(_settings.FocusedGeometryPropertyName);
        writer.WriteStartObject();
        writer.WriteString("wkt", _geometryExtractor.WriteWkt(match.IntersectionGeometry));
        writer.WritePropertyName("geoJson");
        _geometryExtractor.WriteGeoJson(match.IntersectionGeometry).WriteTo(writer);
        writer.WriteEndObject();
    }
}
