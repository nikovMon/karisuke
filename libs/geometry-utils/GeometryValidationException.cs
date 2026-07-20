namespace ImagingPipeline.GeometryUtils;

public sealed class GeometryValidationException : Exception
{
    public GeometryValidationException(string message)
        : base(message)
    {
    }

    public GeometryValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
