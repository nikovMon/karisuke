using ImagingPipeline.Common.Dtos.Messaging;

namespace ImagingPipeline.TbPublisher.Application;

public interface ITbPublisherOutputMessageBuilder
{
    IReadOnlyList<TbPublisherOutputMessageDto> Map(GatewayOutputMessageDto message, string focusedPxWkt);
}
