namespace ImagingPipeline.Observability;

/// <summary>
/// A bounded fragment of a sanitized JSON diagnostic document. The ECS and console providers
/// preserve these six fields independently of their general attribute limits. Consumers can
/// detect missing fragments using the document ID, count, length and hash.
/// </summary>
public sealed class LosslessJsonLogChunk
{
    public const int MaximumChunkLength = 2048;

    public LosslessJsonLogChunk(string id, int index, int count, string text, string sha256, int documentLength)
    {
        if (id.Length != 32 || sha256.Length != 64 || text.Length > MaximumChunkLength ||
            index < 0 || index >= count || documentLength < text.Length)
        {
            throw new ArgumentException("Invalid diagnostic JSON chunk.");
        }
        Attributes = new Dictionary<string, object?>
        {
            ["failure_log_id"] = id,
            ["failure_log_chunk_index"] = index,
            ["failure_log_chunk_count"] = count,
            ["failure_log_json_chunk"] = text,
            ["failure_log_sha256"] = sha256,
            ["failure_log_document_length"] = documentLength
        };
    }

    public IReadOnlyDictionary<string, object?> Attributes { get; }
    public override string ToString() => "Retained message diagnostic JSON fragment.";
}
