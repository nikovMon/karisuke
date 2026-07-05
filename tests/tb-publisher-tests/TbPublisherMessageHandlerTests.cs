using System.Text;
using System.Text.Json;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbPublisher.Application;
using ImagingPipeline.TbPublisher.Dtos.Outbound;
using ImagingPipeline.TbPublisher.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class TbPublisherMessageHandlerTests
{
    private static readonly byte[] ValidBody = Encoding.UTF8.GetBytes("""
    {
      "ruleId": "rule-1",
      "algorithmName": "Flare",
      "tenantId": "tenant-1",
      "imageId": "image-1",
      "roiFootprint": [[35.98, 34.15]],
      "tilingConfigs": [
        { "tiling_size_width": 512, "tiling_size_height": 512, "tile_overlap_width": 32, "tile_overlap_height": 32 },
        { "tiling_size_width": 256, "tiling_size_height": 256, "tile_overlap_width": 16, "tile_overlap_height": 16 }
      ]
    }
    """);

    private static readonly IReadOnlyList<IReadOnlyList<double>> Coordinates = [[1, 2], [3, 4]];

    [Fact]
    public async Task HandleAsyncPublishesOneMessagePerTilingConfig()
    {
        var projectionClient = FakeProjectionMapperClient.ReturningSuccess(Coordinates);
        var publisher = new FakeRabbitMqPublisher();
        var handler = CreateHandler(projectionClient, publisher);

        var result = await handler.HandleAsync(RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(ValidBody), "msg-1"));

        Assert.True(result.IsSuccess);
        Assert.Null(result.OutputBody);
        Assert.Equal(2, publisher.PublishedToOutput.Count);

        var outputs = publisher.PublishedToOutput
            .Select(envelope => JsonSerializer.Deserialize<TilingConfigIngestMessageDto>(
                envelope.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();
        Assert.Equal("tenant-1", outputs[0].TenantId);
        Assert.Equal("image-1", outputs[0].ImageId);
        Assert.Equal(512, outputs[0].TilingConfig.TileSizeWidth);
        Assert.Equal(256, outputs[1].TilingConfig.TileSizeWidth);
        Assert.Equal("image-1", projectionClient.LastOverlayId);
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureWhenMessageIsInvalid()
    {
        var handler = CreateHandler(FakeProjectionMapperClient.ReturningSuccess(Coordinates), new FakeRabbitMqPublisher());

        var result = await handler.HandleAsync(RabbitMqMessageEnvelope.FromUtf8("{}"));

        Assert.False(result.IsSuccess);
        Assert.Null(result.OutputBody);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureWhenProjectionMapperFails()
    {
        var handler = CreateHandler(
            FakeProjectionMapperClient.ThrowingFailure(new ProjectionMapperClientException("mapper down")),
            new FakeRabbitMqPublisher());

        var result = await handler.HandleAsync(RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(ValidBody), "msg-1"));

        Assert.False(result.IsSuccess);
        Assert.Contains("mapper down", result.Error);
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureWhenTilingConfigMappingFails()
    {
        var invalidTilingBody = Encoding.UTF8.GetBytes("""
        {
          "ruleId": "rule-1",
          "algorithmName": "Flare",
          "tenantId": "tenant-1",
          "imageId": "image-1",
          "roiFootprint": [[35.98, 34.15]],
          "tilingConfigs": [
            { "tiling_size_width": 512, "tiling_size_height": 512, "tile_overlap_width": 512, "tile_overlap_height": 0 }
          ]
        }
        """);
        var handler = CreateHandler(FakeProjectionMapperClient.ReturningSuccess(Coordinates), new FakeRabbitMqPublisher());

        var result = await handler.HandleAsync(
            RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(invalidTilingBody), "msg-1"));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    private static TbPublisherMessageHandler CreateHandler(
        IProjectionMapperClient projectionMapperClient,
        IRabbitMqPublisher publisher) =>
        new(
            new TbMessageValidator(),
            projectionMapperClient,
            new TilingConfigMapper(),
            publisher,
            NullLogger<TbPublisherMessageHandler>.Instance);
}
