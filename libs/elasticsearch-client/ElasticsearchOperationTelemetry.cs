using System.Diagnostics;
using System.Globalization;
using Elasticsearch.Net;
using ImagingPipeline.Observability;

namespace ImagingPipeline.ElasticsearchClient;

internal sealed class ElasticsearchOperationTelemetry : IDisposable
{
    private const string ElasticsearchSystemName = "elasticsearch";

    private readonly Activity? _activity;
    private readonly long _startedAt;
    private readonly DependencyOperation _operation;
    private int? _responseStatusCode;
    private string? _responseExceptionType;
    private bool _completed;

    private ElasticsearchOperationTelemetry(
        DependencyOperation operation,
        string? indexName,
        string? documentId,
        long? requestedDocumentCount)
    {
        _operation = operation;
        _startedAt = TelemetryTiming.StartTimestamp();
        var operationName = OperationName(operation);
        _activity = TelemetrySources.Elasticsearch.StartActivity(
            operationName,
            ActivityKind.Client);

        if (_activity?.IsAllDataRequested != true)
        {
            return;
        }

        // Elastic APM 8.15 still classifies database spans using the legacy key.
        // Keep the newer semantic-convention key as well for forward compatibility.
        _activity.SetTag("db.system", ElasticsearchSystemName);
        _activity.SetTag("db.system.name", ElasticsearchSystemName);
        _activity.SetTag("db.operation.name", operationName);
        if (!string.IsNullOrWhiteSpace(indexName))
        {
            _activity.SetTag("db.collection.name", indexName);
            _activity.DisplayName = SpanName(operationName, indexName);
        }

        if (!string.IsNullOrWhiteSpace(documentId))
        {
            _activity.SetTag("elasticsearch.document.id", documentId);
        }

        if (requestedDocumentCount.HasValue)
        {
            _activity.SetTag(
                "elasticsearch.request.document.count",
                Math.Max(0, requestedDocumentCount.Value));
        }
    }

    public static ElasticsearchOperationTelemetry Start(
        DependencyOperation operation,
        string? indexName = null,
        string? documentId = null,
        long? requestedDocumentCount = null) =>
        new(operation, indexName, documentId, requestedDocumentCount);

    public void SetIndex(string? indexName)
    {
        if (string.IsNullOrWhiteSpace(indexName) || _activity?.IsAllDataRequested != true)
        {
            return;
        }

        _activity.SetTag("db.collection.name", indexName);
        _activity.DisplayName = SpanName(OperationName(_operation), indexName);
    }

    public void SetDocumentId(string? documentId)
    {
        // Document identifiers are intentionally span-only. Never add this value to metrics.
        if (!string.IsNullOrWhiteSpace(documentId) && _activity?.IsAllDataRequested == true)
        {
            _activity.SetTag("elasticsearch.document.id", documentId);
        }
    }

