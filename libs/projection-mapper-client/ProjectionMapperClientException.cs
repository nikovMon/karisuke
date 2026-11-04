namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperClientException : Exception
{
    public ProjectionMapperFailureKind Kind { get; } = ProjectionMapperFailureKind.Unknown;
    public string Code { get; } = "projection_mapper_unknown";
    public string? Operation { get; }
    public int? HttpStatus { get; }

    public ProjectionMapperClientException(string message)
        : base(message)
    {
    }

    public ProjectionMapperClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProjectionMapperClientException(
        ProjectionMapperFailureKind kind,
        string code,
        string message,
        string operation,
        int? httpStatus = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
        Operation = operation;
        HttpStatus = httpStatus;
    }
}

public enum ProjectionMapperFailureKind
{
    Unknown,
    Transient,
    DependencyResponse,
    Configuration
}
