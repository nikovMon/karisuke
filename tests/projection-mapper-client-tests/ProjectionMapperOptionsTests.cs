namespace ImagingPipeline.ProjectionMapperClient.Tests;

public sealed class ProjectionMapperOptionsTests
{
    [Fact]
    public void IsValidReturnsTrueForWellFormedOptions()
    {
        var options = CreateValidOptions();

        Assert.True(options.IsValid(out var error));
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData(" ")]
    public void IsValidReturnsFalseForInvalidHost(string host)
    {
        var options = CreateValidOptions();
        options.Host = host;

        Assert.False(options.IsValid(out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void IsValidReturnsFalseForNonPositiveTimeout()
    {
        var options = CreateValidOptions();
        options.TimeoutSeconds = 0;

        Assert.False(options.IsValid(out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void IsValidReturnsFalseWhenSendingSystemIsEmpty()
    {
        var options = CreateValidOptions();
        options.SendingSystem = string.Empty;

        Assert.False(options.IsValid(out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void IsValidReturnsFalseWhenG2IMultiPointsEndpointIsMissing()
    {
        var options = CreateValidOptions();
        options.Endpoints.Clear();

        Assert.False(options.IsValid(out var error));
        Assert.Contains(ProjectionMapperEndpointKeys.G2IMultiPoints, error);
    }

    private static ProjectionMapperOptions CreateValidOptions() => new()
    {
        Host = "http://projection-mapper:8080",
        Endpoints = new Dictionary<string, string>
        {
            [ProjectionMapperEndpointKeys.G2IMultiPoints] = "/flare/g2i-by-id",
            [ProjectionMapperEndpointKeys.I2GById] = "/flare/i2g-by-id",
            [ProjectionMapperEndpointKeys.I2GByRegistration] = "/flare/i2g-by-registration"
        },
        SendingSystem = "flare",
        TimeoutSeconds = 10
    };
}
