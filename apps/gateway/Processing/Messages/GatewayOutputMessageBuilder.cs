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
        string inputMessageId,
        GatewayInputMessage input,
        IReadOnlyList<RuleMatchResult> matches)
    {
        var outputCount = matches.Sum(match => match.Rule.TenantsInfo.Count);
        var outputs = new List<GatewayOutputMessage>(outputCount);
        var outputIndex = 0;

        foreach (var match in matches)
        {
            var roiFootprint = _geometry.WriteGeoJsonUtf8(match.IntersectionGeometry);

            foreach (var tenant in match.Rule.TenantsInfo)
            {
                outputs.Add(new GatewayOutputMessage(
                    BuildOutput(inputMessageId, input, match, tenant, roiFootprint, outputIndex),
                    match.Rule.Id,
                    tenant.TenantId));
                outputIndex++;
            }
        }

        return outputs;
    }

    private byte[] BuildOutput(
        string inputMessageId,
        GatewayInputMessage input,
        RuleMatchResult match,
        TenantInfo tenant,
        ReadOnlySpan<byte> roiFootprint,
        int outputIndex)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString(
            "taskId",
            CreateTaskId(inputMessageId, match.Rule.Id, tenant.TenantId, outputIndex));
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
        writer.WriteNumber("intersectionArea", match.IntersectionGeometry.Area);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static string CreateTaskId(
        string inputMessageId,
        string ruleId,
        string tenantId,
        int outputIndex) =>
        $"{inputMessageId}:gateway-task:{ruleId}:{tenantId}:{outputIndex}";
}