    public void SetResponseMetadata(IApiCallDetails? response)
    {
        if (response is null)
        {
            return;
        }

        RecordKnownPayloadSizes(response);

        if (_activity?.IsAllDataRequested == true)
        {
            var httpMethod = response.HttpMethod.ToString();
            if (!string.IsNullOrWhiteSpace(httpMethod))
            {
                _activity.SetTag("http.request.method", httpMethod.ToUpperInvariant());
            }

            if (response.Uri is { IsAbsoluteUri: true } uri)
            {
                _activity.SetTag("url.full", BuildSafeUrl(uri));
                _activity.SetTag("server.address", uri.Host);
                if (uri.Port > 0)
                {
                    _activity.SetTag("server.port", uri.Port);
                }
            }
        }

        if (response.HttpStatusCode.HasValue)
        {
            _responseStatusCode = response.HttpStatusCode.Value;
            if (_activity?.IsAllDataRequested == true)
            {
                _activity.SetTag(
                    "db.response.status_code",
                    _responseStatusCode.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        _responseExceptionType = response.OriginalException?.GetType().FullName;
    }

    public void Complete(long documentCount)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        var count = Math.Max(0, documentCount);
        if (_activity?.IsAllDataRequested == true)
        {
            _activity.SetTag("elasticsearch.response.document.count", count);
        }

        if (TryGetErrorStatusCode(out var statusCode))
        {
            var category = ClassifyStatusCode(statusCode);
            var errorType = statusCode.ToString(CultureInfo.InvariantCulture);
            if (_activity?.IsAllDataRequested == true)
            {
                _activity.SetTag(TelemetryAttributeNames.PipelineOutcome, "failure");
            }

            _activity.SetTelemetryError(category);
            if (_activity?.IsAllDataRequested == true)
            {
                _activity.SetTag("error.type", errorType);
            }

            DependencyTelemetry.RecordOperation(
                DependencyName.Elasticsearch,
                _operation,
                TelemetryTiming.ElapsedSeconds(_startedAt),
                TelemetryOutcome.Failure,
                category);
        }
        else
        {
            if (_activity?.IsAllDataRequested == true)
            {
                _activity.SetTag(TelemetryAttributeNames.PipelineOutcome, "success");
            }

            _activity.SetTelemetrySuccess();
            DependencyTelemetry.RecordOperation(
                DependencyName.Elasticsearch,
                _operation,
                TelemetryTiming.ElapsedSeconds(_startedAt),
                TelemetryOutcome.Success);
        }

        DependencyTelemetry.RecordBatchSize(
            DependencyName.Elasticsearch,
            _operation,
            PipelineItem.Document,
            count);
    }

    public void Fail(Exception exception, CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        var (outcome, category) = TryGetErrorStatusCode(out var statusCode)
            ? (TelemetryOutcome.Failure, ClassifyStatusCode(statusCode))
            : ClassifyFailure(exception, cancellationToken);
        var errorType = _responseStatusCode?.ToString(CultureInfo.InvariantCulture) ??
            _responseExceptionType ??
            exception.GetType().FullName ??
            exception.GetType().Name;

        if (_activity?.IsAllDataRequested == true)
        {
            _activity.SetTag(TelemetryAttributeNames.PipelineOutcome, OutcomeName(outcome));
        }

        _activity.SetTelemetryError(category, exception);
        if (_activity?.IsAllDataRequested == true)
        {
            _activity.SetTag("error.type", errorType);
        }

        DependencyTelemetry.RecordOperation(
            DependencyName.Elasticsearch,
            _operation,
            TelemetryTiming.ElapsedSeconds(_startedAt),
            outcome,
            category);
    }

    public void Dispose() => _activity?.Dispose();

    private bool TryGetErrorStatusCode(out int statusCode)
    {
        statusCode = _responseStatusCode.GetValueOrDefault();
        return statusCode is >= 400 and <= 599;
    }

    private void RecordKnownPayloadSizes(IApiCallDetails response)
    {
        if (response.RequestBodyInBytes is { } requestBody)
        {
            DependencyTelemetry.RecordPayloadSize(
                DependencyName.Elasticsearch,
                _operation,
                PipelineDirection.Egress,
                requestBody.LongLength);
        }

        if (response.ResponseBodyInBytes is { } responseBody)
        {
            DependencyTelemetry.RecordPayloadSize(
                DependencyName.Elasticsearch,
                _operation,
                PipelineDirection.Ingress,
                responseBody.LongLength);
        }
    }

    private static TelemetryErrorCategory ClassifyStatusCode(int statusCode) => statusCode switch
    {
        408 => TelemetryErrorCategory.Timeout,
        429 => TelemetryErrorCategory.Unavailable,
        >= 500 => TelemetryErrorCategory.Unavailable,
        _ => TelemetryErrorCategory.Dependency
    };

    private static (TelemetryOutcome Outcome, TelemetryErrorCategory Category) ClassifyFailure(
        Exception exception,
        CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested =>
            (TelemetryOutcome.Cancelled, TelemetryErrorCategory.Cancelled),
        OperationCanceledException =>
            (TelemetryOutcome.Failure, TelemetryErrorCategory.Timeout),
        TimeoutException =>
            (TelemetryOutcome.Failure, TelemetryErrorCategory.Timeout),
        ElasticsearchClientException =>
            (TelemetryOutcome.Failure, TelemetryErrorCategory.Dependency),
        _ =>
            (TelemetryOutcome.Failure, TelemetryErrorCategory.Unknown)
    };

    private static string OperationName(DependencyOperation operation) => operation switch
    {
        DependencyOperation.Get => "get",
        DependencyOperation.Search => "search",
        DependencyOperation.Index => "index",
        DependencyOperation.Delete => "delete",
        _ => "operation"
    };

    private static string SpanName(string operationName, string? indexName) =>
        string.IsNullOrWhiteSpace(indexName)
            ? operationName
            : $"{operationName} {indexName}";

    private static string BuildSafeUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            // Query strings can contain Elasticsearch query text, signed credentials, or tokens.
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            builder.UserName = "REDACTED";
            builder.Password = "REDACTED";
        }

        return builder.Uri.AbsoluteUri;
    }

    private static string OutcomeName(TelemetryOutcome outcome) => outcome switch
    {
        TelemetryOutcome.Cancelled => "cancelled",
        TelemetryOutcome.Success => "success",
        _ => "failure"
    };
}
