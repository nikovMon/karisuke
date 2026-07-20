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
        GatewayInputMessage input,
        IReadOnlyList<RuleMatchResult> matches)
    {
        var outputCount = matches.Sum(match => match.Rule.TenantsInfo.Count);
        var outputs = new List<GatewayOutputMessage>(outputCount);

        foreach (var match in matches)
        {
            var roiFootprint = _geometry.WriteGeoJson(match.IntersectionGeometry);

            foreach (var tenant in match.Rule.TenantsInfo)
            {
                outputs.Add(new GatewayOutputMessage(
                    BuildOutput(input, match, tenant, roiFootprint),
                    match.Rule.Id,
                    tenant.TenantId));
            }
        }

        return outputs;
    }

    private byte[] BuildOutput(
        GatewayInputMessage input,
        RuleMatchResult match,
        TenantInfo tenant,
        JsonElement roiFootprint)
    {
        var payload = new GatewayOutputPayload
        {
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
}
