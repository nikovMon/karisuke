using ImagingPipeline.Common.Dtos.Messaging;

namespace ImagingPipeline.TbPublisher.Application;

public interface ITbPublisherOutputMessageBuilder
{
    OutputMessageMappingResult Map(GatewayOutputMessageDto message, string focusedPxWkt);
}

public sealed record OutputMessageMappingResult(bool IsSuccess, IReadOnlyList<TbPublisherOutputMessageDto>? Messages, string? Error)
{
    public static OutputMessageMappingResult Success(IReadOnlyList<TbPublisherOutputMessageDto> messages) => new(true, messages, null);

    public static OutputMessageMappingResult Failure(string error) => new(false, null, error);
}
