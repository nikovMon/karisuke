using System.Net;
using System.Text.Json;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.UnifiedGateway.Dispatch;
using ImagingPipeline.UnifiedGateway.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Catalog = ImagingPipeline.PipelineCatalog.PipelineCatalog;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class ExtraDataDispatchTests
{
    [Fact]
    public void RegisteredContractsResolveRenamedIds()
    {
        using var provider = new ServiceCollection().AddPipelineContracts().BuildServiceProvider();
        var registry = provider.GetRequiredService<IPipelineContractRegistry>();

        Assert.IsType<FindAirPipelineContract>(registry.GetRequired("FindAir"));
        Assert.IsType<DeepWisdomPipelineContract>(registry.GetRequired("DeepWisdom"));
    }

    [Theory]
    [InlineData("FindAir", PipelineTransportKind.RabbitMq)]
    [InlineData("FindAir", PipelineTransportKind.Http)]
    [InlineData("DeepWisdom", PipelineTransportKind.RabbitMq)]
    [InlineData("DeepWisdom", PipelineTransportKind.Http)]
    public async Task ArbitraryExtraDataReachesTheConfiguredTransportUnchanged(string contractId, PipelineTransportKind kind)
    {
        using var provider = new ServiceCollection().AddPipelineContracts().BuildServiceProvider();
        var contracts = provider.GetRequiredService<IPipelineContractRegistry>();
        var extraData = PipelineExtraData.Parse("""
            {"Name":"upper","name":"lower","key:part":{"label":"שלום 🙂 漢字"},
             "items":[null,true,false,{},[],{"large":9007199254740993}],
             "fraction":0.12345678901234567890123456789,"optional":null,"custom":42}
            """);
        var catalog = new Catalog(Options.Create(new PipelineCatalogOptions
        {
            RabbitMqConnections = new(StringComparer.Ordinal)
            {
                ["output"] = new() { Hostname = "broker.invalid", Username = "user", Password = "password" }
            },
            Pipelines = new(StringComparer.Ordinal)
            {
                [contractId] = new()
                {
                    Enabled = true,
                    ContractId = contractId,
                    RulesIndex = "extra-data-test-index",
                    ExtraData = extraData,
                    Transport = kind == PipelineTransportKind.RabbitMq
                        ? new()
                        {
                            Kind = kind,
                            RabbitMq = new() { ConnectionRef = "output", Output = new() { QueueName = "pipeline.output" } }
                        }
                        : new() { Kind = kind, Http = new() { Endpoint = "https://pipeline.invalid/work" } }
                }
            }
        }), contracts, NullLogger<Catalog>.Instance);
        var runParams = RunParams(contractId);
        var dispatchId = DispatchIdentity.Create("image", contractId, "rule", runParams);
        var context = OverlayDispatchContextFactory.Create(dispatchId, "rule", Json("""
            {"id":"image","photoTime":"2026-10-13T08:00:00Z","sensorName":"camera","sensorType":"EO",
             "bestResolution":0.7,"imageUrl":"/image.tiff","width":100,"height":100,"gridType":"grid","gridURI":"/grid.json",
             "roiFootprint":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}
            """)) with { RuleLocationWkt = "POLYGON ((0 0, 1 0, 1 1, 0 0))" };
        var prepared = new PipelineWorkPreparer(catalog, contracts, NullLogger<PipelineWorkPreparer>.Instance)
            .Prepare(contractId, context, runParams);
        Assert.Equal(PipelinePreparationStatus.Prepared, prepared.Status);
        Assert.NotNull(prepared.Work);
        var unit = new DispatchUnit(dispatchId, prepared.Work, "source", null);
        var publisher = new RecordingPublisher();
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        IDispatchTransport transport = kind == PipelineTransportKind.RabbitMq
            ? new RabbitMqDispatchTransport(catalog, catalog, publisher)
            : new HttpDispatchTransport(catalog, new ClientFactory(client));

        var outcome = await transport.SendAsync(unit, CancellationToken.None);

        Assert.Equal(DispatchStatus.Delivered, outcome.Status);
        byte[] received;
        if (kind == PipelineTransportKind.RabbitMq)
        {
            var envelope = Assert.Single(publisher.Messages);
            received = envelope.Body;
            Assert.DoesNotContain("custom", envelope.Headers!.Keys);
        }
        else
        {
            Assert.NotNull(handler.Body);
            received = handler.Body;
            Assert.DoesNotContain("custom", handler.HeaderNames);
        }
        Assert.Equal(prepared.Work.Payload.Body, received);
        using var body = JsonDocument.Parse(received);
        Assert.True(JsonElement.DeepEquals(extraData.Value, body.RootElement.GetProperty("extraData")));
    }

    private static JsonElement RunParams(string id) => Json(id == "FindAir" ? """
        {"tenantId":"tenant","algorithmNames":["FindAir"],
         "tilingConfigs":[{"tileSizeWidth":512,"tileSizeHeight":512,"tileOverlapWidth":0,"tileOverlapHeight":0}]}
        """ : """
        {"customer":"customer","modelName":"profile","hebrewRuleName":"משימה",
         "algorithmName":"custom","priority":1,"username":null,
         "runEveryOtherImage":false,"shouldCheckInVip":false}
        """);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RecordingPublisher : IRabbitMqDestinationPublisher
    {
        public List<RabbitMqMessageEnvelope> Messages { get; } = [];

        public Task PublishAsync(RabbitMqDestination destination, RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public byte[]? Body { get; private set; }
        public IReadOnlyList<string> HeaderNames { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            HeaderNames = request.Headers.Select(header => header.Key).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
