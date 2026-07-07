using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Messaging;

public static class TilingConfigValidator
{
    public static bool IsValid(TilingConfig config, out string error)
    {
        if (config.TileSizeWidth <= 0 || config.TileSizeHeight <= 0)
        {
            error = "tileSizeWidth and tileSizeHeight must be greater than 0.";
            return false;
        }

        if (config.TileOverlapWidth < 0 || config.TileOverlapHeight < 0)
        {
            error = "tileOverlapWidth and tileOverlapHeight cannot be negative.";
            return false;
        }

        if (config.TileOverlapWidth >= config.TileSizeWidth || config.TileOverlapHeight >= config.TileSizeHeight)
        {
            error = "tile overlap must be smaller than the corresponding tile size.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
