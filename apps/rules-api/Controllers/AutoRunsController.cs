using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Controllers;

[ApiController]
[Route("auto-runs")]
[Produces("application/json")]
public sealed class AutoRunsController(
    AutoRunService service,
    IOptions<AutoRunOptions> configured,
    IOptions<ElasticProviderOptions> provider) : ControllerBase
{
    [HttpGet("options")]
    public IActionResult Options()
    {
        var options = configured.Value;
        return Ok(new
        {
            options.Enabled,
            Defaults = new { options.Publication, MinOverlays = 0 },
            Limits = new
            {
                BatchSize = new { Min = 1, Max = 100 },
                DelayMs = new { Min = 0, Max = 60_000 },
                options.MaxSelectedOverlays, options.MaxScannedRecords, options.MaxCandidates,
                options.PayloadBudgetBytes, options.ReadyLifetimeMinutes, options.SummaryLifetimeMinutes,
                MaxTargets = 100
            },
            ProviderTimeZone = provider.Value.SourceTimeZoneId,
            SingleInstance = true
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
    public IReadOnlyList<AutoRunSummary> List() => service.List();

    [HttpGet("{id}")]
    public AutoRunSummary Get(string id) => service.Get(id);

    [HttpGet("{id}/items")]
    public AutoRunItemsPage Items(string id, [FromQuery] int from = 0, [FromQuery] int size = 50,
        [FromQuery] AutoRunItemOutcome? outcome = null) => service.Items(id, from, size, outcome);

    [HttpPost("{id}/start")]
    public AutoRunSummary Start(string id) => service.Start(id);

    [HttpPatch("{id}/publication")]
    public AutoRunSummary Publication(string id, AutoRunPublication settings) => service.UpdatePublication(id, settings);

    [HttpPost("{id}/cancel")]
    public AutoRunSummary Cancel(string id) => service.Cancel(id);
}
