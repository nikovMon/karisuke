using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Gateway.Processing.Rules;

internal static class RegistrationQualityMask
{
    public static int From(RegistrationQuality quality) =>
        quality switch
        {
            RegistrationQuality.Accurate => 1,
            RegistrationQuality.Sensor => 2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(quality),
                quality,
                "Unsupported registration quality.")
        };

    public static int From(IEnumerable<RegistrationQuality> qualities)
    {
        var mask = 0;
        foreach (var quality in qualities)
        {
            mask |= From(quality);
        }

        return mask;
    }
}
