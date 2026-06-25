namespace ImagingPipeline.ElasticsearchClient;

public sealed class ElasticsearchClientOptions
{
    public const string SectionName = "Elasticsearch";

    public string Uri { get; set; } = "http://localhost:9200";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string DefaultIndex { get; set; } = "rules";

    internal bool IsValid(out string error)
    {
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) ||
            (uri.Scheme != System.Uri.UriSchemeHttp && uri.Scheme != System.Uri.UriSchemeHttps))
        {
            error = "Elasticsearch Uri must be an absolute HTTP or HTTPS URI.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DefaultIndex))
        {
            error = "Elasticsearch DefaultIndex must not be empty.";
            return false;
        }

        if ((string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password)) ||
            (!string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password)))
        {
            error = "Elasticsearch Username and Password must be configured together.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
