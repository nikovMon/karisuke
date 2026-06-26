namespace ImagingPipeline.ElasticsearchClient;

public sealed class ElasticsearchClientException : Exception
{
    public ElasticsearchClientException(string message)
        : base(message)
    {
    }
}
