using ImagingPipeline.Common.Dtos.Messaging;

namespace ImagingPipeline.TbPublisher.Application;

public interface IInputMessageValidator
{
    InputMessageValidationResult Validate(byte[] body);
}

public sealed record InputMessageValidationResult(bool IsValid, GatewayOutputMessageDto? Message, IReadOnlyList<string> Errors)
{
    public static InputMessageValidationResult Success(GatewayOutputMessageDto message) => new(true, message, []);

    public static InputMessageValidationResult Failure(IReadOnlyList<string> errors) => new(false, null, errors);
}
