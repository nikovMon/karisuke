using ImagingPipeline.Common.Dtos.Gateway.Messages;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.TbPublisher.Identity;

namespace ImagingPipeline.TbPublisher.MessageHandling;

public sealed class TbPublisherOutputMessageBuilder : ITbPublisherOutputMessageBuilder
{
    public IReadOnlyList<TbPublisherOutputMessageDto> Map(GatewayOutputMessageDto message, string focusedPxWkt)
    {
        var missionId = DeterministicIdGenerator.CreateMissionId(message.TaskId);
        return message.TilingConfigs
            .Select(tilingConfig => BuildOutput(message, tilingConfig, focusedPxWkt, missionId))
            .ToList();
    }

    private static TbPublisherOutputMessageDto BuildOutput(
        GatewayOutputMessageDto message,
        TilingConfig tilingConfig,
        string focusedPxWkt,
        string missionId) =>
        new()
        {
            FrameMetadata = new FrameMetadataDto
            {
                General = new FrameGeneralDto
                {
                    ImageFileUri = message.ImageUrl,
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
                    ImageUrl = message.ImageUrl,
                    RuleId = message.RuleId,
                    ResolutionMPerPx = message.ResolutionMPerPx,
                    AlgorithmNames = message.AlgorithmNames,
                    ImageWidth = message.ImageWidth,
                    ImageHeight = message.ImageHeight,
                    RoiFootprint = message.RoiFootprint,
                    ImageTime = message.PhotoTime,
                    SensorName = message.SensorName,
                    SensorType = message.SensorType
                }
            },
            TaskId = message.TaskId
        };
}
