using System.Buffers;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Contracts.Messages;
using ImagingPipeline.Gateway.Processing.Rules;

namespace ImagingPipeline.Gateway.Processing.Messages;

public sealed class GatewayOutputMessageBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GatewayGeometryConverter _geometry;

    public GatewayOutputMessageBuilder(GatewayGeometryConverter geometry)
    {
        _geometry = geometry;
    }

    public IReadOnlyList<GatewayOutputMessage> BuildOutputs(
        GatewayInputMessage input,
        IReadOnlyList<RuleMatchResult> matches)
    {
        var outputCount = matches.Sum(match => match.Rule.TenantsInfo.Count);
        var outputs = new List<GatewayOutputMessage>(outputCount);

        foreach (var match in matches)
        {
            var roiFootprint = _geometry.WriteGeoJsonUtf8(match.IntersectionGeometry);

            foreach (var tenant in match.Rule.TenantsInfo)
            {
                outputs.Add(new GatewayOutputMessage(
                    BuildOutput(input, match, tenant, roiFootprint),
                    match.Rule.Id,
                    tenant.TenantId,
                    string.Join(",", match.Rule.AlgorithmNames)));
            }
        }

        return outputs;
    }

    private byte[] BuildOutput(
        GatewayInputMessage input,
        RuleMatchResult match,
        TenantInfo tenant,
        ReadOnlySpan<byte> roiFootprint)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString(
            "taskId",
            CreateTaskId(input.ImageId, match.Rule.Id, tenant.TenantId));
        writer.WriteString("ruleId", match.Rule.Id);
        writer.WriteStartArray("algorithmName");
        foreach (var algorithmName in match.Rule.AlgorithmNames)
        {
            writer.WriteStringValue(algorithmName.ToString());
        }
        writer.WriteEndArray();
        writer.WriteString("tenantId", tenant.TenantId);
        writer.WritePropertyName("tilingConfigs");
        JsonSerializer.Serialize(writer, tenant.TilingConfigs, JsonOptions);
        writer.WriteString("imageId", input.ImageId);
        writer.WritePropertyName("roiFootprint");
        writer.WriteRawValue(roiFootprint, skipInputValidation: true);
        writer.WriteString("photoTime", input.PhotoTime);
        writer.WriteString("sensorType", input.SensorType);
        writer.WriteString("imageUrl", input.ImageUrl);
        writer.WriteNumber("imageWidth", input.ImageWidth);
        writer.WriteNumber("imageHeight", input.ImageHeight);
        writer.WriteNumber("bestResolution", input.BestResolution);
        writer.WriteString("sensorName", input.SensorName);
        writer.WriteString("areaOfInterest", input.AreaOfInterest);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static string CreateTaskId(
        string imageId,
        string ruleId,
        string tenantId) =>
        $"{imageId}:gateway-task:{ruleId}:{tenantId}";
}
