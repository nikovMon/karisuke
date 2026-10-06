using System.Text;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.UnifiedGateway.Dispatch;
using ImagingPipeline.UnifiedGateway.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Catalog = ImagingPipeline.PipelineCatalog.PipelineCatalog;

namespace ImagingPipeline.UnifiedGateway.Tests;

internal static class DispatchTestData
{
    public static Catalog CreateCatalog(params PipelineDefinition[] definitions)
    {
        var registry = new PipelineContractRegistry([new AsdPipelineContract()]);
        return new Catalog(
            Options.Create(new PipelineCatalogOptions
            {
                RabbitMqConnections = new Dictionary<string, RabbitMqConnectionOptions>
                {
                    ["asd-output"] = new()
                    {
                        Hostname = "broker-a", Port = 5673, Username = "user", Password = "secret", VirtualHost = "/asd"
                    }
                },
                Pipelines = definitions.ToDictionary(
                    pipeline => pipeline.PipelineId, pipeline => (PipelineSettings)pipeline, StringComparer.Ordinal)
            }),
            registry,
            NullLogger<Catalog>.Instance);
    }

    public static PipelineDefinition RabbitMqPipeline(
        string id,
        bool enabled = true,
        RabbitMqQueueOptions? output = null) => new()
    {
        PipelineId = id,
        ContractId = "asd",
        Enabled = enabled,
        RulesIndex = $"{id}-pipeline-index",
        Transport = new PipelineTransportOptions
        {
            Kind = "rabbitmq",
            RabbitMq = new RabbitMqTransportOptions
            {
                ConnectionRef = "asd-output",
                Output = output ?? new RabbitMqQueueOptions { QueueName = $"{id}.output" }
            }
        }
    };

    public static PipelineDefinition HttpPipeline(string id, HttpTransportOptions? http = null) => new()
    {
        PipelineId = id,
        ContractId = "asd",
        Enabled = true,
        RulesIndex = $"{id}-pipeline-index",
        Transport = new PipelineTransportOptions
        {
            Kind = "http",
            Http = http ?? new HttpTransportOptions { Endpoint = "https://example.invalid/work" }
        }
    };

    public static DispatchUnit Unit(
        PipelineDefinition pipeline,
        string dispatchId = "image-a:asd:rule-a:0123456789abcdef",
        PipelinePayload? payload = null,
        IReadOnlyDictionary<string, object?>? sourceHeaders = null) =>
        new(
            dispatchId,
            new PreparedPipelineWork(pipeline, payload ?? Payload()),
            "source-1",
            sourceHeaders);

    public static PipelinePayload Payload(
        IReadOnlyDictionary<string, string>? attributes = null,
        IReadOnlyDictionary<string, object?>? rabbitMqAttributes = null) =>
        new(
            Encoding.UTF8.GetBytes("""{"taskId":"task-a"}"""),
            "application/json",
            attributes ?? new Dictionary<string, string>(),
            rabbitMqAttributes);
}
