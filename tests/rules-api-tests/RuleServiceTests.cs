using ImagingPipeline.Rules.Contracts.Models;
using ImagingPipeline.Rules.Contracts.Requests;
using ImagingPipeline.Rules.Contracts.Responses;
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
