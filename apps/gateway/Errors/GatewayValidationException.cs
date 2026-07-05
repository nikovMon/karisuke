namespace ImagingPipeline.Gateway.Errors;

public sealed class GatewayValidationException : GatewayProcessingException
{
    public GatewayValidationException(string message, string errorCode = "gateway.validation")
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
