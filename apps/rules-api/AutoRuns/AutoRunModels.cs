using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Rules.Api.AutoRuns;

public enum AutoRunSourceMode { Rules, Filters, OverlayIds }
public enum AutoRunState { Preparing, Ready, Publishing, Completed, CompletedWithErrors, Failed, Cancelled, Expired }
public enum AutoRunItemOutcome { Pending, InFlight, Confirmed, Failed, Uncertain }

public sealed record AutoRunPublication
{
    [Range(1, 100)] public int BatchSize { get; init; } = 10;
    [Range(0, 60_000)] public int DelayMs { get; init; } = 1000;
}

public sealed record AutoRunTimeRange
{
    public string? From { get; init; }
    public string? To { get; init; }
}

public sealed record AutoRunResolutionRange
{
    public double? Min { get; init; }
    public double? Max { get; init; }
}

public sealed record AutoRunFilters
{
    public string[]? SensorNames { get; init; }
    public string[]? SensorTypes { get; init; }
    public string[]? GridTypes { get; init; }
    public string[]? RegistrationQualities { get; init; }
    public string[]? SortieIds { get; init; }
    public bool? IsRealtime { get; init; }
    public AutoRunTimeRange? CreatedTime { get; init; }
    public AutoRunTimeRange? PhotoTime { get; init; }
    public AutoRunResolutionRange? Resolution { get; init; }
    public string? Wkt { get; init; }
}

public sealed record AutoRunSource
{
    public AutoRunSourceMode Mode { get; init; }
    public AutoRunFilters? Filters { get; init; }
    public string[]? OverlayIds { get; init; }
}

public sealed record AutoRunTarget
{
    [Required] public string PipelineId { get; init; } = "";
    [Required] public string RuleId { get; init; } = "";
    [Range(0, 100_000)] public int MinOverlays { get; init; }
    [Required, Range(1, 100_000)] public int? MaxOverlays { get; init; }
}

public sealed record AutoRunPreviewRequest
{
    [Required] public AutoRunSource Source { get; init; } = null!;
    [Required, MaxLength(100)] public List<AutoRunTarget> Targets { get; init; } = [];
    [Range(0, 100_000)] public int MinOverlays { get; init; }
    [Range(1, 100_000)] public int? MaxOverlays { get; init; }
    public AutoRunPublication? Publication { get; init; }
}

public sealed record AutoRunCounters(
    int Scanned, int Invalid, int Duplicates, int ObservedMatches, int Selected,
    int Confirmed, int Failed, int Uncertain, int InFlight, int Remaining);

public sealed record AutoRunTargetSummary(
    string PipelineId, string RuleId, string RuleName, bool IsActive,
    int MinOverlays, int MaxOverlays, int Selected, int Confirmed, int Failed, int Uncertain,
    int Shortfall, PipelineRuleDocument Rule);

public sealed record AutoRunSummary(
    string Id, AutoRunState State, DateTimeOffset CreatedAt, DateTimeOffset? ReadyAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, DateTimeOffset? ExpiresAt,
    AutoRunSourceMode SourceMode, int MinOverlays, int MaxOverlays, int Shortfall,
    AutoRunCounters Counters, IReadOnlyList<AutoRunTargetSummary> Targets,
    AutoRunPublication Publication, AutoRunPublication ActivePublication, bool PublicationChangePending,
    bool SourceExhausted, string? StoppedReason, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors, IReadOnlyList<JsonElement> Queries, double ConfirmedPerSecond,
    AutoRunMemorySnapshot? Memory = null, long RetainedBytes = 0, bool DeletionRequested = false)
{
    public IReadOnlyList<AutoRunDiagnostic> Diagnostics { get; init; } = [];
    public int DiagnosticsOmitted { get; init; }
    public ProviderProgress? ProviderProgress { get; init; }
}

public sealed record AutoRunDeletionResult(
    IReadOnlyList<string> DeletedIds, IReadOnlyList<string> PendingIds, long ReleasedRetainedBytes);

public sealed record AutoRunItemTarget(string PipelineId, string RuleId);
public sealed record AutoRunItem(
    string OverlayId, string SensorName, DateTimeOffset PhotoTime,
    IReadOnlyList<AutoRunItemTarget> Targets, AutoRunItemOutcome Outcome, string? Error)
{
    public string? SensorType { get; init; }
}
public sealed record AutoRunItemsPage(int From, int Size, int Total, IReadOnlyList<AutoRunItem> Items);

public static class AutoRunJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new RegistrationQualityJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class AutoRunRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; init; } = "auto_run_request_rejected";
}
