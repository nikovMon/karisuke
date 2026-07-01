namespace ImagingPipeline.Gateway.Errors;

public sealed class GatewayDependencyException : GatewayProcessingException
{
    public GatewayDependencyException(string message)
        : base(message)
    {
    }

    public GatewayDependencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
