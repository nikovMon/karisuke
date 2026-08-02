using ImagingPipeline.Gateway.Configuration;

namespace ImagingPipeline.Gateway.Tests;

public sealed class GatewaySettingsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IsValidRejectsNonPositiveMaximumPhotoAge(int maxPhotoAgeDays)
    {
        var settings = new GatewaySettings { MaxPhotoAgeDays = maxPhotoAgeDays };

        var isValid = settings.IsValid(out var error);

        Assert.False(isValid);
        Assert.Contains("MaxPhotoAgeDays", error, StringComparison.Ordinal);
    }

    [Fact]
    public void IsValidAcceptsPositiveMaximumPhotoAge()
    {
        var settings = new GatewaySettings { MaxPhotoAgeDays = 30 };

        Assert.True(settings.IsValid(out var error), error);
    }
}
