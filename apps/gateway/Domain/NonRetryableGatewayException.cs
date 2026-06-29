namespace ImagingPipeline.Gateway.Domain;

public sealed class NonRetryableGatewayException : GatewayProcessingException
{
    public NonRetryableGatewayException(string message, string errorCode = "gateway.non_retryable")
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
