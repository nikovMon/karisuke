using System.Text.Json;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.TbPublisher.Errors;

namespace ImagingPipeline.TbPublisher.Processing;

public sealed class TbPublisherGeometryConverter
{
    public IReadOnlyList<IReadOnlyList<double>> ExtractCoordinates(JsonElement roiFootprint)
    {
        try
        {
            var geometry = GeometryUtilities.ReadGeoJson(roiFootprint);
            return geometry.Coordinates
                .Select(coordinate => (IReadOnlyList<double>)[coordinate.X, coordinate.Y])
                .ToList();
        }
        catch (Exception ex) when (ex is not TbPublisherValidationException and not OperationCanceledException)
        {
            throw new TbPublisherValidationException($"roiFootprint is invalid: {ex.Message}", ex);
        }
    }

    public string BuildFocusedPxWkt(IReadOnlyList<IReadOnlyList<double>> pixelCoordinates)
    {
        try
        {
            var geometry = GeometryUtilities.CreatePolygonFromCoordinates(pixelCoordinates);
            return GeometryUtilities.WriteWkt(geometry);
        }
        catch (Exception ex) when (ex is not TbPublisherValidationException and not OperationCanceledException)
        {
            throw new TbPublisherValidationException($"projected pixel geometry is invalid: {ex.Message}", ex);
        }
    }
}
