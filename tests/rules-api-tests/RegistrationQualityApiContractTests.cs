using System.Net;
using System.Text;
using ImagingPipeline.Rules.Api.Tests.Fakes;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RegistrationQualityApiContractTests
{
    [Theory]
    [InlineData("\"accurate\"")]
    [InlineData("\"sensor\"")]
    [InlineData("0")]
    [InlineData("99")]
    public async Task SensorEndpointRejectsNonExactAndNumericRegistrationQualities(string jsonValue)
    {
        using var factory = new RulesApiFactory(new InMemoryRuleRepository());
        using var client = factory.CreateClient();
        using var body = new StringContent(
            $$"""{"sensorName":"camera","values":[{{jsonValue}}]}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PatchAsync(
            "/rules/sensors/add?ids=missing",
            body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
