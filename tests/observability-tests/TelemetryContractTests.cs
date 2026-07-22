using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace ImagingPipeline.Observability.Tests;

public sealed class TelemetryContractTests
{
    [Fact]
    public void DisabledSdk_StillRegistersTransportPropagator()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Enabled"] = "false"
        });

        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);
        using var host = builder.Build();

        Assert.IsType<W3CMessageTraceContextPropagator>(
            host.Services.GetRequiredService<IMessageTraceContextPropagator>());
    }

    [Fact]
    public void MissingSamplerEnvironment_UsesConfiguredSafeFallback()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_TRACES_SAMPLER"] = string.Empty,
            ["Observability:Traces:DefaultSamplingRatio"] = "0",
            ["Observability:Metrics:Enabled"] = "false",
            ["Observability:Logs:Enabled"] = "false",
            ["Observability:Otlp:Enabled"] = "false"
        });
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);

        using var host = builder.Build();
        host.Start();
        using var activity = TelemetrySources.Gateway.StartActivity("sampling-test");

        Assert.True(activity is null || !activity.IsAllDataRequested);
    }

    [Fact]
    public void PipelineContextAndError_AreRecordedOnSampledSpan()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySourceNames.Gateway,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = TelemetrySources.Gateway.StartActivity("gateway.process", ActivityKind.Internal);
        var exception = new InvalidOperationException("failed");
        activity
            .AddPipelineContext(taskId: "task-1", imageId: "image-1", ruleId: "rule-1")
            .SetTelemetryError(TelemetryErrorCategory.Handler, exception);

        Assert.NotNull(activity);
        Assert.Equal("task-1", activity.GetTagItem(TelemetryAttributeNames.PipelineTaskId));
        Assert.Equal("image-1", activity.GetTagItem(TelemetryAttributeNames.PipelineImageId));
        Assert.Equal("rule-1", activity.GetTagItem(TelemetryAttributeNames.PipelineRuleId));
        Assert.Equal("handler", activity.GetTagItem(TelemetryAttributeNames.ErrorCategory));
        Assert.Equal("handler", activity.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        var exceptionEvent = Assert.Single(activity.Events, item => item.Name == "exception");
        Assert.DoesNotContain(exceptionEvent.Tags, tag => tag.Key == "exception.escaped");
    }

    [Fact]
    public void Metrics_DoNotUseRoutingKeysOrBusinessIdentifiersAsDimensions()
    {
        var measurements = new ConcurrentBag<Measurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (TelemetrySourceNames.All.Contains(instrument.Meter.Name, StringComparer.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, tags.ToArray())));
        listener.Start();

        MessagingTelemetry.RecordSent(
            "pipeline.x",
            "tenant-77/image-42",
            512,
            0.02,
            TelemetryOutcome.Success);
        DependencyTelemetry.RecordOperation(
            DependencyName.ProjectionMapper,
            DependencyOperation.GroundToImage,
            0.1,
            TelemetryOutcome.Success);
        PipelineTelemetry.RecordMessage(
            PipelineStage.Gateway,
            PipelineDirection.Ingress,
            TelemetryOutcome.Success);

        Assert.NotEmpty(measurements);
        var forbiddenValues = new[] { "tenant-77/image-42", "task-1", "image-42", "rule-1" };
        Assert.DoesNotContain(
            measurements.SelectMany(item => item.Tags),
            tag => forbiddenValues.Contains(tag.Value?.ToString(), StringComparer.Ordinal));
        Assert.DoesNotContain(
            measurements.SelectMany(item => item.Tags),
            tag => tag.Key.Contains("routing_key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DependencySizeAndBatchMetrics_DoNotPredeclareOperationOutcome()
    {
        var measurements = new ConcurrentBag<Measurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetrySourceNames.Dependencies &&
                    instrument.Name is TelemetryMetricNames.DependencyPayloadSize or
                        TelemetryMetricNames.DependencyBatchSize)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, tags.ToArray())));
        listener.Start();

        DependencyTelemetry.RecordPayloadSize(
            DependencyName.Elasticsearch,
            DependencyOperation.Search,
            PipelineDirection.Egress,
            512);
        DependencyTelemetry.RecordBatchSize(
            DependencyName.Elasticsearch,
            DependencyOperation.Search,
            PipelineItem.Document,
            7);

        Assert.Equal(2, measurements.Count);
        Assert.All(measurements, measurement =>
        {
            Assert.Contains(
                measurement.Tags,
                tag => tag.Key == TelemetryAttributeNames.DependencyName &&
                    Equals(tag.Value, "elasticsearch"));
            Assert.Contains(
                measurement.Tags,
                tag => tag.Key == TelemetryAttributeNames.DependencyOperation &&
                    Equals(tag.Value, "search"));
            Assert.DoesNotContain(
                measurement.Tags,
                tag => tag.Key is TelemetryAttributeNames.PipelineOutcome or "error.type");
        });
    }

    [Fact]
    public void RulesBatchSize_DoesNotPredeclareOperationOutcome()
    {
        KeyValuePair<string, object?>[]? capturedTags = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == TelemetryMetricNames.RulesBatchSize)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => capturedTags = tags.ToArray());
        listener.Start();

        RulesTelemetry.RecordBatchSize(RulesOperation.BulkUpdate, 12);

        var tags = Assert.IsType<KeyValuePair<string, object?>[]>(capturedTags);
        Assert.Contains(tags, tag =>
            tag.Key == "imaging_pipeline.rules.operation" && Equals(tag.Value, "bulk_update"));
        Assert.DoesNotContain(tags, tag =>
            tag.Key is TelemetryAttributeNames.PipelineOutcome or "error.type");
    }

    [Fact]
    public void StructuredLog_ContainsActiveTraceAndOperationalScope()
    {
        var exporter = new CapturingLogExporter();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Traces:Enabled"] = "false",
            ["Observability:Metrics:Enabled"] = "false",
            ["Observability:Logs:ConsoleEnabled"] = "false",
            ["Observability:Otlp:Enabled"] = "false"
        });
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
        });

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<TelemetryContractTests>>();
        using var activity = new Activity("process").Start();
        using var scope = logger.BeginTelemetryScope(new TelemetryLogContext(
            MessageId: "message-1",
            TaskId: "task-1"));

        logger.LogInformation("Processed {ItemCount} items", 3);

        var record = Assert.Single(exporter.Records);
        Assert.Equal(activity.TraceId, record.TraceId);
        Assert.Equal(activity.SpanId, record.SpanId);
        Assert.Equal("message-1", record.Attributes["messaging.message.id"]);
        Assert.Equal("task-1", record.Attributes[TelemetryAttributeNames.PipelineTaskId]);
        Assert.Equal(3, record.Attributes["ItemCount"]);
        Assert.DoesNotContain("TraceId", record.Attributes.Keys);
        Assert.DoesNotContain("SpanId", record.Attributes.Keys);
        Assert.DoesNotContain("ParentId", record.Attributes.Keys);
        Assert.DoesNotContain("TraceFlags", record.Attributes.Keys);
    }

    private sealed record Measurement(
        string InstrumentName,
        KeyValuePair<string, object?>[] Tags);

    private sealed class CapturingLogExporter : BaseExporter<LogRecord>
    {
        public ConcurrentQueue<CapturedLog> Records { get; } = new();

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var attributes = record.Attributes?.ToDictionary(pair => pair.Key, pair => pair.Value)
                    ?? new Dictionary<string, object?>();
                record.ForEachScope(
                    static (scope, state) =>
                    {
                        if (scope.Scope is IEnumerable<KeyValuePair<string, object?>> values)
                        {
                            foreach (var value in values)
                            {
                                state[value.Key] = value.Value;
                            }
                        }
                    },
                    attributes);
                Records.Enqueue(new CapturedLog(
                    record.TraceId,
                    record.SpanId,
                    attributes));
            }

            return ExportResult.Success;
        }
    }

    private sealed record CapturedLog(
        ActivityTraceId TraceId,
        ActivitySpanId SpanId,
        IReadOnlyDictionary<string, object?> Attributes);
}
