using System.Text.Json;
using ImagingPipeline.RuleEngine.Input;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

public interface IElasticOverlayProvider
{
    Task<ProviderOverlayResult> GetAsync(string id, CancellationToken cancellationToken);
    Task<ProviderPage> SearchPageAsync(JsonElement query, string? nextPage, CancellationToken cancellationToken);
}

/// <summary>Owns its normalized payload; it never retains a disposed provider response document.</summary>
public sealed record ProviderOverlay(
    byte[] Payload,
    GatewayInputMessage Input,
    DateTimeOffset? CreatedTime,
    bool? IsRealtime,
    string? SortieId);

/// <summary>An invalid record remains in the page so it still counts against scanning limits.</summary>
public sealed record ProviderOverlayResult(ProviderOverlay? Overlay, string? Error);

public sealed record ProviderPage(IReadOnlyList<ProviderOverlayResult> Items, string? NextPage);

public sealed class ElasticProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);
