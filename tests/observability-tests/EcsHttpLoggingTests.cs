using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace ImagingPipeline.Observability.Tests;

public sealed class EcsHttpLoggingTests
{
    [Fact]
    public async Task ProviderAndSerializer_FlattenScopesAndProduceReadableEcsJson()
    {
        var options = CreateOptions();
        var buffer = new EcsLogBuffer(options);
        using var provider = new EcsHttpLoggerProvider(buffer, options);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("ImagingPipeline.Tests.Component");

        using var activity = new Activity("process").Start();
        using var telemetryScope = logger.BeginTelemetryScope(new TelemetryLogContext(
            MessageId: "message-1",
            CorrelationId: "correlation-1",
            TaskId: "task-1",
            TenantId: "tenant-1",
            AreaName: "Israel",
            SensorName: "sensor-x",
            TileId: "tile-1",
            TileIndex: 7));
        using var metadataScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["Uri"] = new Uri("https://projection.example/i2g?token=secret"),
            ["RabbitHeader"] = Encoding.UTF8.GetBytes("header-value"),
            ["service.name"] = "untrusted-override"
        });

        logger.LogInformation(new EventId(42, "message_processed"), "Processed {ItemCount} items", 3);

        var logEvent = await buffer.ReadAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(logEvent);

        var payload = EcsLogDocumentSerializer.SerializeBatch(
            [logEvent],
            CreateResource(),
            new EcsLogDataStreamOptions("findair", "production"));
        using var json = JsonDocument.Parse(payload);
        var document = json.RootElement[0];

        Assert.Equal("8.11.0", document.GetProperty("ecs").GetProperty("version").GetString());
        Assert.Equal("information", document.GetProperty("log").GetProperty("level").GetString());
        Assert.Equal("findair", document.GetProperty("event").GetProperty("dataset").GetString());
        Assert.Equal("logs", document.GetProperty("data_stream").GetProperty("type").GetString());
        Assert.Equal("findair", document.GetProperty("data_stream").GetProperty("dataset").GetString());
        Assert.Equal("production", document.GetProperty("data_stream").GetProperty("namespace").GetString());
        Assert.Equal("findair-gateway", document.GetProperty("service").GetProperty("name").GetString());
        Assert.Equal("production", document.GetProperty("service").GetProperty("environment").GetString());
        Assert.Equal("findair", document.GetProperty("service").GetProperty("namespace").GetString());
        Assert.Equal("pod-uid", document.GetProperty("service").GetProperty("instance").GetProperty("id").GetString());
        Assert.Equal("gateway-1", document.GetProperty("service").GetProperty("node").GetProperty("name").GetString());
        Assert.Equal("cluster-1", document.GetProperty("orchestrator").GetProperty("cluster").GetProperty("name").GetString());
        Assert.Equal("kubernetes", document.GetProperty("orchestrator").GetProperty("type").GetString());
        Assert.Equal(
            "untrusted-override",
            document.GetProperty("labels").GetProperty("service_name").GetString());

        Assert.Equal(activity.TraceId.ToHexString(), document.GetProperty("trace").GetProperty("id").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), document.GetProperty("span").GetProperty("id").GetString());
        Assert.Equal(
            "message-1",
            document.GetProperty("messaging").GetProperty("message").GetProperty("id").GetString());
        Assert.Equal(
            "task-1",
            document.GetProperty("findair").GetProperty("task").GetProperty("id").GetString());
        Assert.Equal(
            "Israel",
            document.GetProperty("findair").GetProperty("area").GetProperty("name").GetString());
        Assert.Equal(
            "sensor-x",
            document.GetProperty("findair").GetProperty("sensor").GetProperty("name").GetString());
        Assert.Equal(
            "tile-1",
            document.GetProperty("findair").GetProperty("tile").GetProperty("id").GetString());
        Assert.Equal(
            7,
            document.GetProperty("findair").GetProperty("tile").GetProperty("index").GetInt32());
        Assert.False(document.TryGetProperty("host", out _));
        Assert.False(document.TryGetProperty("process", out _));
        Assert.Equal(
            "https://projection.example/i2g",
            document.GetProperty("url").GetProperty("full").GetString()?.TrimEnd('/'));
        Assert.Equal(
            "header-value",
            document.GetProperty("labels").GetProperty("rabbit_header").GetString());

        var jsonText = Encoding.UTF8.GetString(payload);
        Assert.DoesNotContain("{OriginalFormat}", jsonText, StringComparison.Ordinal);
        Assert.DoesNotContain("TelemetryLogContext", jsonText, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Byte[]", jsonText, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", jsonText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exporter_RetriesRetryableResponseAndSendsJsonArray()
    {
        var options = CreateOptions() with
        {
            FlushInterval = TimeSpan.FromMilliseconds(25),
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
            MaxRetryAttempts = 2
        };
        var buffer = new EcsLogBuffer(options);
        var handler = new RecordingHandler();
        using var exporter = new EcsLogstashExporterService(
            buffer,
            options,
            CreateResource(),
            new EcsLogDataStreamOptions("findair", "production"),
            handler);

        await exporter.StartAsync(CancellationToken.None);
        Assert.True(buffer.TryWrite(CreateEvent("first")));
        Assert.True(buffer.TryWrite(CreateEvent("second")));

        var payload = await handler.SuccessfulPayload.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await exporter.StopAsync(CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.True(handler.InstrumentationWasSuppressed);
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(2, json.RootElement.GetArrayLength());
    }
    [Fact]
    public async Task Exporter_StopFlushesPartialBatchWithoutWaitingForTimeout()
    {
        var options = CreateOptions() with
        {
            FlushInterval = TimeSpan.FromSeconds(30),
            BatchSize = 10,
            MaxRetryAttempts = 0
        };
        var buffer = new EcsLogBuffer(options);
        var handler = new RecordingHandler(failuresBeforeSuccess: 0);
        using var exporter = new EcsLogstashExporterService(
            buffer,
            options,
            CreateResource(),
            new EcsLogDataStreamOptions("findair", "production"),
            handler);

        await exporter.StartAsync(CancellationToken.None);
        Assert.True(buffer.TryWrite(CreateEvent("partial")));
        await exporter.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        var payload = await handler.SuccessfulPayload.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, handler.RequestCount);
        Assert.True(handler.InstrumentationWasSuppressed);
        using var json = JsonDocument.Parse(payload);
        Assert.Single(json.RootElement.EnumerateArray());
    }


    [Fact]
    public async Task Exporter_UnexpectedFailureRestartsAndProcessesLaterBatch()
    {
        var options = CreateOptions() with
        {
            BatchSize = 1,
            FlushInterval = TimeSpan.FromMilliseconds(10),
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
            MaxRetryAttempts = 0
        };
        var buffer = new EcsLogBuffer(options);
        var handler = new RecordingHandler(failuresBeforeSuccess: 0, throwFirst: true);
        using var exporter = new EcsLogstashExporterService(
            buffer,
            options,
            CreateResource(),
            new EcsLogDataStreamOptions("findair", "production"),
            handler);

        await exporter.StartAsync(CancellationToken.None);
        Assert.True(buffer.TryWrite(CreateEvent("first")));
        await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(buffer.TryWrite(CreateEvent("second")));
        var payload = await handler.SuccessfulPayload.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await exporter.StopAsync(CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("second", json.RootElement[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProviderSpecificFilter_KeepsDebugOutOfLogstash()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Traces:Enabled"] = "false",
            ["Observability:Metrics:Enabled"] = "false",
            ["Observability:Logs:Enabled"] = "true",
            ["Observability:Logs:ConsoleEnabled"] = "false",
            ["Observability:Logs:Logstash:Enabled"] = "true",
            ["Observability:Logs:Logstash:Endpoint"] = "http://logstash:8081",
            ["Logging:LogLevel:Default"] = "Debug",
            ["Logging:EcsHttp:LogLevel:Default"] = "Information"
        });
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<EcsHttpLoggingTests>>();
        var buffer = host.Services.GetRequiredService<EcsLogBuffer>();

        logger.LogDebug("console-only debug");
        logger.LogInformation("logstash information");

        var logEvent = await buffer.ReadAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(logEvent);
        Assert.Equal(LogLevel.Information, logEvent.Level);
        Assert.Equal("logstash information", logEvent.Message);
        Assert.False(buffer.TryRead(out _));
    }

    [Fact]
    public void Configuration_MissingLogstashEndpointFailsWithIndicativeMessage()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => LogstashHttpOptions.Read(configuration));

        Assert.Contains(
            "Observability:Logs:Logstash:Endpoint",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Data-Set", "production")]
    [InlineData("findair", "prod-west")]
    public void Configuration_InvalidDataStreamNameFails(string dataset, string dataStreamNamespace)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:Logs:DataStream:Dataset"] = dataset,
                ["Observability:Logs:DataStream:Namespace"] = dataStreamNamespace
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => LogstashHttpOptions.ReadDataStream(configuration, "production"));
    }

    [Fact]
    public void Configuration_DefaultDataStreamUsesFindAirAndDeploymentEnvironment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();

        var dataStream = LogstashHttpOptions.ReadDataStream(configuration, "integration");

        Assert.Equal("findair", dataStream.Dataset);
        Assert.Equal("integration", dataStream.Namespace);
        Assert.Equal("findair", new ObservabilityLogDataStreamOptions().Dataset);
    }

    [Fact]
    public void Configuration_ExplicitDataStreamNamespaceOverridesDeploymentEnvironment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:Logs:DataStream:Dataset"] = "findair",
                ["Observability:Logs:DataStream:Namespace"] = "development"
            })
            .Build();

        var dataStream = LogstashHttpOptions.ReadDataStream(configuration, "integration");

        Assert.Equal("findair", dataStream.Dataset);
        Assert.Equal("development", dataStream.Namespace);
    }

    private static LogstashHttpOptions CreateOptions() => new(
        new Uri("http://logstash:8081/"),
        QueueCapacity: 100,
        PriorityQueueCapacity: 10,
        BatchSize: 10,
        FlushInterval: TimeSpan.FromMilliseconds(50),
        RequestTimeout: TimeSpan.FromSeconds(1),
        MaxRetryAttempts: 1,
        RetryBaseDelay: TimeSpan.FromMilliseconds(10),
        ShutdownFlushTimeout: TimeSpan.FromSeconds(2),
        MaxAttributeCount: 64,
        MaxCollectionCount: 32,
        MaxStringLength: 8_192);

    private static ObservabilityResourceIdentity CreateResource() => new(
        ServiceName: "findair-gateway",
        ServiceNamespace: "findair",
        ServiceVersion: "1.2.3",
        DeploymentEnvironment: "production",
        ServiceInstanceId: "pod-uid",
        PodName: "gateway-1",
        PodUid: "pod-uid",
        PodNamespace: "pipeline",
        DeploymentName: "gateway",
        NodeName: "node-1",
        ContainerName: "gateway")
    {
        ClusterName = "cluster-1"
    };

    private static EcsLogEvent CreateEvent(string message) => new(
        DateTimeOffset.UtcNow,
        LogLevel.Information,
        "ImagingPipeline.Tests",
        new EventId(1, "test"),
        message,
        null,
        null,
        null,
        null,
        null,
        Environment.CurrentManagedThreadId,
        new Dictionary<string, object?>());

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly int _failuresBeforeSuccess;
        private readonly bool _throwFirst;
        private int _requestCount;
        private int _suppressionObserved;

        public RecordingHandler(int failuresBeforeSuccess = 1, bool throwFirst = false)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
            _throwFirst = throwFirst;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);
        public bool InstrumentationWasSuppressed => Volatile.Read(ref _suppressionObserved) != 0;
        public TaskCompletionSource FirstRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<byte[]> SuccessfulPayload { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Sdk.SuppressInstrumentation)
            {
                Interlocked.Exchange(ref _suppressionObserved, 1);
            }

            var payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var count = Interlocked.Increment(ref _requestCount);
            FirstRequest.TrySetResult();
            if (_throwFirst && count == 1)
            {
                throw new InvalidOperationException("Injected exporter failure.");
            }

            if (count <= _failuresBeforeSuccess)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            SuccessfulPayload.TrySetResult(payload);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
