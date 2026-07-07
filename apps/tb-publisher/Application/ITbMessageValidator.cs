using ImagingPipeline.Common.Dtos.Messaging;

namespace ImagingPipeline.TbPublisher.Application;

public interface ITbMessageValidator
{
    TbMessageValidationResult Validate(byte[] body);
}

public sealed record TbMessageValidationResult(bool IsValid, GatewayOutputMessageDto? Message, IReadOnlyList<string> Errors)
{
    public static TbMessageValidationResult Success(GatewayOutputMessageDto message) => new(true, message, []);

    public static TbMessageValidationResult Failure(IReadOnlyList<string> errors) => new(false, null, errors);
}
