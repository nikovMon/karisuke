namespace ImagingPipeline.Rules.Api.Services;

public sealed class RulePersistenceException : Exception
{
    public RulePersistenceException(string message)
        : base(message)
    {
    }

    public RulePersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
