namespace ImagingPipeline.Gateway.Configuration;

public sealed class ElasticsearchSettings
{
    public const string SectionName = "Elasticsearch";

    public string Uri { get; set; } = "http://localhost:9200";
    public string IndexName { get; set; } = "rules";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public int RefreshIntervalSeconds { get; set; } = 60;
    public int RequestTimeoutSeconds { get; set; } = 30;

    internal bool IsValid(out string error)
    {
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) ||
            (uri.Scheme != System.Uri.UriSchemeHttp && uri.Scheme != System.Uri.UriSchemeHttps))
        {
            error = "Elasticsearch Uri must be an absolute HTTP or HTTPS URI.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(IndexName))
        {
            error = "Elasticsearch IndexName must not be empty.";
            return false;
        }

        if (RefreshIntervalSeconds <= 0 || RequestTimeoutSeconds <= 0)
        {
            error = "Elasticsearch refresh interval and request timeout must be greater than zero.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ApiKey) &&
            (!string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password)))
        {
            error = "Elasticsearch ApiKey cannot be configured together with Username or Password.";
            return false;
        }

        if ((string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password)) ||
            (!string.IsNullOrWhiteSpace(Username) && string.IsNullOrWhiteSpace(Password)))
        {
            error = "Elasticsearch Username and Password must be configured together.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
