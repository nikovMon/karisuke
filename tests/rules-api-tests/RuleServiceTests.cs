using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Api.Services;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RuleServiceTests
{
    [Fact]
    public async Task CreateSeparatesMultipleValidationErrors()
    {
        var service = CreateService(new InMemoryRuleRepository());

        var result = await service.CreateAsync(new CreateRuleRequest
        {
            AlgorithmName = AlgorithmName.FindAir
        });

        Assert.Equal(RuleOperationStatus.ValidationFailed, result.Status);
        Assert.Contains(" | ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRejectsDuplicateRuleName()
    {
        var repository = new InMemoryRuleRepository();
        repository.Add(ValidRule("rule-1", "same-name"));
        var service = CreateService(repository);

        var result = await service.CreateAsync(ValidCreateRequest("same-name"));

        Assert.Equal(RuleOperationStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task CreateNormalizesSensorCollectionsAndSetsTimestamps()
    {
        var repository = new InMemoryRuleRepository();
        var service = CreateService(repository);
        var request = ValidCreateRequest("one");
        request.Sensors["camera"] = ["cam-1", "cam-1", "", "cam-2"];
        request.Sensors[" "] = ["ignored"];

        var result = await service.CreateAsync(request);

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Value?.Id));
        Assert.NotEqual("rule-1", result.Value?.Id);
        Assert.Equal(["cam-1", "cam-2"], result.Value?.Sensors["camera"]);
        Assert.False(result.Value?.Sensors.ContainsKey(" "));
        Assert.True(result.Value?.CreationTime > DateTimeOffset.MinValue);
        Assert.True(result.Value?.UpdateTime > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task CreateUsesRepositoryGeneratedId()
    {
        var repository = new InMemoryRuleRepository();
        var service = CreateService(repository);

        var result = await service.CreateAsync(ValidCreateRequest("one"));

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        var generatedId = Assert.IsType<string>(result.Value?.Id);
        Assert.NotEmpty(generatedId);
        Assert.NotNull(await repository.GetByIdAsync(generatedId));
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
        Assert.True(result.Value?.UpdateTime > original.UpdateTime);
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
            LocationWkt = null,
            LocationGeoJson = null
        };
        clearLocation.ProvidedFields.Add("locationWkt");
        clearLocation.ProvidedFields.Add("locationGeoJson");

        var sameNameResult = await service.UpdateAsync("rule-1", renameToSame);
        var invalidResult = await service.UpdateAsync("rule-1", clearLocation);

        Assert.Equal(RuleOperationStatus.Success, sameNameResult.Status);
        Assert.Equal(RuleOperationStatus.ValidationFailed, invalidResult.Status);
        Assert.Contains("locationWkt, locationGeoJson, or both", invalidResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateReplacesCollectionsAndCanClearOptionalFields()
    {
        var repository = new InMemoryRuleRepository();
        var rule = ValidRule("rule-1", "one");
        rule.Description = "old description";
        rule.Sensors["camera"] = ["cam-1"];
        rule.TenantsInfo =
        [
            new TenantInfo
            {
                TenantId = "old-tenant",
                TilingConfigs =
                [
                    new TilingConfig
                    {
                        TileSizeWidth = 256,
                        TileSizeHeight = 256
                    }
                ]
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
            TenantsInfo =
            [
                new TenantInfo
                {
                    TenantId = "new-tenant",
                    TilingConfigs =
                    [
                        new TilingConfig
                        {
                            TileSizeWidth = 512,
                            TileSizeHeight = 512,
                            TileOverlapWidth = 32,
                            TileOverlapHeight = 32
                        }
                    ]
                }
            ]
        };
        request.ProvidedFields.Add("description");
        request.ProvidedFields.Add("sensors");
        request.ProvidedFields.Add("tenantsInfo");

        var result = await service.UpdateAsync("rule-1", request);

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        Assert.Null(result.Value?.Description);
        Assert.False(result.Value?.Sensors.ContainsKey("camera"));
        Assert.Equal(["th-1"], result.Value?.Sensors["thermal"]);
        var tenant = Assert.Single(result.Value?.TenantsInfo ?? []);
        Assert.Equal("new-tenant", tenant.TenantId);
        var tiling = Assert.Single(tenant.TilingConfigs);
        Assert.Equal(512, tiling.TileSizeWidth);
        Assert.Equal(32, tiling.TileOverlapWidth);
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

        Assert.Equal(RuleOperationStatus.PartialSuccess, result.Status);
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

        var result = await service.ChangeActivityAsync(
            "missing",
            new ChangeRuleActivationStatusRequest { IsActive = true });

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
        var nullValuesResult = await service.AddSensorsAsync(["rule-1"], new RuleSensorUpdateRequest
        {
            SensorName = "camera",
            Values = null!
        });

        Assert.Equal(RuleOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("unique", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RuleOperationStatus.ValidationFailed, nullValuesResult.Status);
        Assert.Contains("empty", nullValuesResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidationFailureWritesStructuredWarningMetadata()
    {
        var logger = new RecordingLogger<RuleService>();
        var service = new RuleService(
            new InMemoryRuleRepository(),
            Options.Create(new RulesElasticsearchOptions { IndexName = "rules" }),
            logger);
        var request = new UpdateRuleRequest();

        var result = await service.UpdateAsync("rule-1", request);

        Assert.Equal(RuleOperationStatus.ValidationFailed, result.Status);
        var debugEntry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);
        Assert.Equal("update", debugEntry.Properties["Operation"]);
        Assert.Equal("rule-1", debugEntry.Properties["RuleId"]);
        var entry = Assert.Single(logger.Entries, item => item.Level == LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("update", entry.Properties["Operation"]);
        Assert.Equal("rule-1", entry.Properties["RuleId"]);
        Assert.Equal(1, entry.Properties["ErrorCount"]);
        Assert.Contains("At least one field", entry.Properties["ValidationErrors"]?.ToString(), StringComparison.Ordinal);
    }

    private static RuleService CreateService(InMemoryRuleRepository repository) =>
        new(
            repository,
            Options.Create(new RulesElasticsearchOptions { IndexName = "rules" }),
            NullLogger<RuleService>.Instance);

    private static CreateRuleRequest ValidCreateRequest(string ruleName) =>
        new()
        {
            RuleName = ruleName,
            AlgorithmName = AlgorithmName.FindAir,
            IsActive = true,
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
            ]
        };

    private static RuleDto ValidRule(string id, string ruleName) =>
        new()
        {
            Id = id,
            RuleName = ruleName,
            AlgorithmName = AlgorithmName.FindAir,
            IsActive = true,
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
}
