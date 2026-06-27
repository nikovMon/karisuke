using System.Text.Json;
using System.Text.Json.Serialization;
using Elasticsearch.Net;

namespace ImagingPipeline.ElasticsearchClient;

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
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
