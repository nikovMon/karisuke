namespace ImagingPipeline.RuleEngine.Input;

/// <summary>
/// The incoming message breaks its contract. Retrying cannot fix it, so it should be dead-lettered.
/// <see cref="ErrorCode"/> is a stable, bounded code for logs and metrics.
/// </summary>
public sealed class InvalidInputMessageException(string message, string errorCode) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
