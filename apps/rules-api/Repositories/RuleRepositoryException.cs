namespace ImagingPipeline.Rules.Api.Repositories;

public sealed class RuleRepositoryException : Exception
{
    public RuleRepositoryException(string message)
        : base(message)
    {
    }

    public RuleRepositoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
