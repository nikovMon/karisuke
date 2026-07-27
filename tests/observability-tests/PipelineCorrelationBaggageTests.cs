using OpenTelemetry;

namespace ImagingPipeline.Observability.Tests;

public sealed class PipelineCorrelationBaggageTests
{
    [Fact]
    public void PushKeepsOnlyCanonicalValuesOverwritesAuthoritativeFieldsAndRestoresAmbientState()
    {
        var previous = Baggage.Current;
        try
        {
            Baggage.Current = Baggage.Create(new Dictionary<string, string>
            {
                [TelemetryAttributeNames.PipelineTaskId] = "stale-task",
                [TelemetryAttributeNames.PipelineRequestId] = "upstream-request",
                ["secret"] = "do-not-forward"
            });

            using (PipelineCorrelationBaggage.Push(
                       new PipelineCorrelationContext(
                           TaskId: "task-1",
                           ImageId: "image-1",
                           RuleId: "rule-1",
                           TenantId: "tenant-1",
                           AlgorithmName: "FindAir"),
                       includeExistingCanonicalValues: true))
            {
                Assert.Equal("task-1", Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineTaskId));
                Assert.Equal(
                    "upstream-request",
                    Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineRequestId));
                Assert.Equal("image-1", Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineImageId));
                Assert.Equal("rule-1", Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineRuleId));
                Assert.Equal("tenant-1", Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineTenantId));
                Assert.Equal("FindAir", Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineAlgorithmName));
                Assert.Null(Baggage.Current.GetBaggage("secret"));
            }

            Assert.Equal("stale-task", Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineTaskId));
            Assert.Equal("do-not-forward", Baggage.Current.GetBaggage("secret"));
        }
        finally
        {
            Baggage.Current = previous;
        }
    }

    [Fact]
    public void OversizedOrBlankAuthoritativeValuesAreNotPropagated()
    {
        var previous = Baggage.Current;
        try
        {
            Baggage.Current = Baggage.Create(new Dictionary<string, string>
            {
                [TelemetryAttributeNames.PipelineTaskId] = "stale-task"
            });

            using (PipelineCorrelationBaggage.Push(
                       new PipelineCorrelationContext(
                           TaskId: new string('x', 300),
                           RequestId: " "),
                       includeExistingCanonicalValues: true))
            {
                Assert.Null(Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineTaskId));
                Assert.Null(Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineRequestId));
            }
        }
        finally
        {
            Baggage.Current = previous;
        }
    }

    [Fact]
    public void ScopeRestoresAfterExceptionAndSequentialScopesDoNotLeakRequestIds()
    {
        var previous = Baggage.Current;
        try
        {
            Baggage.Current = Baggage.Create(new Dictionary<string, string>
            {
                ["outer"] = "restored"
            });

            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var _ = PipelineCorrelationBaggage.Push(
                    new PipelineCorrelationContext(TaskId: "task-a", RequestId: "request-a"));
                throw new InvalidOperationException("publish failed");
            }));
            Assert.Equal("restored", Baggage.Current.GetBaggage("outer"));

            using (PipelineCorrelationBaggage.Push(
                       new PipelineCorrelationContext(TaskId: "task-b")))
            {
                Assert.Equal("task-b", Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineTaskId));
                Assert.Null(Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineRequestId));
            }

            Assert.Equal("restored", Baggage.Current.GetBaggage("outer"));
        }
        finally
        {
            Baggage.Current = previous;
        }
    }
}
