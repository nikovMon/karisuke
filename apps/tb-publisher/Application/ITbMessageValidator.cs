using ImagingPipeline.TbPublisher.Dtos.Inbound;

namespace ImagingPipeline.TbPublisher.Application;

public interface ITbMessageValidator
{
    TbMessageValidationResult Validate(byte[] body);
}

public sealed record TbMessageValidationResult(bool IsValid, TbMessageDto? Message, IReadOnlyList<string> Errors)
{
    public static TbMessageValidationResult Success(TbMessageDto message) => new(true, message, []);

    public static TbMessageValidationResult Failure(IReadOnlyList<string> errors) => new(false, null, errors);
}
