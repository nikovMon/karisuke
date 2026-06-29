namespace ImagingPipeline.Gateway.Domain;

public abstract class GatewayProcessingException : Exception
{
    protected GatewayProcessingException(string message)
        : base(message)
    {
    }

    protected GatewayProcessingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
