namespace ImagingPipeline.Gateway.Domain;

public sealed class InfrastructureUnavailableException : GatewayProcessingException
{
    public InfrastructureUnavailableException(string message)
        : base(message)
    {
    }

    public InfrastructureUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
