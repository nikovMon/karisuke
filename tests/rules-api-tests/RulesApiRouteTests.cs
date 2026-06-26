using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Responses;
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
    public async Task GetAllReturnsEmptyArrayWhenNoRulesExist()
    {
        using var context = CreateContext();

        var rules = await context.Client.GetFromJsonAsync<List<RuleConfigDto>>("/rules", JsonOptions);

        Assert.Empty(rules ?? []);
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
    public async Task GetAllReturnsBadRequestForInvalidBooleanQueryValues()
    {
        using var context = CreateContext();

        var invalidIsActive = await context.Client.GetAsync("/rules?isActive=maybe");
        var invalidNameOnly = await context.Client.GetAsync("/rules?getNameOnly=maybe");

        Assert.Equal(HttpStatusCode.BadRequest, invalidIsActive.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidNameOnly.StatusCode);
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
    public async Task GetByIdIsCaseSensitive()
    {
        using var context = CreateContext(ValidRule("Rule-1", "one"));

        var exact = await context.Client.GetAsync("/rules/Rule-1");
        var differentCase = await context.Client.GetAsync("/rules/rule-1");

        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, differentCase.StatusCode);
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
    public async Task GetByNameIsCaseSensitive()
    {
        using var context = CreateContext(ValidRule("rule-1", "CameraRule"));

        var exact = await context.Client.GetAsync("/rules/name/CameraRule");
        var differentCase = await context.Client.GetAsync("/rules/name/camerarule");

        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, differentCase.StatusCode);
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
    public async Task CreateAcceptsGeoJsonOnlyRule()
    {
        using var context = CreateContext();
        var rule = ValidRule("rule-1", "geo-json-only");
        rule.Wkt = null;
        rule.GeoJson = JsonDocument.Parse("{\"type\":\"Point\",\"coordinates\":[1,1]}").RootElement.Clone();

        var response = await context.Client.PostAsJsonAsync("/rules", rule, JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await context.Repository.GetByIdAsync("rule-1");
        Assert.Null(stored?.Wkt);
        Assert.Equal(JsonValueKind.Object, stored?.GeoJson?.ValueKind);
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
    public async Task CreateReturnsBadRequestForMalformedJsonOrInvalidEnum()
    {
        using var context = CreateContext();

        var malformed = await context.Client.PostAsync("/rules", Json("{\"_id\":\"rule-1\""));
        var invalidEnum = await context.Client.PostAsync(
            "/rules",
            Json("""
                {
                  "_id": "rule-1",
                  "ruleName": "one",
                  "algorithmName": "Unknown",
                  "isActive": true,
                  "minResolution": 0.5,
                  "maxResolution": 1,
                  "area": "area",
                  "wkt": "POINT (1 1)"
                }
                """));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidEnum.StatusCode);
    }

    [Fact]
    public async Task CreateReturnsBadRequestForMissingBody()
    {
        using var context = CreateContext();

        var response = await context.Client.PostAsync("/rules", Json("null"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchOneUpdatesOnlySentFieldsAndAllowsExplicitNull()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Description = "old";
        rule.MaxLookBackDay = 5;
        using var context = CreateContext(rule);
        var body = Json("{\"description\":null,\"isActive\":false,\"minResolution\":0.8}");

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
    public async Task PatchOneReplacesSensorsAndNormalizesValues()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = ["cam-1"];
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsync(
            "/rules/rule-1",
            Json("""
                {
                  "sensors": {
                    "thermal": ["th-1", "th-1", ""],
                    "": ["ignored"]
                  }
                }
                """));
        var updated = await response.Content.ReadFromJsonAsync<RuleConfigDto>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(updated?.Sensors.ContainsKey("camera"));
        Assert.Equal(["th-1"], updated?.Sensors["thermal"]);
        Assert.False(updated?.Sensors.ContainsKey(""));
    }

    [Fact]
    public async Task PatchOneCanClearMaxLookBackDayAndOptionalGeometry()
    {
        var rule = ValidRule("rule-1", "one");
        rule.MaxLookBackDay = 7;
        rule.GeoJson = JsonDocument.Parse("{\"type\":\"Point\",\"coordinates\":[1,1]}").RootElement.Clone();
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsync(
            "/rules/rule-1",
            Json("{\"maxLookBackDay\":null,\"wkt\":null}"));
        var updated = await response.Content.ReadFromJsonAsync<RuleConfigDto>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(updated?.MaxLookBackDay);
        Assert.Null(updated?.Wkt);
        Assert.Equal(JsonValueKind.Object, updated?.GeoJson?.ValueKind);
    }

    [Fact]
    public async Task PatchOneReturnsConflictWhenRuleNameBelongsToAnotherRule()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"), ValidRule("rule-2", "two"));

        var response = await context.Client.PatchAsync("/rules/rule-1", Json("{\"ruleName\":\"two\"}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PatchOneRejectsInvalidFieldValuesBeforeLookup()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var blankName = await context.Client.PatchAsync("/rules/rule-1", Json("{\"ruleName\":\" \"}"));
        var invalidResolution = await context.Client.PatchAsync("/rules/rule-1", Json("{\"minResolution\":0}"));
        var nullAlgorithm = await context.Client.PatchAsync("/rules/rule-1", Json("{\"algorithmName\":null}"));
        var unknownOnly = await context.Client.PatchAsync("/rules/rule-1", Json("{\"unknown\":\"value\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, blankName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResolution.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nullAlgorithm.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownOnly.StatusCode);
    }

    [Fact]
    public async Task PatchOneReturnsNotFoundAndBadRequest()
    {
        using var context = CreateContext();

        var missing = await context.Client.PatchAsync("/rules/missing", Json("{\"isActive\":true}"));
        var bad = await context.Client.PatchAsync("/rules/missing", Json("{}"));
        var malformed = await context.Client.PatchAsync("/rules/missing", Json("{\"isActive\":"));
        var nullBody = await context.Client.PatchAsync("/rules/missing", Json("null"));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);
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
    public async Task PatchBulkAllowsSingleRuleRename()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var response = await context.Client.PatchAsync(
            "/rules/bulk?ids=rule-1",
            Json("{\"ruleName\":\"renamed\"}"));
        var result = await response.Content.ReadFromJsonAsync<BulkOperationResult>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["rule-1"], result?.SuccessIds);
        Assert.Equal("renamed", (await context.Repository.GetByIdAsync("rule-1"))?.RuleName);
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
    public async Task PatchBulkParsesCommaSeparatedIdsWithWhitespaceAndEmptySegments()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"), ValidRule("rule-2", "two"));

        var response = await context.Client.PatchAsync(
            "/rules/bulk?ids=%20rule-1%20,,%20rule-2%20,",
            Json("{\"isActive\":false}"));
        var result = await response.Content.ReadFromJsonAsync<BulkOperationResult>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["rule-1", "rule-2"], result?.SuccessIds);
        Assert.False((await context.Repository.GetByIdAsync("rule-1"))?.IsActive);
        Assert.False((await context.Repository.GetByIdAsync("rule-2"))?.IsActive);
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
    public async Task ActivityRouteReturnsNotFoundForMissingRule()
    {
        using var context = CreateContext();

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/missing/activity",
            new { isActive = true },
            JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ActivityRouteReturnsBadRequestForMalformedJsonOrMissingBody()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var malformed = await context.Client.PatchAsync(
            "/rules/rule-1/activity",
            Json("{\"isActive\":"));
        var nullBody = await context.Client.PatchAsync(
            "/rules/rule-1/activity",
            Json("null"));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);
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
    public async Task AddSensorsRouteCreatesNewSensorKey()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/add?ids=rule-1",
            new { sensorName = "thermal", values = new[] { "th-1", "th-2" } },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["th-1", "th-2"], (await context.Repository.GetByIdAsync("rule-1"))?.Sensors["thermal"]);
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
    public async Task RemoveSensorsRouteLeavesRuleUnchangedWhenSensorIsMissing()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = ["cam-1"];
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/remove?ids=rule-1",
            new { sensorName = "thermal", values = new[] { "th-1" } },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["cam-1"], (await context.Repository.GetByIdAsync("rule-1"))?.Sensors["camera"]);
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
    public async Task SensorRoutesReturnBadRequestForMalformedJsonAndMissingBody()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var malformed = await context.Client.PatchAsync(
            "/rules/sensors/add?ids=rule-1",
            Json("{\"sensorName\":\"camera\""));
        var nullBody = await context.Client.PatchAsync(
            "/rules/sensors/remove?ids=rule-1",
            Json("null"));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);
    }

    [Fact]
    public async Task UnsupportedMethodsReturnMethodNotAllowedForExistingRoutes()
    {
        using var context = CreateContext();

        var postToId = await context.Client.PostAsJsonAsync("/rules/rule-1", ValidRule("rule-1", "one"), JsonOptions);
        var getToSensorAdd = await context.Client.GetAsync("/rules/sensors/add?ids=rule-1");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, postToId.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getToSensorAdd.StatusCode);
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

    [Fact]
    public async Task RepositoryFailuresOnWriteRoutesReturnServiceUnavailable()
    {
        using var factory = new RulesApiFactory(new ThrowingRuleRepository());
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/rules", ValidRule("rule-1", "one"), JsonOptions);
        var patch = await client.PatchAsync("/rules/rule-1", Json("{\"description\":\"new\"}"));
        var bulk = await client.PatchAsync("/rules/bulk?ids=rule-1", Json("{\"description\":\"new\"}"));
        var activity = await client.PatchAsJsonAsync("/rules/rule-1/activity", new { isActive = true }, JsonOptions);
        var addSensor = await client.PatchAsJsonAsync(
            "/rules/sensors/add?ids=rule-1",
            new { sensorName = "camera", values = new[] { "cam-1" } },
            JsonOptions);
        var removeSensor = await client.PatchAsJsonAsync(
            "/rules/sensors/remove?ids=rule-1",
            new { sensorName = "camera", values = new[] { "cam-1" } },
            JsonOptions);
        var delete = await client.DeleteAsync("/rules/rule-1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, patch.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, bulk.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, activity.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, addSensor.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, removeSensor.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, delete.StatusCode);
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
