using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;

namespace ImagingPipeline.UnifiedGateway.Processing;

/// <summary>
/// Validates and builds work for an already matched pipeline. Rule evaluation and network delivery
/// are separate stages; this service cannot publish or send a request.
/// </summary>
public sealed class PipelineWorkPreparer(
    IPipelineCatalog catalog,
    IPipelineContractRegistry contracts,
    ILogger<PipelineWorkPreparer> logger)
{
    private static readonly Counter<long> Preparations = TelemetryMeters.UnifiedGateway.CreateCounter<long>(
        "unified_gateway.contract.preparations", description: "Contract preparation outcomes by configured pipeline.");
    private static readonly Histogram<double> Duration = TelemetryMeters.UnifiedGateway.CreateHistogram<double>(
        "unified_gateway.contract.preparation.duration", "s", "Time spent validating and building pipeline work.");

    public PipelinePreparationResult Prepare(
        string pipelineId,
        PipelineDispatchContext context,
        JsonElement runParams)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pipeline = catalog.GetRequired(pipelineId);
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        using var activity = TelemetrySources.UnifiedGateway.StartActivity("unified_gateway.contract.prepare");
        activity?.SetTag("pipeline.id", pipeline.PipelineId);
        activity?.SetTag("pipeline.contract", pipeline.ContractId);

        try
        {
            if (!pipeline.Enabled)
            {
                outcome = "disabled";
                logger.LogDebug("Work preparation suppressed for disabled pipeline {PipelineId}.", pipeline.PipelineId);
                return new(PipelinePreparationStatus.Disabled, null, []);
            }

            var contract = contracts.GetRequired(pipeline.ContractId);
            var errors = contract.ValidateRunParams(runParams);
            if (errors.Count > 0)
            {
                outcome = "invalid";
                activity?.SetStatus(ActivityStatusCode.Error, "Invalid pipeline run parameters.");
                logger.LogWarning(
                    "Run parameters rejected for pipeline {PipelineId}, contract {ContractId}: {ErrorCount} validation errors.",
                    pipeline.PipelineId, pipeline.ContractId, errors.Count);
                return new(PipelinePreparationStatus.Invalid, null, errors);
            }

            var payload = contract.BuildPayload(context, runParams, pipeline.ExtraData.Value);
            outcome = "prepared";
            return new(PipelinePreparationStatus.Prepared, new(pipeline, payload), []);
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Pipeline work preparation failed.");
            logger.LogError(
                "Work preparation failed for pipeline {PipelineId}, contract {ContractId}.",
                pipeline.PipelineId, pipeline.ContractId);
            throw;
        }
        finally
        {
            activity?.SetTag("pipeline.outcome", outcome);
            var tags = new TagList { { "pipeline.id", pipeline.PipelineId }, { "outcome", outcome } };
            Preparations.Add(1, tags);
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}
