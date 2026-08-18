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
            ["Observability:Traces:OtlpEnabled"] = "false"
        });
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);

        using var host = builder.Build();
        host.Start();
        using var activity = TelemetrySources.Gateway.StartActivity("sampling-test");

        Assert.True(activity is null || !activity.IsAllDataRequested);
    }

    [Fact]
    public void MissingSamplerEnvironment_DefaultsToParentBasedAlwaysOn()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_TRACES_SAMPLER"] = string.Empty,
            ["Observability:Traces:OtlpEnabled"] = "false",
            ["Observability:Metrics:Enabled"] = "false",
            ["Observability:Logs:Enabled"] = "false"
        });
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);

        using var host = builder.Build();
        host.Start();
        using var activity = TelemetrySources.Gateway.StartActivity("default-sampling-test");

        Assert.NotNull(activity);
        Assert.True(activity.Recorded);
    }

    [Fact]
    public void EnabledTraceExporterWithoutEndpoint_FailsWithConfigurationKeys()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
            ["Observability:Metrics:Enabled"] = "false",
            ["Observability:Logs:Enabled"] = "false"
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway));

        Assert.Contains(
            "OTEL_EXPORTER_OTLP_ENDPOINT",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledTraceExporterWithoutProtocol_FailsWithConfigurationKeys()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://collector:4317",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = string.Empty,
            ["Observability:Metrics:Enabled"] = "false",
            ["Observability:Logs:Enabled"] = "false"
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway));

        Assert.Contains(
            "OTEL_EXPORTER_OTLP_PROTOCOL",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("grpc")]
    [InlineData("http/protobuf")]
    public void EnabledTraceExporter_AcceptsSupportedProtocols(string protocol)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://collector:4317",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol,
            ["Observability:Metrics:Enabled"] = "false",
            ["Observability:Logs:Enabled"] = "false"
        });

        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);
        using var host = builder.Build();
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
    public void MessagingMetricsDoNotDeclareARoutingKeyDimension()
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
            512,
            0.02,
            TelemetryOutcome.Success);
        Assert.NotEmpty(measurements);
        Assert.DoesNotContain(
            measurements.SelectMany(item => item.Tags),
            tag => tag.Key.Contains("routing_key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StageDurationDoesNotClaimAnIngressDirection()
    {
        KeyValuePair<string, object?>[]? capturedTags = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == TelemetryMetricNames.PipelineStageDuration)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => capturedTags = tags.ToArray());
        listener.Start();

        PipelineTelemetry.RecordStageDuration(
            PipelineStage.Gateway,
            0.25,
            TelemetryOutcome.Failure,
            TelemetryErrorCategory.Handler);

        var tags = Assert.IsType<KeyValuePair<string, object?>[]>(capturedTags);
        Assert.Contains(tags, tag =>
            tag.Key == TelemetryAttributeNames.PipelineStage && Equals(tag.Value, "gateway"));
        Assert.Contains(tags, tag =>
            tag.Key == TelemetryAttributeNames.PipelineOutcome && Equals(tag.Value, "failure"));
        Assert.Contains(tags, tag =>
            tag.Key == "error.type" && Equals(tag.Value, "handler"));
        Assert.DoesNotContain(tags, tag => tag.Key == TelemetryAttributeNames.PipelineDirection);
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
            tag.Key == "findair.rules.operation" && Equals(tag.Value, "bulk_update"));
        Assert.DoesNotContain(tags, tag =>
            tag.Key is TelemetryAttributeNames.PipelineOutcome or "error.type");
    }

    [Fact]
    public void WorkloadMetricsUseBoundedBusinessDimensionsAndExcludeExecutionIds()
    {
        var measurements = new ConcurrentBag<Measurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name is
                    TelemetryMetricNames.Images or
                    TelemetryMetricNames.MatchedImages or
                    TelemetryMetricNames.Tasks or
                    TelemetryMetricNames.TileRequests or
                    TelemetryMetricNames.TileBatches or
                    TelemetryMetricNames.Tiles or
                    TelemetryMetricNames.TilesReceived or
                    TelemetryMetricNames.TilePublishAttempts)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, tags.ToArray())));
        listener.Start();

        WorkloadTelemetry.RecordImage(TelemetryOutcome.Success, null, "sensor-x");
        WorkloadTelemetry.RecordMatchedImage("Israel", "sensor-x");
        WorkloadTelemetry.RecordTask(
            PipelineDirection.Egress,
            TelemetryOutcome.Success,
            "rule-1",
            "tenant-1",
            "Israel",
            "sensor-x",
            "FindAir,Rpn");
        WorkloadTelemetry.RecordTileRequest(
            TelemetryOutcome.Success,
            "rule-1",
            "tenant-1",
            "Israel",
            "sensor-x",
            "FindAir,Rpn");
        WorkloadTelemetry.RecordTileBatch(
            TelemetryOutcome.Success,
            "rule-1",
            "tenant-1",
            "Israel",
            "sensor-x",
            "FindAir,Rpn");
        WorkloadTelemetry.RecordTiles(
            TelemetryOutcome.Success,
            "rule-1",
            "tenant-1",
            "Israel",
            "sensor-x",
            "FindAir,Rpn",
            512,
            512,
            2);
        WorkloadTelemetry.RecordTilesReceived(
            "rule-1",
            "tenant-1",
            "Israel",
            "sensor-x",
            "FindAir,Rpn",
            4);
        WorkloadTelemetry.RecordTilePublishAttempt(
            TelemetryOutcome.Success,
            "rule-1",
            "tenant-1",
            "Israel",
            "sensor-x",
            "FindAir,Rpn");

        Assert.Equal(8, measurements.Count);
        var tags = measurements.SelectMany(measurement => measurement.Tags).ToArray();
        Assert.Contains(tags, tag =>
            tag.Key == TelemetryAttributeNames.AreaName && Equals(tag.Value, "Israel"));        Assert.Contains(tags, tag =>
            tag.Key == TelemetryAttributeNames.AreaName && Equals(tag.Value, "unknown"));

        Assert.Contains(tags, tag =>
            tag.Key == TelemetryAttributeNames.SensorName && Equals(tag.Value, "sensor-x"));
        Assert.Contains(tags, tag =>
            tag.Key == TelemetryAttributeNames.PipelineRuleId && Equals(tag.Value, "rule-1"));
        Assert.Contains(tags, tag =>
            tag.Key == TelemetryAttributeNames.PipelineTenantId && Equals(tag.Value, "tenant-1"));
        Assert.DoesNotContain(tags, tag =>
            tag.Key is
                TelemetryAttributeNames.PipelineTaskId or
                TelemetryAttributeNames.PipelineRequestId or
                TelemetryAttributeNames.PipelineImageId or
                TelemetryAttributeNames.TileId or
                TelemetryAttributeNames.TileIndex);
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
            ["Observability:Logs:Enabled"] = "false"
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
