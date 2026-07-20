using ImagingPipeline.Common.Dtos.Messaging;

namespace ImagingPipeline.TbPublisher.MessageHandling;

public interface ITbPublisherOutputMessageBuilder
{
    IReadOnlyList<TbPublisherOutputMessageDto> Map(GatewayOutputMessageDto message, string focusedPxWkt);
}
