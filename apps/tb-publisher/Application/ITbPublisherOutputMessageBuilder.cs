using ImagingPipeline.TbPublisher.Dtos.Inbound;
using ImagingPipeline.TbPublisher.Dtos.Outbound;

namespace ImagingPipeline.TbPublisher.Application;

public interface ITbPublisherOutputMessageBuilder
{
    OutputMessageMappingResult Map(TbMessageDto message, string focusedPxWkt, string missionId);
}

public sealed record OutputMessageMappingResult(bool IsSuccess, IReadOnlyList<TbPublisherOutputMessageDto>? Messages, string? Error)
{
    public static OutputMessageMappingResult Success(IReadOnlyList<TbPublisherOutputMessageDto> messages) => new(true, messages, null);

    public static OutputMessageMappingResult Failure(string error) => new(false, null, error);
}
