using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Gateway.Processing.Rules;

internal static class RegistrationQualityMask
{
    public static int From(RegistrationQuality quality) =>
        1 << RegistrationQualityContract.GetValidatedOrdinal(quality);

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
