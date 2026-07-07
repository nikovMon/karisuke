namespace ImagingPipeline.TbPublisher.Errors;

public abstract class TbPublisherProcessingException : Exception
{
    protected TbPublisherProcessingException(string message)
        : base(message)
    {
    }

    protected TbPublisherProcessingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
