namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperClientException : Exception
{
    public ProjectionMapperClientException(string message)
        : base(message)
    {
    }

    public ProjectionMapperClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
