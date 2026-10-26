namespace ImagingPipeline.Rules.Api.AutoRuns;

/// <summary>Bounded, sanitized context shared by the run UI and operational logs.</summary>
public sealed record AutoRunDiagnostic(
    DateTimeOffset Timestamp,
    string Severity,
    string Code,
    string Stage,
    string Message,
    string? TraceId = null,
    ProviderFailureDetails? Upstream = null,
    int? QueryIndex = null,
    int? Page = null,
    string? OverlayId = null,
    string? PipelineId = null,
    string? RuleId = null);

public sealed record ProviderFailureDetails(
    string Operation,
    string Method,
    int? StatusCode,
    int Attempts,
    double ElapsedMs,
    string? ContentType = null,
    string? BodyExcerpt = null,
    bool BodyTruncated = false,
    string? BodyUnavailableReason = null,
    long BytesRead = 0);

/// <summary>Latest operation only; polling never accumulates an unbounded event history.</summary>
public sealed record ProviderProgress(
    string Phase,
    string Operation,
    int Attempt,
    int MaxAttempts,
    int? LastStatusCode = null,
    DateTimeOffset? NextAttemptAt = null,
    int? QueryIndex = null,
    int? QueryCount = null,
    int? Page = null);
