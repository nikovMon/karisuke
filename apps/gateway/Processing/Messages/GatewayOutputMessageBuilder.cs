using System.Text.Json;
using ImagingPipeline.Common.Dtos.Gateway.Messages;
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
            var roiFootprint = _geometry.WriteGeoJson(match.IntersectionGeometry);

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
        JsonElement roiFootprint,
        int outputIndex)
    {
        var payload = new GatewayOutputPayload
        {
            TaskId = CreateTaskId(inputMessageId, match.Rule.Id, tenant.TenantId, outputIndex),
            RuleId = match.Rule.Id,
            AlgorithmName = match.Rule.AlgorithmName,
            TenantId = tenant.TenantId,
            TilingConfigs = tenant.TilingConfigs,
            ImageId = input.ImageId,
            RoiFootprint = roiFootprint,
            PhotoTime = input.PhotoTime,
            SensorType = input.SensorType
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    }

    private static string CreateTaskId(
        string inputMessageId,
        string ruleId,
        string tenantId,
        int outputIndex) =>
        $"{inputMessageId}:gateway-task:{ruleId}:{tenantId}:{outputIndex}";
}
