using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Rules.Api.Services;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RuleServiceTests
{
    [Fact]
    public async Task CreateRejectsDuplicateRuleName()
    {
        var repository = new InMemoryRuleRepository();
        repository.Add(ValidRule("rule-1", "same-name"));
        var service = CreateService(repository);

        var result = await service.CreateAsync(ValidRule("rule-2", "same-name"));

        Assert.Equal(RuleOperationStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task CreateNormalizesSensorCollectionsAndSetsTimestamps()
    {
        var repository = new InMemoryRuleRepository();
        var service = CreateService(repository);
        var rule = ValidRule("rule-1", "one");
        rule.CreatedAt = DateTimeOffset.MinValue;
        rule.ModifiedAt = DateTimeOffset.MinValue;
        rule.Sensors["camera"] = ["cam-1", "cam-1", "", "cam-2"];
        rule.Sensors[" "] = ["ignored"];

        var result = await service.CreateAsync(rule);

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        Assert.Equal(["cam-1", "cam-2"], result.Value?.Sensors["camera"]);
        Assert.False(result.Value?.Sensors.ContainsKey(" "));
        Assert.True(result.Value?.CreatedAt > DateTimeOffset.MinValue);
        Assert.True(result.Value?.ModifiedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task UpdateOnlyChangesProvidedFieldsAndUpdatesModifiedAt()
    {
        var repository = new InMemoryRuleRepository();
        var original = ValidRule("rule-1", "old-name");
        repository.Add(original);
        var service = CreateService(repository);
        var request = new UpdateRuleRequest
        {
            Description = "new description"
        };
        request.ProvidedFields.Add("description");

        var result = await service.UpdateAsync("rule-1", request);

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        Assert.Equal("old-name", result.Value?.RuleName);
        Assert.Equal("new description", result.Value?.Description);
        Assert.True(result.Value?.ModifiedAt > original.ModifiedAt);
    }

    [Fact]
    public async Task UpdateAllowsSameRuleNameAndRejectsInvalidFinalRule()
    {
        var repository = new InMemoryRuleRepository();
        var rule = ValidRule("rule-1", "same-name");
        repository.Add(rule);
        var service = CreateService(repository);
        var renameToSame = new UpdateRuleRequest
        {
            RuleName = "same-name"
        };
        renameToSame.ProvidedFields.Add("ruleName");
        var clearLocation = new UpdateRuleRequest
        {
            Wkt = null,
            GeoJson = null
        };
        clearLocation.ProvidedFields.Add("wkt");
        clearLocation.ProvidedFields.Add("geoJson");

        var sameNameResult = await service.UpdateAsync("rule-1", renameToSame);
        var invalidResult = await service.UpdateAsync("rule-1", clearLocation);

        Assert.Equal(RuleOperationStatus.Success, sameNameResult.Status);
        Assert.Equal(RuleOperationStatus.ValidationFailed, invalidResult.Status);
        Assert.Contains("wkt, geoJson, or both", invalidResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateReplacesCollectionsAndCanClearOptionalFields()
    {
        var repository = new InMemoryRuleRepository();
        var rule = ValidRule("rule-1", "one");
        rule.Description = "old description";
        rule.Sensors["camera"] = ["cam-1"];
        rule.Tenants =
        [
            new TenantConfigDto
            {
                TenantName = "old-tenant",
                TilingConfig = new TilingConfigDto { Width = 256, Length = 256 }
            }
        ];
        repository.Add(rule);
        var service = CreateService(repository);
        var request = new UpdateRuleRequest
        {
            Description = null,
            Sensors = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["thermal"] = ["th-1", "th-1", ""]
            },
            Tenants =
            [
                new TenantConfigDto
                {
                    TenantName = "new-tenant",
                    TilingConfig = new TilingConfigDto { Width = 512, Length = 512 }
                }
            ]
        };
        request.ProvidedFields.Add("description");
        request.ProvidedFields.Add("sensors");
        request.ProvidedFields.Add("tenants");

        var result = await service.UpdateAsync("rule-1", request);

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        Assert.Null(result.Value?.Description);
        Assert.False(result.Value?.Sensors.ContainsKey("camera"));
        Assert.Equal(["th-1"], result.Value?.Sensors["thermal"]);
        var tenant = Assert.Single(result.Value?.Tenants ?? []);
        Assert.Equal("new-tenant", tenant.TenantName);
        Assert.Equal(512, tenant.TilingConfig.Width);
    }

    [Fact]
    public async Task BulkUpdateReportsMissingIds()
    {
        var repository = new InMemoryRuleRepository();
        repository.Add(ValidRule("rule-1", "one"));
        var service = CreateService(repository);
        var request = new UpdateRuleRequest
        {
            IsActive = false
        };
        request.ProvidedFields.Add("isActive");

        var result = await service.UpdateBulkAsync(["rule-1", "missing"], request);

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        Assert.Equal(["rule-1"], result.Value?.SuccessIds);
        var failure = Assert.Single(result.Value?.FailedIds ?? []);
        Assert.Equal("missing", failure.Id);
        Assert.Contains("not found", failure.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BulkUpdateAllowsRuleNameChangeForSingleRule()
    {
        var repository = new InMemoryRuleRepository();
        repository.Add(ValidRule("rule-1", "one"));
        var service = CreateService(repository);
        var request = new UpdateRuleRequest
        {
            RuleName = "renamed"
        };
        request.ProvidedFields.Add("ruleName");

        var result = await service.UpdateBulkAsync(["rule-1"], request);

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        Assert.Equal(["rule-1"], result.Value?.SuccessIds);
        Assert.Equal("renamed", (await repository.GetByIdAsync("rule-1"))?.RuleName);
    }

    [Fact]
    public async Task BulkUpdateRejectsSameRuleNameForMultipleRules()
    {
        var service = CreateService(new InMemoryRuleRepository());
        var request = new UpdateRuleRequest
        {
            RuleName = "same-name"
        };
        request.ProvidedFields.Add("ruleName");

        var result = await service.UpdateBulkAsync(["rule-1", "rule-2"], request);

        Assert.Equal(RuleOperationStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task ChangeActivityReturnsNotFoundForMissingRule()
    {
        var service = CreateService(new InMemoryRuleRepository());

        var result = await service.ChangeActivityAsync("missing", new ChangeRuleActivityRequest { IsActive = true });

        Assert.Equal(RuleOperationStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task AddSensorsAddsOnlyNewValues()
    {
        var repository = new InMemoryRuleRepository();
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = ["cam-1"];
        repository.Add(rule);
        var service = CreateService(repository);

        var result = await service.AddSensorsAsync(["rule-1"], new RuleSensorUpdateRequest
        {
            SensorName = "camera",
            Values = ["cam-1", "cam-2"]
        });

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        var updated = await repository.GetByIdAsync("rule-1");
        Assert.Equal(["cam-1", "cam-2"], updated?.Sensors["camera"]);
    }

    [Fact]
    public async Task AddSensorsCreatesMissingSensorKeyWithDistinctValues()
    {
        var repository = new InMemoryRuleRepository();
        repository.Add(ValidRule("rule-1", "one"));
        var service = CreateService(repository);

        var result = await service.AddSensorsAsync(["rule-1"], new RuleSensorUpdateRequest
        {
            SensorName = "thermal",
            Values = ["th-1", "th-2"]
        });

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        var updated = await repository.GetByIdAsync("rule-1");
        Assert.Equal(["th-1", "th-2"], updated?.Sensors["thermal"]);
    }

    [Fact]
    public async Task RemoveSensorsDeletesSensorKeyWhenEmpty()
    {
        var repository = new InMemoryRuleRepository();
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = ["cam-1"];
        repository.Add(rule);
        var service = CreateService(repository);

        var result = await service.RemoveSensorsAsync(["rule-1"], new RuleSensorUpdateRequest
        {
            SensorName = "camera",
            Values = ["cam-1"]
        });

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        var updated = await repository.GetByIdAsync("rule-1");
        Assert.False(updated?.Sensors.ContainsKey("camera"));
    }

    [Fact]
    public async Task RemoveSensorsFromMissingSensorStillSucceedsWithoutChangingOtherSensors()
    {
        var repository = new InMemoryRuleRepository();
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = ["cam-1"];
        repository.Add(rule);
        var service = CreateService(repository);

        var result = await service.RemoveSensorsAsync(["rule-1"], new RuleSensorUpdateRequest
        {
            SensorName = "thermal",
            Values = ["th-1"]
        });

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        var updated = await repository.GetByIdAsync("rule-1");
        Assert.Equal(["cam-1"], updated?.Sensors["camera"]);
    }

    [Fact]
    public async Task SensorMutationsRejectInvalidRequestBeforeReadingRules()
    {
        var repository = new InMemoryRuleRepository();
        repository.Add(ValidRule("rule-1", "one"));
        var service = CreateService(repository);

        var result = await service.AddSensorsAsync(["rule-1"], new RuleSensorUpdateRequest
        {
            SensorName = "camera",
            Values = ["cam-1", "cam-1"]
        });

        Assert.Equal(RuleOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("unique", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static RuleService CreateService(InMemoryRuleRepository repository) =>
        new(repository, NullLogger<RuleService>.Instance);

    private static RuleConfigDto ValidRule(string id, string ruleName) =>
        new()
        {
            Id = id,
            RuleName = ruleName,
            AlgorithmName = AlgorithmName.Finder,
            IsActive = true,
            MinResolution = 0.5,
            MaxResolution = 1,
            Area = "area",
            Wkt = "POINT (1 1)",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ModifiedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
}
