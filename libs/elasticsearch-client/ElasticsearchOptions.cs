using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Elasticsearch.Net;

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

public sealed class SystemTextJsonSourceSerializer : IElasticsearchSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public T Deserialize<T>(Stream stream) =>
        JsonSerializer.Deserialize<T>(stream, Options)!;

    public object Deserialize(Type type, Stream stream) =>
        JsonSerializer.Deserialize(stream, type, Options)!;

    public async Task<T> DeserializeAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        (await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken))!;

    public async Task<object> DeserializeAsync(
        Type type,
        Stream stream,
        CancellationToken cancellationToken = default) =>
        (await JsonSerializer.DeserializeAsync(stream, type, Options, cancellationToken))!;

    public void Serialize<T>(
        T data,
        Stream stream,
        SerializationFormatting formatting = SerializationFormatting.None) =>
        JsonSerializer.Serialize(stream, data, Options);

    public Task SerializeAsync<T>(
        T data,
        Stream stream,
        SerializationFormatting formatting = SerializationFormatting.None,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(stream, data, Options, cancellationToken);

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            var metadataId = typeInfo.Properties.FirstOrDefault(property =>
                string.Equals(property.Name, "_id", StringComparison.Ordinal));
            if (metadataId is not null)
            {
                metadataId.ShouldSerialize = static (_, _) => false;
            }
        });

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
