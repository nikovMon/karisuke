using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.Observability;
using ImagingPipeline.Rules.Api.Tests.Fakes;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RulesApiHostTests
{
    [Fact]
    public async Task HealthRouteReturnsHealthy()
    {
        using var factory = new RulesApiFactory(new InMemoryIndexStore());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var apiResponse = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, apiResponse.StatusCode);
        Assert.Contains("Healthy", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthRouteReturnsServiceUnavailableWhenElasticsearchIsUnhealthy()
    {
        using var factory = new RulesApiFactory(new InMemoryIndexStore(), isHealthy: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Unhealthy", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwaggerDescribesTheCreateRequestWithoutServerOwnedFields()
    {
        using var factory = new RulesApiFactory(new InMemoryIndexStore());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");
        using var swagger = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var createSchemaReference = swagger.RootElement
            .GetProperty("paths")
            .GetProperty("/pipelines/{pipelineId}/rules")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        Assert.EndsWith("/CreatePipelineRuleRequest", createSchemaReference, StringComparison.Ordinal);
        var createProperties = swagger.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("CreatePipelineRuleRequest")
            .GetProperty("properties");
        Assert.False(createProperties.TryGetProperty("_id", out _));
        Assert.False(createProperties.TryGetProperty("creationTime", out _));
        Assert.False(createProperties.TryGetProperty("updateTime", out _));
    }

    [Fact]
    public async Task ModelValidationFailureLogsTheMatchedRouteTemplate()
    {
        using var loggerProvider = new RecordingLoggerProvider();
        using var factory = new RulesApiFactory(new InMemoryIndexStore(), loggerProvider: loggerProvider);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/pipelines/asd/rules?isActive=not-a-boolean");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = Assert.Single(loggerProvider.Entries, log => log.EventId.Id == 1001);
        Assert.Equal("Request model validation failed.", entry.Message);
        Assert.Equal("/pipelines/{pipelineId}/rules", entry.Properties[TelemetryAttributeNames.HttpRoute]);
        Assert.Equal("GET", entry.Properties[TelemetryAttributeNames.HttpRequestMethod]);
        Assert.Equal(["isActive"], (string[])entry.Properties[TelemetryAttributeNames.RequestInvalidFieldNames]!);
        Assert.Equal(1, entry.Properties[TelemetryAttributeNames.RequestInvalidFieldCount]);
    }

    [Fact]
    public async Task ElasticsearchFailureReturnsServiceUnavailableAndLogsTheRouteWithoutValues()
    {
        const string sensitiveRuleName = "do-not-log-this-rule-name";
        using var loggerProvider = new RecordingLoggerProvider();
        using var factory = new RulesApiFactory(new ThrowingRuleRepository(), loggerProvider: loggerProvider);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/pipelines/asd/rules/name/{sensitiveRuleName}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Elasticsearch dependency is unavailable.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive Elasticsearch failure details.", body, StringComparison.Ordinal);
        var entry = Assert.Single(loggerProvider.Entries, log => log.EventId.Id == 1002);
        Assert.Equal("Elasticsearch persistence operation failed.", entry.Message);
        Assert.Equal("/pipelines/{pipelineId}/rules/name/{ruleName}", entry.Properties[TelemetryAttributeNames.HttpRoute]);
    }

    [Fact]
    public async Task ElasticsearchFailuresOnWriteRoutesReturnServiceUnavailable()
    {
        using var factory = new RulesApiFactory(new ThrowingRuleRepository());
        using var client = factory.CreateClient();
        var sensor = new { sensorName = "camera", registrationQualities = new[] { "Accurate" } };

        HttpResponseMessage[] responses =
        [
            await client.PatchAsJsonAsync("/pipelines/asd/rules/rule-1", new { description = "new" }),
            await client.PatchAsJsonAsync("/pipelines/asd/rules/rule-1/activity", new { isActive = true }),
            await client.DeleteAsync("/pipelines/asd/rules/rule-1")
        ];

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode));
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await client.PatchAsJsonAsync("/pipelines/asd/rules/sensors/add?ids=rule-1", sensor)).StatusCode);
    }
}
