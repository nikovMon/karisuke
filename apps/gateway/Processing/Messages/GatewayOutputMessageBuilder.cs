using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Dtos.Messages;
using ImagingPipeline.Gateway.Processing.Rules;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Processing.Messages;

public sealed class GatewayOutputMessageBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OutputSettings _settings;
    private readonly GatewayGeometryConverter _geometry;

    public GatewayOutputMessageBuilder(
        IOptions<OutputSettings> settings,
        GatewayGeometryConverter geometry)
    {
        _settings = settings.Value;
        _geometry = geometry;
    }

    public IReadOnlyList<GatewayOutputMessage> BuildOutputs(
        GatewayInputMessage input,
        IReadOnlyList<RuleMatchResult> matches)
    {
        var outputs = new List<GatewayOutputMessage>();

        foreach (var match in matches)
        {
            foreach (var tenant in match.Rule.TenantsInfo)
            {
                outputs.Add(new GatewayOutputMessage(
                    BuildOutput(input, match, tenant),
                    match.Rule.Id,
                    tenant.TenantId));
            }
        }

        return outputs;
    }

    private byte[] BuildOutput(
        GatewayInputMessage input,
        RuleMatchResult match,
        TenantInfo tenant)
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
        TenantInfo tenant)
    {
        var metadata = new GatewayMatchedOutputDto
        {
            RuleId = rule.Id,
            RuleName = rule.RuleName,
            Description = rule.Description,
            AlgorithmName = rule.AlgorithmName?.ToString() ?? string.Empty,
            Area = rule.Area,
            TenantId = tenant.TenantId,
            TilingConfigs = tenant.TilingConfigs,
            MatchedAt = DateTimeOffset.UtcNow
        };

        writer.WritePropertyName(_settings.RoutingMetadataPropertyName);
        JsonSerializer.Serialize(writer, metadata, JsonOptions);
    }

    private void WriteFocusedGeometry(Utf8JsonWriter writer, RuleMatchResult match)
    {
        writer.WritePropertyName(_settings.FocusedGeometryPropertyName);
        writer.WriteStartObject();
        writer.WriteString("wkt", _geometry.WriteWkt(match.IntersectionGeometry));
        writer.WritePropertyName("geoJson");
        _geometry.WriteGeoJson(match.IntersectionGeometry).WriteTo(writer);
        writer.WriteEndObject();
    }
}

public sealed record GatewayOutputMessage(byte[] Body, string RuleId, string TenantId);
