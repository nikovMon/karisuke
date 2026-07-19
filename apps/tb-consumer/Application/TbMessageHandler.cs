using System.Collections.ObjectModel;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;

namespace ImagingPipeline.TbConsumer.Application;

public sealed class TbMessageHandler(
    IProjectionMapperClient projectionMapper,
    IRabbitMqPublisher publisher,
    TimeProvider timeProvider) : RabbitMqJsonMessageHandler<TbConsumerInputDto>
{

    protected override async Task<RabbitMqMessageProcessingResult> HandleMessageAsync(
        TbConsumerInputDto input,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(input.MissionMetadata.TenantId))
        {
            return RabbitMqMessageProcessingResult.Failure("Validation failed: tenantId is missing or empty.");
        }

        if (input.Tiles is not { Count: > 0 })
        {
            return RabbitMqMessageProcessingResult.Failure("Validation failed: Tiles batch is null or empty.");
        }

        var rawAlgorithmName = input.MissionMetadata.Overlay.AlgorithmName.ToString();
        if (!Enum.TryParse<TargetAlgorithm>(rawAlgorithmName, ignoreCase: true, out var matchedAlgorithm))
        {
            throw new InvalidOperationException($"Invalid algorithm: {rawAlgorithmName}. Valid target algorithms are: {string.Join(", ", Enum.GetNames<TargetAlgorithm>())}");
        }

        var headers = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>
        {
            ["algorithm_name"] = matchedAlgorithm.ToString()
        });

        // --- Step 4: Batch-level projection mapping (single call for all tiles) ---
        var overlay = input.MissionMetadata.Overlay;

        var tilesRois = input.Tiles
            .Select(t => (IReadOnlyList<double>)t.Roi)
            .ToList();

        var batchMapped = await projectionMapper.ProcessBatchAsync(
            overlay.ImageId, tilesRois, cancellationToken);

        // --- Step 5: Per-tile fan-out (map + publish only, no projection calls) ---
        for (var i = 0; i < input.Tiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tile = input.Tiles[i];

            // 5a. Extract pre-computed projection result for this tile
            double? lon = null;
            double? lat = null;
            var coordsList = new List<double[]>();

            if (i < batchMapped.Count && batchMapped[i] is { Count: >= 2 })
            {
                var mapped = batchMapped[i];
                lon = mapped[0];
                lat = mapped[1];

                // Pass through mapped coordinate pairs directly without geometrical manipulations
                for (var j = 0; j < mapped.Count - 1; j += 2)
                {
                    coordsList.Add([mapped[j], mapped[j + 1]]);
                }
            }

            var tileCoordinates = new PolygonDto
            {
                Coordinates = coordsList.ToArray()
            };

            double? tilesSizeMeters = null;
            if (tile.Roi.Length >= 4 && overlay.ResolutionMPerPx > 0)
            {
                var widthPx = System.Math.Abs(tile.Roi[2] - tile.Roi[0]);
                tilesSizeMeters = widthPx * overlay.ResolutionMPerPx;
            }

            // 5c. Build EmbedderInput from processed tile + message metadata
            var embedderInput = new EmbedderInput
            {
                TileId = tile.TileIndex.ToString(),
                Gid = input.RequestId,
                ImagePath = tile.Uri,
                Sensor = overlay.SensorName,
                ImagingTime = overlay.ImageTime,
                Resolution = overlay.ResolutionMPerPx,
                TenantId = input.MissionMetadata.TenantId,
                Algorithms = new List<string> { matchedAlgorithm.ToString() },
                TileCoordinates = tileCoordinates,
                Lon = lon,
                Lat = lat,
                TilesSizeMeters = tilesSizeMeters,
                RequestTime = timeProvider.GetUtcNow().UtcDateTime,
            };

            // 5d. Wrap into final envelope
            var envelope = new EmbedderInputDto
            {
                FrameMetadata = input.FrameMetadata,
                ModelMetadata = input.ModelMetadata,
                FocusedPxWkt = input.FocusedPxWkt,
                MissionMetadata = input.MissionMetadata,
                RequestId = input.RequestId,
                TaskId = input.TaskId,
                EmbedderInput = embedderInput
            };

            // 5e. Serialize and publish with headers
            var body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            var outgoing = new RabbitMqMessageEnvelope(
                MessageId: Guid.NewGuid().ToString("N"),
                Body: body,
                Headers: headers,
                CorrelationId: message.CorrelationId);

            await publisher.PublishToOutputAsync(outgoing, cancellationToken);
        }

        return RabbitMqMessageProcessingResult.Success();
    }
}
