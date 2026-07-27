using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using static ImagingPipeline.Common.Dtos.Rules.Models.RegistrationQuality;

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
    public async Task HealthRouteReturnsServiceUnavailableWhenElasticsearchIsUnhealthy()
    {
        using var factory = new RulesApiFactory(new InMemoryRuleRepository(), isHealthy: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Unhealthy", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwaggerRouteIsAvailable()
    {
        using var context = CreateContext();

        var response = await context.Client.GetAsync("/swagger/v1/swagger.json");
        using var swagger = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var createSchemaReference = swagger.RootElement
            .GetProperty("paths")
            .GetProperty("/rules")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        Assert.EndsWith("/CreateRuleRequest", createSchemaReference, StringComparison.Ordinal);

        var createProperties = swagger.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("CreateRuleRequest")
            .GetProperty("properties");
        Assert.False(createProperties.TryGetProperty("_id", out _));
        Assert.False(createProperties.TryGetProperty("creationTime", out _));
        Assert.False(createProperties.TryGetProperty("updateTime", out _));
    }

    [Fact]
    public async Task GetAllReturnsFullRulesAndFiltersByActive()
    {
        var active = ValidRule("rule-1", "active", isActive: true);
        var inactive = ValidRule("rule-2", "inactive", isActive: false);
        using var context = CreateContext(active, inactive);

        var rules = await context.Client.GetFromJsonAsync<List<RuleDto>>(
            "/rules?isActive=true",
            JsonOptions);

        var rule = Assert.Single(rules ?? []);
        Assert.Equal("rule-1", rule.Id);
    }

    [Fact]
    public async Task GetAllReturnsEmptyArrayWhenNoRulesExist()
    {
        using var context = CreateContext();

        var rules = await context.Client.GetFromJsonAsync<List<RuleDto>>("/rules", JsonOptions);

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
    public async Task GetAllSupportsConfigurablePagination()
    {
        using var context = CreateContext(
            ValidRule("rule-1", "one"),
            ValidRule("rule-2", "two"),
            ValidRule("rule-3", "three"));

        var rules = await context.Client.GetFromJsonAsync<List<RuleDto>>(
            "/rules?from=1&size=1",
            JsonOptions);
        var names = await context.Client.GetFromJsonAsync<List<string>>(
            "/rules?getNameOnly=true&from=1&size=2",
            JsonOptions);

        Assert.Equal(["rule-2"], rules?.Select(rule => rule.Id));
        Assert.Equal(["two", "three"], names);
    }

    [Fact]
    public async Task GetAllReturnsBadRequestForInvalidBooleanQueryValues()
    {
        using var context = CreateContext();

        var invalidIsActive = await context.Client.GetAsync("/rules?isActive=maybe");
        var invalidNameOnly = await context.Client.GetAsync("/rules?getNameOnly=maybe");
        var invalidFrom = await context.Client.GetAsync("/rules?from=-1");
        var invalidSize = await context.Client.GetAsync("/rules?size=0");
        var excessiveSize = await context.Client.GetAsync("/rules?size=1001");

        Assert.Equal(HttpStatusCode.BadRequest, invalidIsActive.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidNameOnly.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidFrom.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidSize.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, excessiveSize.StatusCode);
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
    public async Task CreateIgnoresServerOwnedFieldsAndStoresRule()
    {
        using var context = CreateContext();
        var request = ValidRule("client-controlled-id", "one");

        var response = await context.Client.PostAsJsonAsync("/rules", request, JsonOptions);
        var created = await response.Content.ReadFromJsonAsync<RuleDto>(JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(created?.Id));
        Assert.NotEqual("client-controlled-id", created.Id);
        Assert.True(created.CreationTime > request.CreationTime);
        Assert.True(created.UpdateTime > request.UpdateTime);
        Assert.EndsWith($"/rules/{created.Id}", response.Headers.Location?.ToString(), StringComparison.Ordinal);
        Assert.NotNull(await context.Repository.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task CreateRejectsGeoJsonOnlyRule()
    {
        using var context = CreateContext();
        var rule = ValidRule("rule-1", "geo-json-only");
        rule.LocationWkt = string.Empty;
        rule.LocationGeoJson = JsonDocument.Parse("{\"type\":\"Point\",\"coordinates\":[1,1]}").RootElement.Clone();

        var response = await context.Client.PostAsJsonAsync("/rules", rule, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        var response = await context.Client.PostAsJsonAsync("/rules", new { }, JsonOptions);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.RootElement.TryGetProperty("type", out _));
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
        Assert.True(body.RootElement.TryGetProperty("traceId", out _));
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
                  "algorithmName": ["Unknown"],
                  "isActive": true,
                  "minimumResolution": 0.5,
                  "maximumResolution": 1,
                  "area": "area",
                  "locationWkt": "POINT (1 1)"
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
        rule.IsPhotoOld = true;
        using var context = CreateContext(rule);
        var body = Json("{\"description\":null,\"isActive\":false,\"minimumResolution\":0.8}");

        var response = await context.Client.PatchAsync("/rules/rule-1", body);
        var updated = await response.Content.ReadFromJsonAsync<RuleDto>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(updated?.Description);
        Assert.False(updated?.IsActive);
        Assert.Equal("one", updated?.RuleName);
        Assert.True(updated?.IsPhotoOld);
        Assert.Equal(0.8, updated?.MinimumResolution);
    }

    [Fact]
    public async Task CreateAndPatchPersistAndReturnBothAlgorithmsAsAnArray()
    {
        using var context = CreateContext();
        var createRequest = ValidRule("ignored", "both-algorithms");
        createRequest.AlgorithmNames = [AlgorithmName.Rpn, AlgorithmName.FindAir];

        var createResponse = await context.Client.PostAsJsonAsync(
            "/rules",
            createRequest,
            JsonOptions);
        using var createdJson = JsonDocument.Parse(
            await createResponse.Content.ReadAsStringAsync());
        var createdAlgorithms = createdJson.RootElement
            .GetProperty("algorithmName")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        var createdId = createdJson.RootElement.GetProperty("_id").GetString();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(["FindAir", "Rpn"], createdAlgorithms);
        Assert.False(string.IsNullOrWhiteSpace(createdId));

        var patchResponse = await context.Client.PatchAsync(
            $"/rules/{createdId}",
            Json("""{"algorithmName":["Rpn"]}"""));
        var patched = await patchResponse.Content.ReadFromJsonAsync<RuleDto>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        Assert.Equal([AlgorithmName.Rpn], patched?.AlgorithmNames);
        Assert.Equal(
            [AlgorithmName.Rpn],
            (await context.Repository.GetByIdAsync(createdId!))?.AlgorithmNames);
    }

    [Theory]
    [InlineData("""{"algorithmName":[]}""")]
    [InlineData("""{"algorithmName":["FindAir","FindAir"]}""")]
    [InlineData("""{"algorithmName":"FindAir"}""")]
    public async Task PatchOneRejectsInvalidAlgorithmSelections(string body)
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var response = await context.Client.PatchAsync("/rules/rule-1", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            [AlgorithmName.FindAir],
            (await context.Repository.GetByIdAsync("rule-1"))?.AlgorithmNames);
    }

    [Fact]
    public async Task PatchOneReplacesSensorsAndNormalizesValues()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = [Accurate];
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsync(
            "/rules/rule-1",
            Json("""
                {
                  "sensors": {
                    "thermal": ["Sensor", "Sensor", "Accurate"],
                    "": ["Sensor"]
                  },
                  "tenantsInfo": [
                    {
                      "tenantId": "tenant-2",
                      "tilingConfigs": [
                        {
                          "tileSizeWidth": 1024,
                          "tileSizeHeight": 512,
                          "tileOverlapWidth": 64,
                          "tileOverlapHeight": 32
                        }
                      ]
                    }
                  ]
                }
                """));
        var updated = await response.Content.ReadFromJsonAsync<RuleDto>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(updated?.Sensors.ContainsKey("camera"));
        Assert.Equal([Sensor, Accurate], updated?.Sensors["thermal"]);
        Assert.False(updated?.Sensors.ContainsKey(""));
        var tenant = Assert.Single(updated?.TenantsInfo ?? []);
        Assert.Equal("tenant-2", tenant.TenantId);
        Assert.Equal(1024, Assert.Single(tenant.TilingConfigs).TileSizeWidth);
    }

    [Fact]
    public async Task PatchOneCannotClearRequiredWkt()
    {
        var rule = ValidRule("rule-1", "one");
        rule.IsPhotoOld = true;
        rule.LocationGeoJson = JsonDocument.Parse("{\"type\":\"Point\",\"coordinates\":[1,1]}").RootElement.Clone();
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsync(
            "/rules/rule-1",
            Json("{\"isPhotoOld\":null,\"locationWkt\":null}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await context.Repository.GetByIdAsync("rule-1");
        Assert.True(stored?.IsPhotoOld);
        Assert.Equal("POINT (1 1)", stored?.LocationWkt);
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
        var invalidResolution = await context.Client.PatchAsync("/rules/rule-1", Json("{\"minimumResolution\":0}"));
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
            Json("{\"isActive\":false,\"isPhotoOld\":true}"));
        var result = await response.Content.ReadFromJsonAsync<BulkOperationResult>(JsonOptions);

        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
        Assert.Equal(["rule-1", "rule-2"], result?.SuccessIds);
        var failure = Assert.Single(result?.FailedIds ?? []);
        Assert.Equal("missing", failure.Id);
        Assert.False((await context.Repository.GetByIdAsync("rule-1"))?.IsActive);
        Assert.True((await context.Repository.GetByIdAsync("rule-2"))?.IsPhotoOld);
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
    public async Task PatchBulkReturnsUnprocessableEntityWhenEveryItemFails()
    {
        using var context = CreateContext();

        var response = await context.Client.PatchAsync(
            "/rules/bulk?ids=missing-1,missing-2",
            Json("{\"isActive\":false}"));
        var result = await response.Content.ReadFromJsonAsync<BulkOperationResult>(JsonOptions);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(result?.SuccessIds ?? []);
        Assert.Equal(2, result?.FailedIds.Count);
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
        var updated = await response.Content.ReadFromJsonAsync<RuleDto>(JsonOptions);

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
        var missingActivity = await context.Client.PatchAsync(
            "/rules/rule-1/activity",
            Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingActivity.StatusCode);
    }

    [Fact]
    public async Task CreateAndUpdateRejectNullSensorValueLists()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var create = await context.Client.PostAsync(
            "/rules",
            Json("""
                {
                  "ruleName": "invalid-sensors",
                  "algorithmName": ["FindAir"],
                  "sensors": { "camera": null },
                  "minimumResolution": 0.5,
                  "maximumResolution": 999,
                  "locationWkt": "POINT (1 1)"
                }
                """));
        var update = await context.Client.PatchAsync(
            "/rules/rule-1",
            Json("""{ "sensors": { "camera": null } }"""));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task CreateAndUpdateRejectEmptySensorValueLists()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var create = await context.Client.PostAsync(
            "/rules",
            Json("""
                {
                  "ruleName": "invalid-sensors",
                  "algorithmName": ["FindAir"],
                  "sensors": { "camera": [] },
                  "minimumResolution": 0.5,
                  "maximumResolution": 999,
                  "locationWkt": "POINT (1 1)"
                }
                """));
        var update = await context.Client.PatchAsync(
            "/rules/rule-1",
            Json("""{ "sensors": { "camera": [] } }"""));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task AddSensorsRouteAddsUniqueValuesAndReportsMissingIds()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = [Accurate];
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/add?ids=rule-1,missing",
            new { sensorName = "camera", values = new[] { "Accurate", "Sensor" } },
            JsonOptions);
        var result = await response.Content.ReadFromJsonAsync<BulkOperationResult>(JsonOptions);

        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
        Assert.Equal(["rule-1"], result?.SuccessIds);
        Assert.Equal([Accurate, Sensor], (await context.Repository.GetByIdAsync("rule-1"))?.Sensors["camera"]);
        Assert.Single(result?.FailedIds ?? []);
    }

    [Fact]
    public async Task SensorRoutesReturnUnprocessableEntityWhenEveryItemFails()
    {
        using var context = CreateContext();
        var request = new { sensorName = "camera", values = new[] { "Accurate" } };

        var add = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/add?ids=missing-1,missing-2",
            request,
            JsonOptions);
        var remove = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/remove?ids=missing-1,missing-2",
            request,
            JsonOptions);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, remove.StatusCode);
    }

    [Fact]
    public async Task AddSensorsRouteCreatesNewSensorKey()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/add?ids=rule-1",
            new { sensorName = "thermal", values = new[] { "Sensor", "Accurate" } },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Sensor, Accurate], (await context.Repository.GetByIdAsync("rule-1"))?.Sensors["thermal"]);
    }

    [Fact]
    public async Task RemoveSensorsRouteRemovesValuesAndDeletesEmptyKey()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = [Accurate];
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/remove?ids=rule-1",
            new { sensorName = "camera", values = new[] { "Accurate" } },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await context.Repository.GetByIdAsync("rule-1"))?.Sensors.ContainsKey("camera"));
    }

    [Fact]
    public async Task RemoveSensorsRouteLeavesRuleUnchangedWhenSensorIsMissing()
    {
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = [Accurate];
        using var context = CreateContext(rule);

        var response = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/remove?ids=rule-1",
            new { sensorName = "thermal", values = new[] { "Sensor" } },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Accurate], (await context.Repository.GetByIdAsync("rule-1"))?.Sensors["camera"]);
    }

    [Fact]
    public async Task SensorRoutesReturnBadRequestForInvalidSensorPayloadOrEmptyIds()
    {
        using var context = CreateContext(ValidRule("rule-1", "one"));

        var invalidPayload = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/add?ids=rule-1",
            new { sensorName = "", values = new[] { "Accurate", "Accurate" } },
            JsonOptions);
        var emptyIds = await context.Client.PatchAsJsonAsync(
            "/rules/sensors/remove",
            new { sensorName = "camera", values = new[] { "Accurate" } },
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
        Assert.DoesNotContain("Sensitive Elasticsearch failure details.", body, StringComparison.Ordinal);
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
            new { sensorName = "camera", values = new[] { "Accurate" } },
            JsonOptions);
        var removeSensor = await client.PatchAsJsonAsync(
            "/rules/sensors/remove?ids=rule-1",
            new { sensorName = "camera", values = new[] { "Accurate" } },
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

    private static TestContext CreateContext(params RuleDto[] rules)
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

    private static RuleDto ValidRule(string id, string ruleName, bool isActive = true) =>
        new()
        {
            Id = id,
            RuleName = ruleName,
            AlgorithmNames = [AlgorithmName.FindAir],
            IsActive = isActive,
            MinimumResolution = 0.5,
            MaximumResolution = 1,
            Area = "area",
            LocationWkt = "POINT (1 1)",
            TenantsInfo =
            [
                new TenantInfo
                {
                    TenantId = "tenant-1",
                    TilingConfigs =
                    [
                        new TilingConfig
                        {
                            TileSizeWidth = 512,
                            TileSizeHeight = 512
                        }
                    ]
                }
            ],
            CreationTime = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdateTime = DateTimeOffset.UtcNow.AddMinutes(-5)
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
