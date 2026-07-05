using ImagingPipeline.TbPublisher.Dtos.Inbound;
using ImagingPipeline.TbPublisher.Dtos.Outbound;

namespace ImagingPipeline.TbPublisher.Application;

public interface ITilingConfigMapper
{
    TilingConfigMappingResult Map(TbMessageDto message, string focusedPxWkt, string missionId);
}

public sealed record TilingConfigMappingResult(bool IsSuccess, IReadOnlyList<TbPublisherOutputMessageDto>? Messages, string? Error)
{
    public static TilingConfigMappingResult Success(IReadOnlyList<TbPublisherOutputMessageDto> messages) => new(true, messages, null);

    public static TilingConfigMappingResult Failure(string error) => new(false, null, error);
}
