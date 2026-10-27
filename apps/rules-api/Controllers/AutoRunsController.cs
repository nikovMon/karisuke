using ImagingPipeline.Rules.Api.AutoRuns;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Controllers;

[ApiController]
[Route("auto-runs")]
[Produces("application/json")]
public sealed class AutoRunsController(
    AutoRunService service,
    IOptions<AutoRunOptions> configured,
    IAutoRunMemoryGuard memory,
    IOptions<AutoRuns.Provider.ElasticProviderOptions> providerOptions,
    AutoRuns.Provider.ProviderRequestCoordinator requests) : ControllerBase
{
    [HttpGet("options")]
    public IActionResult Options()
    {
        var options = configured.Value;
        var execution = service.Execution();
        var provider = requests.Snapshot();
        return Ok(new
        {
            options.Enabled,
            Defaults = new
            {
                options.Publication, MinOverlays = 0,
                FilterMaxOverlays = Math.Min(AutoRunOptions.DefaultFilterMaxOverlays, options.MaxSelectedOverlays)
            },
            Limits = new
            {
                BatchSize = new { Min = 1, Max = 100 },
                DelayMs = new { Min = 0, Max = 60_000 },
                options.MaxSelectedOverlays, options.MaxScannedRecords, options.MaxCandidates,
                options.PayloadBudgetBytes, options.ReadyLifetimeMinutes, options.SummaryLifetimeMinutes,
                MaxTargets = 100
            },
            SingleInstance = true,
            Execution = new
            {
                execution.MaxConcurrentPreparations,
                execution.MaxConcurrentPublications,
                execution.MaxQueuedJobs,
                execution.ActivePreparations,
                execution.ActivePublications,
                execution.QueuedPreparations,
                execution.QueuedPublications,
                execution.PublisherConcurrency,
                execution.ActivePublicationPermits,
                execution.WaitingPublicationPermits,
                execution.PreparationReservationBytes,
                execution.ReservedPreparationBytes,
                execution.RetainedBytes,
                ProviderMaxConcurrentRequests = provider.MaxConcurrentRequests,
                ProviderActiveRequests = provider.ActiveRequests, ProviderWaitingRequests = provider.WaitingRequests,
                ProviderCooldownUntil = provider.CooldownUntil
            },
            Ui = options.Ui,
            Memory = memory.Snapshot(),
            Provider = new
            {
                providerOptions.Value.MaxResponseBytes, providerOptions.Value.TimeoutSeconds, providerOptions.Value.To,
                providerOptions.Value.RequestDelayMs, providerOptions.Value.MaxRetries, providerOptions.Value.RetryDelayMs,
                providerOptions.Value.SensorTypeMappings, providerOptions.Value.MaxConcurrentRequests
            }
        });
    }

    [HttpPost("preview")]
    [ProducesResponseType(typeof(AutoRunSummary), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Preview(AutoRunPreviewRequest request, CancellationToken cancellationToken)
    {
        var job = await service.PreviewAsync(request, cancellationToken);
        return AcceptedAtAction(nameof(Get), new { id = job.Id }, job);
    }

    [HttpGet]
    public IActionResult List([FromQuery] bool summaryOnly = false) =>
        Ok(summaryOnly ? (object)service.ListSummaries() : service.List());

    [HttpGet("{id}")]
    public AutoRunSummary Get(string id) => service.Get(id);

    [HttpGet("{id}/items")]
    public AutoRunItemsPage Items(string id, [FromQuery] int from = 0, [FromQuery] int size = 50,
        [FromQuery] AutoRunItemOutcome? outcome = null) => service.Items(id, from, size, outcome);

    [HttpPost("{id}/start")]
    [ProducesResponseType(typeof(AutoRunSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AutoRunSummary), StatusCodes.Status202Accepted)]
    public IActionResult Start(string id)
    {
        var job = service.Start(id);
        return StatusCode(job.State == AutoRunState.QueuedPublication ? StatusCodes.Status202Accepted : StatusCodes.Status200OK, job);
    }

    [HttpPost("{id}/rerun")]
    [ProducesResponseType(typeof(AutoRunSummary), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Rerun(string id, CancellationToken cancellationToken)
    {
        var job = await service.RerunAsync(id, cancellationToken);
        return AcceptedAtAction(nameof(Get), new { id = job.Id }, job);
    }

    [HttpPatch("{id}/publication")]
    public AutoRunSummary Publication(string id, AutoRunPublication settings) => service.UpdatePublication(id, settings);

    [HttpPost("{id}/cancel")]
    public AutoRunSummary Cancel(string id) => service.Cancel(id);

    [HttpDelete("{id}")]
    [ProducesResponseType(typeof(AutoRunDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AutoRunDeletionResult), StatusCodes.Status202Accepted)]
    public IActionResult Delete(string id) => DeletionResponse(service.Delete(id));

    [HttpDelete]
    [ProducesResponseType(typeof(AutoRunDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AutoRunDeletionResult), StatusCodes.Status202Accepted)]
    public IActionResult DeleteAll() => DeletionResponse(service.DeleteAll());

    private IActionResult DeletionResponse(AutoRunDeletionResult result) =>
        StatusCode(result.PendingIds.Count > 0 ? StatusCodes.Status202Accepted : StatusCodes.Status200OK, result);
}
