using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Rules.Contracts.Models;
using ImagingPipeline.Rules.Contracts.Responses;
using ImagingPipeline.Rules.Api.Tests.Fakes;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RulesApiRouteTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task HealthRouteReturnsHealthy()
    {
        using var context = CreateContext();

        var response = await context.Client.GetAsync("/health");
        var apiResponse = await context.Client.GetAsync("/api/health");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, apiResponse.StatusCode);
        Assert.Contains("Healthy", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwaggerRouteIsAvailable()
    {
        using var context = CreateContext();

        var response = await context.Client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAllReturnsFullRulesAndFiltersByActive()
    {
        var active = ValidRule("rule-1", "active", isActive: true);
        var inactive = ValidRule("rule-2", "inactive", isActive: false);
        using var context = CreateContext(active, inactive);

        var rules = await context.Client.GetFromJsonAsync<List<RuleConfigDto>>(
            "/rules?isActive=true",
            JsonOptions);

        var rule = Assert.Single(rules ?? []);
        Assert.Equal("rule-1", rule.Id);
    }

    [Fact]
    public async Task GetAllNameOnlyReturnsOnlyNames()
    {
        using var context = CreateContext(
            ValidRule("rule-1", "one", isActive: true),
            ValidRule("rule-2", "two", isActive: false));

        var names = await context.Client.GetFromJsonAsync<List<string>>(
            "/rules?getNameOnly=true&isActive=true",
            JsonOptions);

        Assert.Equal(["one"], names);
    }

    [Fact]
    public async Task GetByIdReturnsRuleOrNotFound()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var ok = await context.Client.GetAsync("/rules/rule-1");
        var missing = await context.Client.GetAsync("/rules/missing");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task GetByNameReturnsExactRuleOrNotFound()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var ok = await context.Client.GetAsync("/rules/name/one");
        var missing = await context.Client.GetAsync("/rules/name/missing");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CreateReturnsCreatedAndStoresRule()
    {
        using var context = CreateContext();

        var response = await context.Client.PostAsJsonAsync("/rules", ValidRule("rule-1", "one"), JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.Location?.ToString().Contains("/rules/rule-1", StringComparison.Ordinal));
        Assert.NotNull(await context.Repository.GetByIdAsync("rule-1"));
    }

    [Fact]
    public async Task CreateReturnsConflictForDuplicateRuleName()
    {
        using var context = CreateContext(ValidRule("rule-1", "same"));

        var response = await context.Client.PostAsJsonAsync("/rules", ValidRule("rule-2", "same"), JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateReturnsBadRequestForInvalidRule()
    {
        using var context = CreateContext();

        var response = await context.Client.PostAsJsonAsync("/rules", new RuleConfigDto(), JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchOneUpdatesOnlySentFieldsAndAllowsExplicitNull()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Description = "old";
        rule.MaxLookBackDay = 5;
        using var context = CreateContext(rule);
        var body = Json("{\"description\":null,\"isActive\":false,\"minResulution\":0.8}");

        var response = await context.Client.PatchAsync("/rules/rule-1", body);
        var updated = await response.Content.ReadFromJsonAsync<RuleConfigDto>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(updated?.Description);
        Assert.False(updated?.IsActive);
        Assert.Equal("one", updated?.RuleName);
        Assert.Equal(5, updated?.MaxLookBackDay);
        Assert.Equal(0.8, updated?.MinResolution);
    }

    [Fact]
    public async Task PatchOneReturnsConflictWhenRuleNameBelongsToAnotherRule()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"), ValidRule("rule-2", "two"));

        var response = await context.Client.PatchAsync("/rules/rule-1", Json("{\"ruleName\":\"two\"}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PatchOneReturnsNotFoundAndBadRequest()
    {
        using var context = CreateContext();

        var missing = await context.Client.PatchAsync("/rules/missing", Json("{\"isActive\":true}"));
        var bad = await context.Client.PatchAsync("/rules/missing", Json("{}"));
        var malformed = await context.Client.PatchAsync("/rules/missing", Json("{\"isActive\":"));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task PatchBulkUpdatesExistingRulesAndReportsMissingIds()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"), ValidRule("rule-2", "two"));

        var response = await context.Client.PatchAsync(
            "/rules/bulk?ids=rule-1,missing,rule-2",
            Json("{\"isActive\":false,\"maxLookBackDay\":7}"));
        var result = await response.Content.ReadFromJsonAsync<BulkOperationResult>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["rule-1", "rule-2"], result?.SuccessIds);
        var failure = Assert.Single(result?.FailedIds ?? []);
        Assert.Equal("missing", failure.Id);
        Assert.False((await context.Repository.GetByIdAsync("rule-1"))?.IsActive);
        Assert.Equal(7, (await context.Repository.GetByIdAsync("rule-2"))?.MaxLookBackDay);
    }

    [Fact]
    public async Task PatchBulkReturnsBadRequestForEmptyIdsAndConflictForSharedRuleName()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"), ValidRule("rule-2", "two"));

        var bad = await context.Client.PatchAsync("/rules/bulk", Json("{\"isActive\":false}"));
        var conflict = await context.Client.PatchAsync(
            "/rules/bulk?ids=rule-1,rule-2",
            Json("{\"ruleName\":\"same\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task DeleteReturnsNoContentOrNotFound()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var deleted = await context.Client.DeleteAsync("/rules/rule-1");
        var missing = await context.Client.DeleteAsync("/rules/rule-1");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ActivityRouteChangesOnlyIsActive()
    {
        using var context = CreateContext(ValidRule("rule-1", "one", isActive: false));

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/rule-1/activity",
            new { isActive = true },
            JsonOptions);
        var updated = await response.Content.ReadFromJsonAsync<RuleConfigDto>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(updated?.IsActive);
        Assert.Equal("one", updated?.RuleName);
    }

    [Fact]
    public async Task AddSensorsRouteAddsUniqueValuesAndReportsMissingIds()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = ["cam-1"];
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/add?ids=rule-1,missing",
            new { sensorName = "camera", values = new[] { "cam-1", "cam-2" } },
            JsonOptions);
        var result = await response.Content.ReadFromJsonAsync<BulkOperationResult>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["rule-1"], result?.SuccessIds);
        Assert.Equal(["cam-1", "cam-2"], (await context.Repository.GetByIdAsync("rule-1"))?.Sensors["camera"]);
        Assert.Single(result?.FailedIds ?? []);
    }

    [Fact]
    public async Task RemoveSensorsRouteRemovesValuesAndDeletesEmptyKey()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = ["cam-1"];
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/remove?ids=rule-1",
            new { sensorName = "camera", values = new[] { "cam-1" } },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await context.Repository.GetByIdAsync("rule-1"))?.Sensors.ContainsKey("camera"));
    }

    [Fact]
    public async Task SensorRoutesReturnBadRequestForInvalidSensorPayloadOrEmptyIds()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var invalidPayload = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/add?ids=rule-1",
            new { sensorName = "", values = new[] { "cam-1", "cam-1" } },
            JsonOptions);
        var emptyIds = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/remove",
            new { sensorName = "camera", values = new[] { "cam-1" } },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, invalidPayload.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, emptyIds.StatusCode);
    }

    [Fact]
    public async Task RepositoryFailureReturnsServiceUnavailableInsteadOfUnexpectedServerError()
    {
        using var factory = new RulesApiFactory(new ThrowingRuleRepository());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/rules");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Elasticsearch dependency is unavailable.", body, StringComparison.Ordinal);
    }

    private static TestContext CreateContext(params RuleConfigDto[] rules)
    {
        var repository = new InMemoryRuleRepository();
        foreach (var rule in rules)
        {
            repository.Add(rule);
        }

        var factory = new RulesApiFactory(repository);
        return new TestContext(repository, factory, factory.CreateClient());
    }

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static RuleConfigDto ValidRule(string id, string ruleName, bool isActive = true) =>
        new()
        {
            Id = id,
            RuleName = ruleName,
            AlgorithmName = AlgorithmName.Finder,
            IsActive = isActive,
            MinResolution = 0.5,
            MaxResolution = 1,
            Area = "area",
            Wkt = "POINT (1 1)",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ModifiedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

    private sealed record TestContext(
        InMemoryRuleRepository Repository,
        RulesApiFactory Factory,
        HttpClient Client) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }
}
