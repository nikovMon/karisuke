namespace ImagingPipeline.TbPublisher.Errors;

public sealed class TbPublisherValidationException : TbPublisherProcessingException
{
    public TbPublisherValidationException(string message)
        : base(message)
    {
    }

    public TbPublisherValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
