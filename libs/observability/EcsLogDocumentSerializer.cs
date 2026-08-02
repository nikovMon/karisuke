using System.Globalization;
using System.Text.Json;

namespace ImagingPipeline.Observability;

internal static class EcsLogDocumentSerializer
{
    private const string EcsVersion = "8.11.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, string> CanonicalFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MessageId"] = "messaging.message.id",
            ["CorrelationId"] = "messaging.message.conversation_id",
            ["Destination"] = "messaging.destination.name",
            ["RetryAttempt"] = TelemetryAttributeNames.RetryAttempt,
            ["TaskId"] = TelemetryAttributeNames.PipelineTaskId,
            ["RequestId"] = TelemetryAttributeNames.PipelineRequestId,
            ["ImageId"] = TelemetryAttributeNames.PipelineImageId,
            ["RuleId"] = TelemetryAttributeNames.PipelineRuleId,
            ["TenantId"] = TelemetryAttributeNames.PipelineTenantId,
            ["AlgorithmName"] = TelemetryAttributeNames.PipelineAlgorithmName,
            ["RequestMethod"] = "http.request.method",
            ["HttpMethod"] = "http.request.method",
            ["RequestPath"] = "url.path",
            ["RequestRoute"] = "http.route",
            ["Uri"] = "url.full",
            ["Url"] = "url.full",
            ["StatusCode"] = "http.response.status_code",
            ["HttpStatusCode"] = "http.response.status_code",
            ["Operation"] = "event.action",
            ["messaging.message.id"] = "messaging.message.id",
            ["messaging.message.conversation_id"] = "messaging.message.conversation_id",
            ["messaging.destination.name"] = "messaging.destination.name",
            [TelemetryAttributeNames.RetryAttempt] = TelemetryAttributeNames.RetryAttempt,
            [TelemetryAttributeNames.PipelineTaskId] = TelemetryAttributeNames.PipelineTaskId,
            [TelemetryAttributeNames.PipelineRequestId] = TelemetryAttributeNames.PipelineRequestId,
            [TelemetryAttributeNames.PipelineImageId] = TelemetryAttributeNames.PipelineImageId,
            [TelemetryAttributeNames.PipelineRuleId] = TelemetryAttributeNames.PipelineRuleId,
            [TelemetryAttributeNames.PipelineTenantId] = TelemetryAttributeNames.PipelineTenantId,
            [TelemetryAttributeNames.PipelineAlgorithmName] = TelemetryAttributeNames.PipelineAlgorithmName
        };

    public static byte[] SerializeBatch(
        IReadOnlyList<EcsLogEvent> events,
        ObservabilityResourceIdentity resource,
        EcsLogDataStreamOptions dataStream)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(dataStream);

        var documents = new List<IReadOnlyDictionary<string, object?>>(events.Count);
        foreach (var logEvent in events)
        {
            documents.Add(BuildDocument(logEvent, resource, dataStream));
        }

        return JsonSerializer.SerializeToUtf8Bytes(documents, JsonOptions);
    }

    private static IReadOnlyDictionary<string, object?> BuildDocument(
        EcsLogEvent logEvent,
        ObservabilityResourceIdentity resource,
        EcsLogDataStreamOptions dataStream)
    {
        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["@timestamp"] = logEvent.Timestamp.UtcDateTime.ToString(
                "O",
                CultureInfo.InvariantCulture),
            ["message"] = logEvent.Message
        };

        SetPath(document, "ecs.version", EcsVersion);
        SetPath(document, "data_stream.type", "logs");
        SetPath(document, "data_stream.dataset", dataStream.Dataset);
        SetPath(document, "data_stream.namespace", dataStream.Namespace);

        SetPath(document, "service.name", resource.ServiceName);
        SetPath(document, "service.namespace", resource.ServiceNamespace);
        SetPath(document, "service.version", resource.ServiceVersion);
        SetPath(document, "service.environment", resource.DeploymentEnvironment);
        SetPath(document, "service.instance.id", resource.ServiceInstanceId);
        SetIfPresent(document, "service.node.name", resource.PodName);

        SetPath(document, "host.name", resource.HostName);
        SetPath(document, "process.pid", resource.ProcessId);
        SetPath(document, "process.thread.id", logEvent.ThreadId);
        SetPath(document, "service.language.name", "dotnet");
        SetPath(document, "service.language.version", resource.RuntimeVersion);
        SetPath(document, "service.runtime.name", resource.RuntimeName);
        SetPath(document, "service.runtime.version", resource.RuntimeVersion);

        SetIfPresent(document, "kubernetes.namespace", resource.PodNamespace);
        SetIfPresent(document, "kubernetes.pod.name", resource.PodName);
        SetIfPresent(document, "kubernetes.pod.uid", resource.PodUid);
        SetIfPresent(document, "kubernetes.deployment.name", resource.DeploymentName);
        SetIfPresent(document, "kubernetes.node.name", resource.NodeName);
        SetIfPresent(document, "kubernetes.container.name", resource.ContainerName);

        SetIfPresent(document, "orchestrator.cluster.name", resource.ClusterName);
        if (!string.IsNullOrWhiteSpace(resource.ClusterName))
        {
            SetPath(document, "orchestrator.type", "kubernetes");
        }

        SetPath(document, "event.kind", "event");
        SetPath(document, "event.dataset", dataStream.Dataset);
        SetPath(document, "event.code", logEvent.EventId.Id.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(logEvent.EventId.Name))
        {
            SetPath(document, "event.action", logEvent.EventId.Name);
        }

        SetPath(document, "log.level", logEvent.Level.ToString().ToLowerInvariant());
        SetPath(document, "log.logger", logEvent.Category);

        SetIfPresent(document, "trace.id", logEvent.TraceId);
        SetIfPresent(document, "span.id", logEvent.SpanId);

        if (logEvent.ErrorType is not null)
        {
            SetPath(document, "error.type", logEvent.ErrorType);
            SetIfPresent(document, "error.message", logEvent.ErrorMessage);
            SetIfPresent(document, "error.stack_trace", logEvent.ErrorStackTrace);
        }

        foreach (var attribute in logEvent.Attributes)
        {
            var path = CanonicalPath(attribute.Key);
            if (path is null)
            {
                continue;
            }

            SetPath(document, path, attribute.Value);
        }

        return document;
    }

    private static string? CanonicalPath(string key)
    {
        if (string.IsNullOrWhiteSpace(key)
            || key.Equals("{OriginalFormat}", StringComparison.Ordinal)
            || key.Equals("OriginalFormat", StringComparison.OrdinalIgnoreCase)
            || key.Equals("TraceId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SpanId", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (CanonicalFields.TryGetValue(key, out var canonical))
        {
            return canonical;
        }


        return $"labels.{EcsLogValueNormalizer.ToSnakeCase(key)}";
    }

    private static void SetIfPresent(
        IDictionary<string, object?> document,
        string path,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            SetPath(document, path, value);
        }
    }

    private static void SetPath(
        IDictionary<string, object?> document,
        string path,
        object? value)
    {
        if (value is null)
        {
            return;
        }

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        var current = document;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            if (!current.TryGetValue(segment, out var existing)
                || existing is not IDictionary<string, object?> child)
            {
                child = new Dictionary<string, object?>(StringComparer.Ordinal);
                current[segment] = child;
            }

            current = child;
        }

        current[segments[^1]] = value;
    }
}
