using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.TbPublisher.Domain;

namespace ImagingPipeline.TbPublisher.Application;

public sealed class TbPublisherOutputMessageBuilder : ITbPublisherOutputMessageBuilder
{
    public OutputMessageMappingResult Map(GatewayOutputMessageDto message, string focusedPxWkt)
    {
        var missionId = DeterministicIdGenerator.CreateMissionId(message.TaskId);
        var outputMessages = message.TilingConfigs
            .Select((tilingConfig, index) => BuildOutput(message, tilingConfig, focusedPxWkt, missionId, index))
            .ToList();

        return OutputMessageMappingResult.Success(outputMessages);
    }

    private static TbPublisherOutputMessageDto BuildOutput(
        GatewayOutputMessageDto message,
        TilingConfig tilingConfig,
        string focusedPxWkt,
        string missionId,
        int tilingIndex) =>
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
                    AlgorithmName = message.AlgorithmName,
                    ImageWidth = message.ImageWidth,
                    ImageHeight = message.ImageHeight,
                    RoiFootprint = message.RoiFootprint,
                    ImageTime = message.PhotoTime,
                    SensorName = message.SensorName,
                    SensorType = message.SensorType
                }
            },
            RequestId = DeterministicIdGenerator.CreateRequestId(message.TaskId, tilingIndex),
            TaskId = message.TaskId
        };
}
