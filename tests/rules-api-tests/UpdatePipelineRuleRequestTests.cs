using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Requests;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class UpdatePipelineRuleRequestTests
{
    [Fact]
    public void ProvidedFieldsListTopLevelAndMatchFieldsThatWereSent()
    {
        var request = JsonSerializer.Deserialize<UpdatePipelineRuleRequest>(
            """{"ruleName":"x","description":null,"match":{"sensors":null,"maxPhotoAgeDays":3}}""")!;

        Assert.Equal(
            ["description", "match.maxPhotoAgeDays", "match.sensors", "ruleName"],
            request.ProvidedFields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NullMatchIsAProvidedField()
    {
        var request = JsonSerializer.Deserialize<UpdatePipelineRuleRequest>("""{"match":null}""")!;

        Assert.Equal(["match"], request.ProvidedFields);
    }
}
