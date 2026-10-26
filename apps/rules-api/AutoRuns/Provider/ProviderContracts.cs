using System.Text.Json;
using ImagingPipeline.RuleEngine.Input;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

public interface IElasticOverlayProvider
{
    Task<ProviderOverlayResult> GetAsync(string id, CancellationToken cancellationToken,
        Action<ProviderProgress>? report = null);
    Task<ProviderPage> SearchPageAsync(JsonElement query, string? nextPage, CancellationToken cancellationToken,
        Action<ProviderProgress>? report = null);
    Task<ProviderOverlayResult> ResolveSensorTypeAsync(ProviderOverlay overlay, CancellationToken cancellationToken,
        Action<ProviderProgress>? report = null);
    // Consume normalized records as they are mapped, so the coordinator can stop before
    // retaining another candidate. Implementations need not keep a second page-sized list.
    async Task<ProviderPage> VisitPageAsync(JsonElement query, string? nextPage,
        Func<ProviderOverlayResult, ValueTask<bool>> accept, CancellationToken cancellationToken,
        Action<ProviderProgress>? report = null)
    {
        var page = await SearchPageAsync(query, nextPage, cancellationToken, report);
        foreach (var item in page.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await accept(item)) return new([], null, page.StoppedReason);
        }
        return page with { Items = [], NextPage = page.Items.Count == 0 ? null : page.NextPage };
    }
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

public sealed record ProviderPage(IReadOnlyList<ProviderOverlayResult> Items, string? NextPage,
    string? StoppedReason = null);

public sealed class ProviderResponseLimitException(long maximumBytes)
    : Exception($"Provider response exceeds the configured {maximumBytes:N0} byte limit (AutoRun.Provider.MaxResponseBytes).");

public class ElasticProviderException(string message, Exception? innerException = null,
    AutoRunDiagnostic? diagnostic = null) : Exception(message, innerException)
{
    public AutoRunDiagnostic? Diagnostic { get; } = diagnostic;
}

public sealed class ProviderUnavailableException(string message, AutoRunDiagnostic diagnostic)
    : ElasticProviderException(message, diagnostic: diagnostic);
