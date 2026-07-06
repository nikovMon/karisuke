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
      "algorithmName": "FindAir",
      "tenantId": "tenant-1",
      "imageId": "image-1",
      "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
      "tilingConfigs": [
        { "tileSizeWidth": 512, "tileSizeHeight": 512, "tileOverlapWidth": 32, "tileOverlapHeight": 32 },
        { "tileSizeWidth": 256, "tileSizeHeight": 256, "tileOverlapWidth": 16, "tileOverlapHeight": 16 }
      ]
    }
    """);

    private static readonly IReadOnlyList<IReadOnlyList<double>> Coordinates =
        [[0, 0], [1, 0], [1, 1], [0, 1]];

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
            .Select(envelope => JsonSerializer.Deserialize<TbPublisherOutputMessageDto>(
                envelope.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();
        Assert.Equal("tenant-1", outputs[0].MissionMetadata.TenantId);
        Assert.Equal("image-1", outputs[0].MissionMetadata.Overlay.ImageId);
        Assert.Equal(512, outputs[0].ModelMetadata.TbCropSizeX);
        Assert.Equal(256, outputs[1].ModelMetadata.TbCropSizeX);
        Assert.Equal(outputs[0].MissionMetadata.MissionId, outputs[1].MissionMetadata.MissionId);
        Assert.NotEqual(outputs[0].TaskId, outputs[1].TaskId);
        Assert.StartsWith("POLYGON", outputs[0].FocusedPxWkt);
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
          "algorithmName": "FindAir",
          "tenantId": "tenant-1",
          "imageId": "image-1",
          "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
          "tilingConfigs": [
            { "tileSizeWidth": 512, "tileSizeHeight": 512, "tileOverlapWidth": 512, "tileOverlapHeight": 0 }
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
            new TbPublisherOutputMessageBuilder(),
            publisher,
            NullLogger<TbPublisherMessageHandler>.Instance);
}
