using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.RuleEngine.Loading;

public interface IRuleRepository
{
    /// <summary>Reads every active rule in the index. Throws <see cref="RuleLoadException"/> when the read fails.</summary>
    Task<RuleLoadResult> GetActiveRulesAsync(string indexName, CancellationToken cancellationToken);
}

/// <summary>
/// The documents read from an index. <see cref="RejectedSources"/> holds documents that could not
/// even be read as rules; they are reported, not fatal.
/// </summary>
public sealed record RuleLoadResult(IReadOnlyList<PipelineRuleDocument> Rules, IReadOnlyList<RuleRejection> RejectedSources);

/// <summary>A rule left out of a snapshot, and why.</summary>
public sealed record RuleRejection(string RuleId, string Reason, Exception? Exception = null);

/// <summary>The rules could not be read, for example because Elasticsearch is unavailable. Retryable.</summary>
public sealed class RuleLoadException(string message, Exception innerException) : Exception(message, innerException);
