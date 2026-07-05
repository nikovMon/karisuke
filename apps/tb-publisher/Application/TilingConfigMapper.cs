using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.TbPublisher.Domain;
using ImagingPipeline.TbPublisher.Dtos.Inbound;
using ImagingPipeline.TbPublisher.Dtos.Outbound;

namespace ImagingPipeline.TbPublisher.Application;

public sealed class TilingConfigMapper : ITilingConfigMapper
{
    public TilingConfigMappingResult Map(TbMessageDto message, string focusedPxWkt, string missionId)
    {
        if (message.TilingConfigs.Count == 0)
        {
            return TilingConfigMappingResult.Failure("tilingConfigs must contain at least one entry.");
        }

        foreach (var tilingConfig in message.TilingConfigs)
        {
            if (!TilingConfigValidator.IsValid(tilingConfig, out var error))
            {
                return TilingConfigMappingResult.Failure(error);
            }
        }

        var outputMessages = message.TilingConfigs
            .Select(tilingConfig => BuildOutput(message, tilingConfig, focusedPxWkt, missionId))
            .ToList();

        return TilingConfigMappingResult.Success(outputMessages);
    }

    private static TbPublisherOutputMessageDto BuildOutput(
        TbMessageDto message,
        TilingConfig tilingConfig,
        string focusedPxWkt,
        string missionId) =>
        new()
        {
            FrameMetadata = new FrameMetadataDto
            {
                General = new FrameGeneralDto
                {
                    ImageFileUri = message.ImageUrl ?? string.Empty,
                    Id = message.ImageId
                }
            },
            ModelMetadata = new ModelMetadataDto
            {
                OverlapHeight = tilingConfig.TileOverlapHeight,
                TbCropSizeY = tilingConfig.TileSizeHeight,
                OverlapWidth = tilingConfig.TileOverlapWidth,
                TbCropSizeX = tilingConfig.TileSizeWidth
            },
            FocusedPxWkt = focusedPxWkt,
            MissionMetadata = new MissionMetadataDto
            {
                MissionId = missionId,
                TenantId = message.TenantId,
                Overlay = new OverlayDto
                {
                    ImageId = message.ImageId,
                    ImageUrl = message.ImageUrl ?? string.Empty,
                    RuleId = message.RuleId,
                    ResolutionMPerPx = message.ResolutionMPerPx ?? 0,
                    AlgoritmName = message.AlgorithmName,
                    ImageWidth = message.ImageWidth ?? 0,
                    ImageHeight = message.ImageHeight ?? 0,
                    RoiFootprint = message.RoiFootprint,
                    ImageTime = message.PhotoTime,
                    SensorName = message.SensorName,
                    SensorType = message.SensorType
                }
            },
            RequestId = Guid.NewGuid().ToString(),
            TaskId = Guid.NewGuid().ToString()
        };
}
