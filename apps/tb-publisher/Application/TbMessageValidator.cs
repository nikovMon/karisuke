using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ImagingPipeline.TbPublisher.Dtos.Inbound;

namespace ImagingPipeline.TbPublisher.Application;

public sealed class TbMessageValidator : ITbMessageValidator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public TbMessageValidationResult Validate(byte[] body)
    {
        TbMessageDto? message;
        try
        {
            message = JsonSerializer.Deserialize<TbMessageDto>(body, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return TbMessageValidationResult.Failure([$"message body is not valid JSON: {ex.Message}"]);
        }

        if (message is null)
        {
            return TbMessageValidationResult.Failure(["message body cannot be empty."]);
        }

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(message, new ValidationContext(message), results, validateAllProperties: true);
        if (results.Count > 0)
        {
            return TbMessageValidationResult.Failure(
                results.Select(result => result.ErrorMessage ?? "message is invalid.").ToArray());
        }

        return TbMessageValidationResult.Success(message);
    }
}
