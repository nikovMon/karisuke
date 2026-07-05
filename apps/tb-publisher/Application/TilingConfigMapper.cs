using ImagingPipeline.TbPublisher.Dtos.Inbound;
using ImagingPipeline.TbPublisher.Dtos.Outbound;

namespace ImagingPipeline.TbPublisher.Application;

public sealed class TilingConfigMapper : ITilingConfigMapper
{
    public TilingConfigMappingResult Map(TbMessageDto message, IReadOnlyList<IReadOnlyList<double>> coordinates)
    {
        if (message.TilingConfigs.Count == 0)
        {
            return TilingConfigMappingResult.Failure("tilingConfigs must contain at least one entry.");
        }

        foreach (var tilingConfig in message.TilingConfigs)
        {
            if (!tilingConfig.IsValid(out var error))
            {
                return TilingConfigMappingResult.Failure(error);
            }
        }

        var processedAt = DateTimeOffset.UtcNow;
        var ingestMessages = message.TilingConfigs
            .Select(tilingConfig => new TilingConfigIngestMessageDto
            {
                RuleId = message.RuleId,
                TenantId = message.TenantId,
                ImageId = message.ImageId,
                TilingConfig = tilingConfig,
                Coordinates = coordinates,
                PhotoTime = message.PhotoTime,
                SensorType = message.SensorType,
                ProcessedAt = processedAt
            })
            .ToList();

        return TilingConfigMappingResult.Success(ingestMessages);
    }
}
