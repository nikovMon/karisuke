using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.Rules.Api.Observability;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class PipelinesApiTests
{
    [Fact]
    public async Task GetAllListsTheShippedCatalogWithoutTransportSettings()
    {
        using var factory = new RulesApiFactory(new InMemoryRuleRepository());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/pipelines");
        var json = await response.Content.ReadAsStringAsync();
        var pipelines = JsonSerializer.Deserialize<List<PipelineResponse>>(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [new PipelineResponse("algo", "algo", true), new PipelineResponse("asd", "asd", true)],
            pipelines);
        Assert.DoesNotContain("transport", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rulesIndex", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetByIdReturnsOnePipelineOrNotFound()
    {
        using var factory = new RulesApiFactory(new InMemoryRuleRepository());
        using var client = factory.CreateClient();

        var found = await client.GetFromJsonAsync<PipelineResponse>("/pipelines/asd");
        var missing = await client.GetAsync("/pipelines/unknown");

        Assert.Equal(new PipelineResponse("asd", "asd", true), found);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void UnknownPipelineLogsStaticWarningWithThePipelineIdAsAField()
    {
        var logger = new RecordingLogger<KnownPipelineFilter>();
        var filter = new KnownPipelineFilter(new SinglePipelineCatalog(), logger);
        var routeData = new RouteData { Values = { [KnownPipelineFilter.RouteKey] = "unknown" } };
        var context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext(), routeData, new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            controller: null!);

        filter.OnActionExecuting(context);

        Assert.IsType<NotFoundResult>(context.Result);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(5030, log.EventId.Id);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal("Requested pipeline is not configured.", log.Message);
        var scope = Assert.Single(logger.Scopes);
        Assert.Equal("unknown", scope[TelemetryAttributeNames.PipelineId]);
    }

    private sealed class SinglePipelineCatalog : IPipelineCatalog
    {
        private static readonly PipelineDefinition Pipeline = new() { PipelineId = "asd", ContractId = "asd", Enabled = true };

        public IReadOnlyList<PipelineDefinition> GetAll() => [Pipeline];
        public IReadOnlyList<PipelineDefinition> GetEnabled() => [Pipeline];
        public PipelineDefinition GetRequired(string pipelineId) => Pipeline;
    }
}
