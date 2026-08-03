using ImagingPipeline.Common.Dtos.Messaging;

namespace ImagingPipeline.TbConsumer.Application;

public sealed class EmbedderInputMessageBuilder
{
    public IReadOnlyList<EmbedderInputDto> Build(
        TbConsumerInputDto input,
        IReadOnlyList<IReadOnlyList<double>> mappedCoordinates,
        DateTimeOffset requestTime)
    {
        var metadata = input.Metadata;
        var overlay = metadata.MissionMetadata.Overlay;
        var algorithmNames = overlay.AlgorithmNames
            .Select(algorithm => algorithm.ToString())
            .ToList();

        var outputs = new List<EmbedderInputDto>(input.Tiles.Count);
        for (var i = 0; i < input.Tiles.Count; i++)
        {
            var tile = input.Tiles[i];

            double? lon = null;
            double? lat = null;
            var corners = new List<double[]>();

            var baseIndex = i * 4;
            if (baseIndex + 3 < mappedCoordinates.Count
                && mappedCoordinates[baseIndex] is { Count: >= 2 })
            {
                for (var j = 0; j < 4; j++)
                {
                    var point = mappedCoordinates[baseIndex + j];
                    corners.Add([point[0], point[1]]);
                }

                lon = (corners[0][0] + corners[2][0]) / 2.0;
                lat = (corners[0][1] + corners[2][1]) / 2.0;
            }

            var tileCoordinates = new PolygonDto
            {
                Coordinates = corners.ToArray()
            };

            double resolutionMPerPx = Math.Round(overlay.BestResolution, 2) / 100d;

            var pixelSize = Math.Abs(tile.Roi[2] - tile.Roi[0]);
            var tileSizeMeters = pixelSize * resolutionMPerPx;

            var embedderInput = new EmbedderInputPayload
            {
                TileId = tile.TileIndex.ToString(),
                Gid = overlay.ImageId,
                ImagePath = tile.Uri,
                Sensor = overlay.SensorName,
                ImagingTime = overlay.ImageTime,
                Resolution = resolutionMPerPx,
                TenantId = metadata.MissionMetadata.TenantId,
                Algorithms = algorithmNames,
                TileCoordinates = tileCoordinates,
                Lon = lon,
                Lat = lat,
                TilesSizeMeters = tileSizeMeters,
                RequestTime = requestTime.UtcDateTime,
                PixelRoi = tile.Roi,
            };

            outputs.Add(new EmbedderInputDto
            {
                FrameMetadata = metadata.FrameMetadata,
                ModelMetadata = metadata.ModelMetadata,
                FocusedPxWkt = metadata.FocusedPxWkt,
                MissionMetadata = metadata.MissionMetadata,
                RequestId = input.RequestId,
                TaskId = metadata.TaskId,
                ImageUrl = tile.Uri,
                S3Uri = ToS3Uri(tile.Uri),
                EmbedderInput = embedderInput
            });
        }

        return outputs;
    }

    private static string ToS3Uri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return url;
        }

        return $"s3://{parsed.AbsolutePath.TrimStart('/')}";
    }
}
