using System.Text.Json;

namespace ImagingPipeline.RabbitMqClient;

/// <summary>
/// A generic base class for RabbitMQ message handlers that expect a JSON payload.
/// Handles boilerplate parsing, null-checks, and error conversion.
/// </summary>
/// <typeparam name="TMessage">The typed DTO expected in the message body.</typeparam>
public abstract class RabbitMqJsonMessageHandler<TMessage> : IRabbitMqMessageHandler where TMessage : class
{
    protected static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<RabbitMqMessageProcessingResult> HandleAsync(
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        TMessage? input;
        try
        {
            input = JsonSerializer.Deserialize<TMessage>(message.BodyAsUtf8(), JsonOptions);
        }
        catch (JsonException ex)
        {
            return RabbitMqMessageProcessingResult.Failure($"Json deserialization failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RabbitMqMessageProcessingResult.Failure($"Unhandled deserialization error: {ex.Message}");
        }

        if (input is null)
        {
            return RabbitMqMessageProcessingResult.Failure("Deserialization produced null.");
        }

        return await HandleMessageAsync(input, message, cancellationToken);
    }

    /// <summary>
    /// Handles the strongly-typed message payload.
    /// </summary>
    protected abstract Task<RabbitMqMessageProcessingResult> HandleMessageAsync(
        TMessage payload,
        RabbitMqMessageEnvelope originalMessage,
        CancellationToken cancellationToken);
}
