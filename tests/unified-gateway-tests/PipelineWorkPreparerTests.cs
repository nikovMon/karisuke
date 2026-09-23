using System.Diagnostics;
using System.Text.Json;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.UnifiedGateway.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Catalog = ImagingPipeline.PipelineCatalog.PipelineCatalog;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class PipelineWorkPreparerTests
{
    [Fact]
    public void EnabledAsdPipelineBuildsItsExistingPayloadAndAttributes()
    {
        var pipeline = Definition("asd", "asd", enabled: true);
        var preparer = CreatePreparer([pipeline], new AsdPipelineContract());

        var result = preparer.Prepare("asd", Context(), AsdRunParams());

        Assert.Equal(PipelinePreparationStatus.Prepared, result.Status);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Work);
        Assert.Equal("asd", result.Work.Pipeline.PipelineId);
        Assert.Equal("rabbitmq", result.Work.Pipeline.Transport.Kind);
        Assert.Equal("application/json", result.Work.Payload.ContentType);
        Assert.Equal("FindAir", result.Work.Payload.Attributes["algorithmName"]);
        Assert.Equal("tenant-a", result.Work.Payload.Attributes["tenantId"]);
        using var body = JsonDocument.Parse(result.Work.Payload.Body);
        Assert.Equal("task-a", body.RootElement.GetProperty("taskId").GetString());
        Assert.Equal("image-a", body.RootElement.GetProperty("imageId").GetString());
        Assert.Equal("grid-a", body.RootElement.GetProperty("gridURI").GetString());
        Assert.Equal(500, body.RootElement.GetProperty("tilingConfigs")[0].GetProperty("tileSizeWidth").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("extraData", out _));
    }

    [Fact]
    public void ConfiguredExtraDataIsIsolatedPerPipelineWithoutOverridingInputOrRules()
    {
        var first = Definition("first", "asd", true) with
        {
            ExtraData = PipelineExtraData.Parse("""{"tenantId":"extra-tenant","imageId":"extra-image","enabled":true,"options":{"values":[1,"1",null]}}""")
        };
        var second = Definition("second", "asd", true) with
        {
            ExtraData = PipelineExtraData.Parse("""{"origin":"second"}"""),
            Transport = new() { Kind = "http", Http = new() { Endpoint = "https://example.invalid/work" } }
        };
        var preparer = CreatePreparer([first, second], new AsdPipelineContract());

        var firstResult = preparer.Prepare("first", Context(), AsdRunParams());
        var secondResult = preparer.Prepare("second", Context(), AsdRunParams());

        using var firstBody = JsonDocument.Parse(firstResult.Work!.Payload.Body);
        using var secondBody = JsonDocument.Parse(secondResult.Work!.Payload.Body);
        Assert.Equal("tenant-a", firstBody.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal("image-a", firstBody.RootElement.GetProperty("imageId").GetString());
        var extra = firstBody.RootElement.GetProperty("extraData");
        Assert.Equal("extra-tenant", extra.GetProperty("tenantId").GetString());
        Assert.True(extra.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Number, extra.GetProperty("options").GetProperty("values")[0].ValueKind);
        Assert.Equal(JsonValueKind.String, extra.GetProperty("options").GetProperty("values")[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, extra.GetProperty("options").GetProperty("values")[2].ValueKind);
        Assert.Equal("second", secondBody.RootElement.GetProperty("extraData").GetProperty("origin").GetString());
        Assert.Single(secondBody.RootElement.GetProperty("extraData").EnumerateObject());
        Assert.False(firstResult.Work.Payload.Attributes.ContainsKey("enabled"));
        Assert.False(secondResult.Work.Payload.Attributes.ContainsKey("origin"));
    }

    [Fact]
    public void ExtraDataIsPassedToAnyRegisteredContractSeparatelyFromRuleParameters()
    {
        var contract = new RecordingContract();
        var pipeline = Definition("custom", contract.ContractId, true) with
        {
            ExtraData = PipelineExtraData.Parse("""{"anyNewField":{"flag":false}}""")
        };
        var preparer = CreatePreparer([pipeline], contract);

        preparer.Prepare("custom", Context(), JsonSerializer.SerializeToElement(new { mode = "rule-value" }));

        var received = Assert.Single(contract.ReceivedExtraData);
        Assert.False(received.GetProperty("anyNewField").GetProperty("flag").GetBoolean());
        Assert.False(received.TryGetProperty("mode", out _));
    }

    [Fact]
    public void DisabledPipelineNeverValidatesOrBuildsWork()
    {
        var contract = new RecordingContract();
        var preparer = CreatePreparer([Definition("paused", contract.ContractId, false)], contract);

        var result = preparer.Prepare("paused", Context(), default);

        Assert.Equal(PipelinePreparationStatus.Disabled, result.Status);
        Assert.Null(result.Work);
        Assert.Empty(result.Errors);
        Assert.Equal(0, contract.ValidationCalls);
        Assert.Equal(0, contract.BuildCalls);
    }

    [Fact]
    public void InvalidRunParametersCannotProduceWork()
    {
        var preparer = CreatePreparer([Definition("asd", "asd", true)], new AsdPipelineContract());

        var result = preparer.Prepare("asd", Context(), JsonSerializer.SerializeToElement(new { tenantId = "" }));

        Assert.Equal(PipelinePreparationStatus.Invalid, result.Status);
        Assert.Null(result.Work);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void IntegrationPipelinesKeepTheirOwnIndexesContractsAndDestinations()
    {
        var contract = new RecordingContract();
        var asdPipeline = Definition("asd", "asd", true) with { RulesIndex = "asd-integ-pipeline-index" };
        var httpPipeline = Definition("http-example", contract.ContractId, true) with
        {
            RulesIndex = "http-example-integ-pipeline-index",
            Transport = new PipelineTransportOptions
            {
                Kind = "http",
                Http = new HttpTransportOptions { Endpoint = "https://example.invalid/work" }
            }
        };
        var preparer = CreatePreparer(
            [asdPipeline, httpPipeline],
            new AsdPipelineContract(), contract);

        var asdResult = preparer.Prepare("asd", Context(), AsdRunParams());
        var result = preparer.Prepare("http-example", Context(), JsonSerializer.SerializeToElement(new { mode = "analysis" }));

        Assert.Equal(PipelinePreparationStatus.Prepared, asdResult.Status);
        Assert.NotNull(asdResult.Work);
        Assert.Equal("asd", asdResult.Work.Pipeline.ContractId);
        Assert.Equal("rabbitmq", asdResult.Work.Pipeline.Transport.Kind);
        Assert.Equal("output", asdResult.Work.Pipeline.Transport.RabbitMq!.ConnectionRef);
        Assert.Equal("publisher", asdResult.Work.Pipeline.Transport.RabbitMq!.Output.QueueName);
        Assert.Null(asdResult.Work.Pipeline.Transport.Http);
        Assert.Equal("tenant-a", asdResult.Work.Payload.Attributes["tenantId"]);
        Assert.False(asdResult.Work.Payload.Attributes.ContainsKey("mode"));
        using var asdBody = JsonDocument.Parse(asdResult.Work.Payload.Body);
        Assert.Equal("FindAir", asdBody.RootElement.GetProperty("algorithmName")[0].GetString());
        Assert.Equal(PipelinePreparationStatus.Prepared, result.Status);
        Assert.NotNull(result.Work);
        Assert.Equal(contract.ContractId, result.Work.Pipeline.ContractId);
        Assert.Equal("http", result.Work.Pipeline.Transport.Kind);
        Assert.Equal("https://example.invalid/work", result.Work.Pipeline.Transport.Http!.Endpoint);
        Assert.Null(result.Work.Pipeline.Transport.RabbitMq);
        Assert.Equal("analysis", result.Work.Payload.Attributes["mode"]);
        Assert.False(result.Work.Payload.Attributes.ContainsKey("tenantId"));
        Assert.Equal("asd-integ-pipeline-index", asdResult.Work.Pipeline.RulesIndex);
        Assert.Equal("http-example-integ-pipeline-index", result.Work.Pipeline.RulesIndex);
        var wrongParameters = preparer.Prepare("asd", Context(), JsonSerializer.SerializeToElement(new { mode = "analysis" }));
        Assert.Equal(PipelinePreparationStatus.Invalid, wrongParameters.Status);
        Assert.Null(wrongParameters.Work);
        Assert.Equal(1, contract.ValidationCalls);
        Assert.Equal(1, contract.BuildCalls);
    }

    [Fact]
    public void UnknownPipelineDoesNotFallBackToAsd()
    {
        var preparer = CreatePreparer([Definition("asd", "asd", true)], new AsdPipelineContract());

        Assert.Throws<KeyNotFoundException>(() => preparer.Prepare("unknown", Context(), AsdRunParams()));
    }

    [Fact]
    public void PreparationEmitsPipelineAndOutcomeOnRegisteredTraceSource()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySourceNames.UnifiedGateway,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);
        var preparer = CreatePreparer([Definition("asd", "asd", true)], new AsdPipelineContract());

        preparer.Prepare("asd", Context(), AsdRunParams());

        Assert.NotNull(captured);
        Assert.Equal("asd", captured.GetTagItem("pipeline.id"));
        Assert.Equal("prepared", captured.GetTagItem("pipeline.outcome"));
        Assert.Contains(TelemetrySourceNames.UnifiedGateway, TelemetrySourceNames.All);
    }

    private static PipelineWorkPreparer CreatePreparer(PipelineDefinition[] definitions, params IPipelineContract[] contracts)
    {
        var registry = new PipelineContractRegistry(contracts);
        var catalog = new Catalog(
            Options.Create(new PipelineCatalogOptions
            {
                RabbitMqConnections = new Dictionary<string, RabbitMqConnectionOptions>
                {
                    ["output"] = new()
                    {
                        Hostname = "localhost", Username = "test", Password = "test", VirtualHost = "/"
                    }
                },
                Pipelines = [.. definitions]
            }),
            registry,
            NullLogger<Catalog>.Instance);
        return new(catalog, registry, NullLogger<PipelineWorkPreparer>.Instance);
    }

    private static PipelineDefinition Definition(string id, string contractId, bool enabled) => new()
    {
        PipelineId = id,
        ContractId = contractId,
        Enabled = enabled,
        RulesIndex = $"{id}-pipeline-index",
        Transport = new PipelineTransportOptions
        {
            Kind = "rabbitmq",
            RabbitMq = new RabbitMqTransportOptions
            {
                ConnectionRef = "output",
                Output = new RabbitMqQueueOptions { QueueName = "publisher" }
            }
        }
    };

    private static PipelineDispatchContext Context() => new(
        "task-a", "rule-a", "image-a",
        JsonSerializer.SerializeToElement(new { type = "Polygon", coordinates = new double[][][] { [[0, 0], [1, 0], [1, 1], [0, 0]] } }),
        DateTimeOffset.Parse("2026-09-17T10:00:00Z"),
        "EO", "https://example.invalid/image", 1000, 1000, 0.5,
        "sensor-a", null, "grid", "grid-a");

    private static JsonElement AsdRunParams() => JsonSerializer.SerializeToElement(new
    {
        tenantId = "tenant-a",
        algorithmNames = new[] { "FindAir" },
        tilingConfigs = new[] { new { tileSizeWidth = 500, tileSizeHeight = 500, tileOverlapWidth = 10, tileOverlapHeight = 10 } }
    });

    private sealed class RecordingContract : IPipelineContract
    {
        public string ContractId => "example";
        public int ValidationCalls { get; private set; }
        public int BuildCalls { get; private set; }
        public List<JsonElement> ReceivedExtraData { get; } = [];

        public IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams)
        {
            ValidationCalls++;
            return [];
        }

        public PipelinePayload BuildPayload(PipelineDispatchContext context, JsonElement runParams, JsonElement extraData = default)
        {
            BuildCalls++;
            ReceivedExtraData.Add(extraData.Clone());
            return new("{}"u8.ToArray(), "application/json", new Dictionary<string, string> { ["mode"] = runParams.GetProperty("mode").GetString()! });
        }
    }
}
