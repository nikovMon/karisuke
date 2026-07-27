using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Gateway.Messages;

namespace ImagingPipeline.TbPublisher.MessageHandling;

public sealed class InputMessageValidator : IInputMessageValidator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public InputMessageValidationResult Validate(byte[] body)
    {
        GatewayOutputMessageDto? message;
        try
        {
            message = JsonSerializer.Deserialize<GatewayOutputMessageDto>(body, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return InputMessageValidationResult.Failure([$"message body is not valid JSON: {ex.Message}"]);
        }

        if (message is null)
        {
            return InputMessageValidationResult.Failure(["message body cannot be empty."]);
        }

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(message, new ValidationContext(message), results, validateAllProperties: true);
        if (results.Count > 0)
        {
            return InputMessageValidationResult.Failure(
                results.Select(result => result.ErrorMessage ?? "message is invalid.").ToArray());
        }

        return InputMessageValidationResult.Success(message);
    }
}
