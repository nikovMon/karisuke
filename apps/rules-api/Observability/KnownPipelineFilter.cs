using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ImagingPipeline.Rules.Api.Observability;

/// <summary>
/// Answers 404 for any route whose <c>pipelineId</c> is not in the catalog, before the action runs.
/// </summary>
public sealed class KnownPipelineFilter : IActionFilter
{
    public const string RouteKey = "pipelineId";

    private readonly IPipelineCatalog _catalog;
    private readonly ILogger<KnownPipelineFilter> _logger;

    public KnownPipelineFilter(IPipelineCatalog catalog, ILogger<KnownPipelineFilter> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.RouteData.Values[RouteKey] is not string pipelineId ||
            _catalog.GetAll().Any(pipeline => pipeline.PipelineId == pipelineId))
        {
            return;
        }

        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.PipelineId, pipelineId)
               }))
        {
            _logger.PipelineNotFound();
        }

        context.Result = new NotFoundResult();
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
