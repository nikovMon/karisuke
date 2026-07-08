using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Contracts.Messages;
using ImagingPipeline.Gateway.Errors;
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
        if (match.Rule.AlgorithmName is null)
        {
            throw new GatewayValidationException(
                $"Rule '{match.Rule.Id}' cannot build output without algorithmName.",
                "gateway.rule_missing_algorithm");
        }

        var payload = new GatewayOutputPayload
        {
            RuleId = match.Rule.Id,
            AlgorithmName = match.Rule.AlgorithmName.Value,
            TenantId = tenant.TenantId,
            TilingConfigs = tenant.TilingConfigs,
            ImageId = input.ImageId,
            RoiFootprint = _geometry.WriteGeoJson(match.IntersectionGeometry),
            PhotoTime = input.PhotoTime,
            SensorType = input.SensorType
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    }
}
