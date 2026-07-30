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
            var coordsList = new List<double[]>();

            if (mappedCoordinates[i] is { Count: >= 2 } mapped)
            {
                lon = mapped[0];
                lat = mapped[1];

                for (var j = 0; j < mapped.Count - 1; j += 2)
                {
                    coordsList.Add([mapped[j], mapped[j + 1]]);
                }
            }

            var tileCoordinates = new PolygonDto
            {
                Coordinates = coordsList.ToArray()
            };

            double? tileSizeMeters = null;
            double resolutionMPerPx = Math.Round(Convert.ToDouble(overlay.BestResolution), 2) / 100d;

            if (tile.Roi.Length >= 4 && resolutionMPerPx > 0)
            {
                var widthPx = System.Math.Abs(tile.Roi[2] - tile.Roi[0]);
                var heightPx = System.Math.Abs(tile.Roi[3] - tile.Roi[1]);
                tileSizeMeters = System.Math.Max(widthPx, heightPx) * resolutionMPerPx;
            }

            var embedderInput = new EmbedderInput
            {
                TileId = tile.TileIndex.ToString(),
                Gid = input.RequestId,
                ImagePath = tile.Uri,
                ImageUrl = overlay.ImageUrl,
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
            };

            outputs.Add(new EmbedderInputDto
            {
                FrameMetadata = metadata.FrameMetadata,
                ModelMetadata = metadata.ModelMetadata,
                FocusedPxWkt = metadata.FocusedPxWkt,
                MissionMetadata = metadata.MissionMetadata,
                RequestId = input.RequestId,
                TaskId = metadata.TaskId,
                EmbedderInput = embedderInput
            });
        }

        return outputs;
    }
}
