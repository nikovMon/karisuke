using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Api.Services;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ImagingPipeline.Common.Dtos.Rules.Models.RegistrationQuality;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RuleServiceTests
{
    [Fact]
    public async Task CreateSeparatesMultipleValidationErrors()
    {
        var service = CreateService(new InMemoryRuleRepository());

        var result = await service.CreateAsync(new CreateRuleRequest
        {
            AlgorithmNames = [AlgorithmName.FindAir]
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
        request.Sensors["camera"] = [Accurate, Accurate, Sensor];
        request.Sensors[" "] = [Sensor];

        var result = await service.CreateAsync(request);

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Value?.Id));
        Assert.NotEqual("rule-1", result.Value?.Id);
        Assert.Equal([Accurate, Sensor], result.Value?.Sensors["camera"]);
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
    public async Task CreateConvertsValidWktToGeoJsonAndRejectsInvalidWkt()
    {
        var service = CreateService(new InMemoryRuleRepository());
        var valid = ValidCreateRequest("valid");
        valid.LocationWkt = "POINT (10 20)";
        var invalid = ValidCreateRequest("invalid");
        invalid.LocationWkt = "not wkt";

        var validResult = await service.CreateAsync(valid);
        var invalidResult = await service.CreateAsync(invalid);

        Assert.Equal(RuleOperationStatus.Success, validResult.Status);
        Assert.Equal("Point", validResult.Value?.LocationGeoJson?.GetProperty("type").GetString());
        Assert.Equal(10, validResult.Value?.LocationGeoJson?.GetProperty("coordinates")[0].GetDouble());
        Assert.Equal(RuleOperationStatus.ValidationFailed, invalidResult.Status);
        Assert.Contains("valid WKT", invalidResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAndBulkUpdateConvertWktToGeoJson()
    {
        var repository = new InMemoryRuleRepository();
        repository.Add(ValidRule("rule-1", "one"));
        repository.Add(ValidRule("rule-2", "two"));
        var service = CreateService(repository);

        var update = await service.UpdateAsync("rule-1", new UpdateRuleRequest
        {
            LocationWkt = "POINT (2 3)"
        });
        var bulk = await service.UpdateBulkAsync(["rule-1", "rule-2"], new UpdateRuleRequest
        {
            LocationWkt = "POINT (4 5)"
        });

        Assert.Equal(RuleOperationStatus.Success, update.Status);
        Assert.Equal(2, update.Value?.LocationGeoJson?.GetProperty("coordinates")[0].GetDouble());
        Assert.Equal(RuleOperationStatus.Success, bulk.Status);
        Assert.All(new[] { "rule-1", "rule-2" }, id =>
        {
            var stored = repository.GetByIdAsync(id).GetAwaiter().GetResult();
            Assert.Equal(4, stored?.LocationGeoJson?.GetProperty("coordinates")[0].GetDouble());
        });
    }

    [Fact]
    public async Task UpdateAndBulkUpdateRejectInvalidWkt()
    {
        var repository = new InMemoryRuleRepository();
        repository.Add(ValidRule("rule-1", "one"));
        var service = CreateService(repository);
        var invalid = new UpdateRuleRequest { LocationWkt = "invalid" };

        var update = await service.UpdateAsync("rule-1", invalid);
        var bulk = await service.UpdateBulkAsync(["rule-1"], invalid);

        Assert.Equal(RuleOperationStatus.ValidationFailed, update.Status);
        Assert.Equal(RuleOperationStatus.ValidationFailed, bulk.Status);
        Assert.Equal("POINT (1 1)", (await repository.GetByIdAsync("rule-1"))?.LocationWkt);
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
        Assert.Contains("locationWkt is required", invalidResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateReplacesCollectionsAndCanClearOptionalFields()
    {
        var repository = new InMemoryRuleRepository();
        var rule = ValidRule("rule-1", "one");
        rule.Description = "old description";
        rule.Sensors["camera"] = [Accurate];
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
            Sensors = new Dictionary<string, List<RegistrationQuality>>(StringComparer.Ordinal)
            {
                ["thermal"] = [Sensor, Sensor, Accurate]
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
        Assert.Equal([Sensor, Accurate], result.Value?.Sensors["thermal"]);
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
    public async Task BulkUpdateLogsOneWarningWithAtMostTenFailedIds()
    {
        var repository = new InMemoryRuleRepository();
        repository.Add(ValidRule("rule-1", "one"));
        var logger = new RecordingLogger<RuleService>();
        var service = new RuleService(
            repository,
            Options.Create(new RulesElasticsearchOptions { IndexName = "rules" }),
            logger);
        var missingIds = Enumerable.Range(1, 12).Select(index => $"missing-{index}").ToArray();
        var ids = new[] { "rule-1" }.Concat(missingIds).ToArray();
        var request = new UpdateRuleRequest { IsActive = false };
        request.ProvidedFields.Add("isActive");

        var result = await service.UpdateBulkAsync(ids, request);

        Assert.Equal(RuleOperationStatus.PartialSuccess, result.Status);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(5025, warning.EventId.Id);
        Assert.Equal("bulk_update", warning.Properties["Operation"]);
        Assert.Equal(13, warning.Properties["RequestedCount"]);
        Assert.Equal(1, warning.Properties["SuccessCount"]);
        Assert.Equal(12, warning.Properties["FailureCount"]);
        Assert.Null(warning.Properties["SensorName"]);
        Assert.Equal(2, warning.Properties["OmittedFailureCount"]);
        Assert.Equal(
            missingIds.Take(10),
            warning.Properties["FailedIdSample"]?.ToString()?.Split(", ", StringSplitOptions.None));
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == 5011);
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
        rule.Sensors["camera"] = [Accurate];
        repository.Add(rule);
        var service = CreateService(repository);

        var result = await service.AddSensorsAsync(["rule-1"], new RuleSensorUpdateRequest
        {
            SensorName = "camera",
            Values = [Accurate, Sensor]
        });

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        var updated = await repository.GetByIdAsync("rule-1");
        Assert.Equal([Accurate, Sensor], updated?.Sensors["camera"]);
    }

    [Fact]
    public async Task SensorBulkFailureLogsOneBoundedWarningWithSensorMetadata()
    {
        var logger = new RecordingLogger<RuleService>();
        var service = new RuleService(
            new InMemoryRuleRepository(),
            Options.Create(new RulesElasticsearchOptions { IndexName = "rules" }),
            logger);
        var missingIds = Enumerable.Range(1, 12).Select(index => $"missing-{index}").ToArray();

        var result = await service.AddSensorsAsync(missingIds, new RuleSensorUpdateRequest
        {
            SensorName = "camera",
            Values = [Accurate]
        });

        Assert.Equal(12, result.Value?.FailedIds.Count);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(5025, warning.EventId.Id);
        Assert.Equal("add_sensors", warning.Properties["Operation"]);
        Assert.Equal(12, warning.Properties["RequestedCount"]);
        Assert.Equal(0, warning.Properties["SuccessCount"]);
        Assert.Equal(12, warning.Properties["FailureCount"]);
        Assert.Equal("camera", warning.Properties["SensorName"]);
        Assert.Equal(2, warning.Properties["OmittedFailureCount"]);
        Assert.Equal(
            missingIds.Take(10),
            warning.Properties["FailedIdSample"]?.ToString()?.Split(", ", StringSplitOptions.None));
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
            Values = [Sensor, Accurate]
        });

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        var updated = await repository.GetByIdAsync("rule-1");
        Assert.Equal([Sensor, Accurate], updated?.Sensors["thermal"]);
    }

    [Fact]
    public async Task RemoveSensorsDeletesSensorKeyWhenEmpty()
    {
        var repository = new InMemoryRuleRepository();
        var rule = ValidRule("rule-1", "one");
        rule.Sensors["camera"] = [Accurate];
        repository.Add(rule);
        var service = CreateService(repository);

        var result = await service.RemoveSensorsAsync(["rule-1"], new RuleSensorUpdateRequest
        {
            SensorName = "camera",
            Values = [Accurate]
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
        rule.Sensors["camera"] = [Accurate];
        repository.Add(rule);
        var service = CreateService(repository);

        var result = await service.RemoveSensorsAsync(["rule-1"], new RuleSensorUpdateRequest
        {
            SensorName = "thermal",
            Values = [Sensor]
        });

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        var updated = await repository.GetByIdAsync("rule-1");
        Assert.Equal([Accurate], updated?.Sensors["camera"]);
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
            Values = [Accurate, Accurate]
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

    [Fact]
    public async Task SuccessfulCreateWritesStructuredDebugSuccessWithoutInformationLog()
    {
        var logger = new RecordingLogger<RuleService>();
        var service = new RuleService(
            new InMemoryRuleRepository(),
            Options.Create(new RulesElasticsearchOptions { IndexName = "rules" }),
            logger);

        var result = await service.CreateAsync(ValidCreateRequest("observed-rule"));

        Assert.Equal(RuleOperationStatus.Success, result.Status);
        var entry = Assert.Single(
            logger.Entries,
            item => item.EventId.Id == 5020 && item.Level == LogLevel.Debug);
        Assert.Equal("create", entry.Properties["Operation"]);
        Assert.Equal(result.Value?.Id, entry.Properties["RuleId"]);
        Assert.Equal("observed-rule", entry.Properties["RuleName"]);
        Assert.DoesNotContain(logger.Entries, item => item.Level == LogLevel.Information);
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
            AlgorithmNames = [AlgorithmName.FindAir],
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
            AlgorithmNames = [AlgorithmName.FindAir],
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
