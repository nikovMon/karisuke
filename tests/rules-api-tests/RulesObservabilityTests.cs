using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Observability;
using ImagingPipeline.Rules.Api.Tests.Fakes;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RulesObservabilityTests
{
    [Fact]
    public async Task GetByIdEmitsDomainSpanWithoutUsingRuleIdAsMetricTag()
    {
        const string ruleId = "observability-span-only-rule";
        var stoppedActivities = new ConcurrentBag<Activity>();
        var metricTagKeys = new ConcurrentBag<string>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySourceNames.RulesApi,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == TelemetrySourceNames.RulesApi)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                metricTagKeys.Add(tag.Key);
            }
        });
        meterListener.Start();

        var repository = new InMemoryRuleRepository();
        repository.Add(new RuleDto
        {
            Id = ruleId,
            RuleName = "observed-rule",
            AlgorithmNames = [AlgorithmName.FindAir],
            MinimumResolution = 1,
            MaximumResolution = 2,
            Area = "test",
            LocationWkt = "POINT (1 1)"
        });
        using var factory = new RulesApiFactory(repository);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/rules/{ruleId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activity = Assert.Single(
            stoppedActivities,
            candidate => candidate.OperationName == "rules.get_by_id" &&
                Equals(candidate.GetTagItem(TelemetryAttributeNames.PipelineRuleId), ruleId));
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal("get_by_id", activity.GetTagItem("findair.rules.operation"));
        Assert.Equal(1L, activity.GetTagItem("findair.rules.response.document.count"));
        Assert.DoesNotContain(TelemetryAttributeNames.PipelineRuleId, metricTagKeys);
    }
}
