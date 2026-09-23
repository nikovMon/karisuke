using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImagingPipeline.Common.Dtos.Gateway.Messages;

namespace ImagingPipeline.PipelineContracts.Tests;

public sealed class AsdPipelineContractTests
{
    private const string ValidRunParams = """
        {
          "tenantId": "tenant-a",
          "algorithmNames": ["Rpn", "FindAir"],
          "tilingConfigs": [
            {"tileSizeWidth": 500, "tileSizeHeight": 400, "tileOverlapWidth": 10, "tileOverlapHeight": 20},
            {"tileSizeWidth": 110, "tileSizeHeight": 120, "tileOverlapWidth": 0, "tileOverlapHeight": 0}
          ]
        }
        """;

    private readonly AsdPipelineContract _contract = new();

    [Fact]
    public void BuildPayloadPreservesLegacyAsdWireFieldsOrderAndAttributes()
    {
        // Golden wire fixture follows the existing GatewayOutputMessageBuilder contract.
        const string expected = """
            {"taskId":"image-1:gateway-task:rule-1:tenant-a","ruleId":"rule-1","algorithmName":["Rpn","FindAir"],"tenantId":"tenant-a","tilingConfigs":[{"tileSizeWidth":500,"tileSizeHeight":400,"tileOverlapWidth":10,"tileOverlapHeight":20},{"tileSizeWidth":110,"tileSizeHeight":120,"tileOverlapWidth":0,"tileOverlapHeight":0}],"imageId":"image-1","roiFootprint":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"photoTime":"2026-06-30T06:54:07+00:00","sensorType":"EO","imageUrl":"/images/image-1.tiff","imageWidth":4096,"imageHeight":3072,"bestResolution":25.9,"sensorName":"cam-001","areaOfInterest":"region-alpha","gridType":"regular","gridURI":"/grids/image-1.json"}
            """;

        var payload = _contract.BuildPayload(Context(), Json(ValidRunParams));

        Assert.Equal(expected, Encoding.UTF8.GetString(payload.Body));
        Assert.Equal("application/json", payload.ContentType);
        Assert.Equal(2, payload.Attributes.Count);
        Assert.Equal("Rpn,FindAir", payload.Attributes["algorithmName"]);
        Assert.Equal("tenant-a", payload.Attributes["tenantId"]);
        Assert.NotNull(payload.RabbitMqAttributes);
        var wireHeader = Assert.Single(payload.RabbitMqAttributes);
        Assert.Equal("findair-contract-version", wireHeader.Key);
        Assert.Equal(1, Assert.IsType<int>(wireHeader.Value));
        Assert.DoesNotContain("findair-contract-version", payload.Attributes.Keys);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, object?>)payload.RabbitMqAttributes)["findair-contract-version"] = 2);

        var downstream = JsonSerializer.Deserialize<GatewayOutputMessageDto>(payload.Body)!;
        var validation = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(downstream, new ValidationContext(downstream), validation, true));
        Assert.Empty(validation);
    }

    [Fact]
    public void BuildPayloadUsesAssignedTaskIdentityAndWritesNullAreaOfInterest()
    {
        var context = Context() with { TaskId = "caller-assigned-task", AreaOfInterest = null };

        var payload = _contract.BuildPayload(context, Json(ValidRunParams));

        using var body = JsonDocument.Parse(payload.Body);
        Assert.Equal(context.TaskId, body.RootElement.GetProperty("taskId").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("areaOfInterest").ValueKind);
    }

    [Fact]
    public void UndefinedAndEmptyExtraDataPreserveLegacyPayloadBytesAndHeaders()
    {
        IPipelineContract contract = _contract;
        var legacy = contract.BuildPayload(Context(), Json(ValidRunParams));
        var omitted = contract.BuildPayload(Context(), Json(ValidRunParams), default);
        var empty = contract.BuildPayload(Context(), Json(ValidRunParams), Json("{}"));

        Assert.Equal(legacy.Body, omitted.Body);
        Assert.Equal(legacy.Body, empty.Body);
        Assert.Equal(legacy.Attributes, omitted.Attributes);
        Assert.Equal(legacy.Attributes, empty.Attributes);
        Assert.Same(legacy.RabbitMqAttributes, omitted.RabbitMqAttributes);
        Assert.Same(legacy.RabbitMqAttributes, empty.RabbitMqAttributes);
        using var body = JsonDocument.Parse(empty.Body);
        Assert.False(body.RootElement.TryGetProperty("extraData", out _));
    }

    [Fact]
    public void ExtraDataPreservesNestedJsonTypesAndNumericPrecision()
    {
        var extraData = Json("""
            {
              "name": "custom",
              "threshold": 0.12345678901234567890123456789,
              "largeInteger": 9007199254740993,
              "enabled": true,
              "disabled": false,
              "optional": null,
              "nested": {"count": 3, "values": [1, "two", null, false, {"fraction": 2.5}]},
              "emptyObject": {},
              "emptyArray": []
            }
            """);

        var payload = _contract.BuildPayload(Context(), Json(ValidRunParams), extraData);

        using var body = JsonDocument.Parse(payload.Body);
        var written = body.RootElement.GetProperty("extraData");
        Assert.Equal(JsonValueKind.Object, written.ValueKind);
        Assert.Equal("custom", written.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Number, written.GetProperty("threshold").ValueKind);
        Assert.Equal(extraData.GetProperty("threshold").GetRawText(), written.GetProperty("threshold").GetRawText());
        Assert.Equal(9007199254740993L, written.GetProperty("largeInteger").GetInt64());
        Assert.True(written.GetProperty("enabled").GetBoolean());
        Assert.False(written.GetProperty("disabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, written.GetProperty("optional").ValueKind);
        var nested = written.GetProperty("nested");
        Assert.Equal(3, nested.GetProperty("count").GetInt32());
        var values = nested.GetProperty("values");
        Assert.Equal(JsonValueKind.Array, values.ValueKind);
        Assert.Equal(5, values.GetArrayLength());
        Assert.Equal(1, values[0].GetInt32());
        Assert.Equal("two", values[1].GetString());
        Assert.Equal(JsonValueKind.Null, values[2].ValueKind);
        Assert.False(values[3].GetBoolean());
        Assert.Equal(2.5m, values[4].GetProperty("fraction").GetDecimal());
        Assert.Empty(written.GetProperty("emptyObject").EnumerateObject());
        Assert.Equal(0, written.GetProperty("emptyArray").GetArrayLength());
    }

    [Fact]
    public void ExtraDataFieldNamesCannotOverrideContractFieldsOrBecomeHeaders()
    {
        var extraData = Json("""
            {
              "taskId": "custom-task",
              "tenantId": "custom-tenant",
              "algorithmName": ["custom-algorithm"],
              "imageWidth": 0,
              "Authorization": "custom-secret",
              "findair-contract-version": 99,
              "extraData": {"nested": true}
            }
            """);
        var legacy = _contract.BuildPayload(Context(), Json(ValidRunParams));

        var payload = _contract.BuildPayload(Context(), Json(ValidRunParams), extraData);

        using var legacyBody = JsonDocument.Parse(legacy.Body);
        using var body = JsonDocument.Parse(payload.Body);
        foreach (var property in legacyBody.RootElement.EnumerateObject())
        {
            Assert.Equal(property.Value.GetRawText(), body.RootElement.GetProperty(property.Name).GetRawText());
        }
        Assert.Equal(legacyBody.RootElement.EnumerateObject().Count() + 1, body.RootElement.EnumerateObject().Count());
        var written = body.RootElement.GetProperty("extraData");
        Assert.Equal("custom-task", written.GetProperty("taskId").GetString());
        Assert.Equal("custom-tenant", written.GetProperty("tenantId").GetString());
        Assert.True(written.GetProperty("extraData").GetProperty("nested").GetBoolean());
        Assert.Equal(legacy.Attributes, payload.Attributes);
        Assert.Same(legacy.RabbitMqAttributes, payload.RabbitMqAttributes);
        Assert.DoesNotContain("extraData", payload.Attributes.Keys);
        Assert.DoesNotContain("Authorization", payload.Attributes.Keys);
        Assert.Equal(1, payload.RabbitMqAttributes!["findair-contract-version"]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"private-content\"]")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("\"private-content\"")]
    public void PresentExtraDataMustBeAnObjectAndValidationDoesNotExposeItsContent(string json)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            _contract.BuildPayload(Context(), Json(ValidRunParams), Json(json)));

        Assert.Equal("extraData", error.ParamName);
        Assert.StartsWith("Extra data must be a JSON object when provided.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-content", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPayloadKeepsOneTenantPayloadWithAllTilingConfigs()
    {
        var payload = _contract.BuildPayload(Context(), Json(ValidRunParams));

        using var body = JsonDocument.Parse(payload.Body);
        Assert.Equal(2, body.RootElement.GetProperty("tilingConfigs").GetArrayLength());
        Assert.Equal("tenant-a", body.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public void ValidateRunParamsAcceptsSupportedAlgorithmsAndZeroOverlap()
    {
        Assert.Equal("asd", _contract.ContractId);
        Assert.Empty(_contract.ValidateRunParams(Json(ValidRunParams)));
    }

    [Theory]
    [MemberData(nameof(InvalidRunParams))]
    public void InvalidParamsHaveFieldErrorsAndCannotBuildPayload(string json, string expectedField)
    {
        var parameters = Json(json);

        var errors = _contract.ValidateRunParams(parameters);

        Assert.Contains(errors, error => error.Field == expectedField && !string.IsNullOrWhiteSpace(error.Message));
        var exception = Assert.Throws<ArgumentException>(() => _contract.BuildPayload(Context(), parameters));
        Assert.Equal("runParams", exception.ParamName);
        Assert.Contains(expectedField, exception.Message);
    }

    [Fact]
    public void UndefinedRunParamsAreReportedAsInvalid()
    {
        var errors = _contract.ValidateRunParams(default);

        Assert.Single(errors);
        Assert.Equal("runParams", errors[0].Field);
        Assert.Throws<ArgumentException>(() => _contract.BuildPayload(Context(), default));
    }

    [Fact]
    public void BuildPayloadRequiresAnObjectRoiWithoutDoingGeometryCalculations()
    {
        var context = Context() with { RoiFootprint = Json("null") };

        var error = Assert.Throws<ArgumentException>(() => _contract.BuildPayload(context, Json(ValidRunParams)));

        Assert.Equal("context", error.ParamName);
    }

    public static IEnumerable<object[]> InvalidRunParams()
    {
        foreach (var root in new[] { "null", "[]", "42", "\"tenant-a\"" })
        {
            yield return [root, "runParams"];
        }

        yield return ["{}", "tenantId"];
        foreach (var value in new[] { "null", "42", "\"\"", "\"  \"", "[]" })
        {
            yield return [WithProperty("tenantId", value), "tenantId"];
        }
        yield return [WithoutProperty("tenantId"), "tenantId"];
        yield return [WithProperty("unexpected", "true"), "unexpected"];
        yield return [ValidRunParams.Replace("\"tenantId\": \"tenant-a\"", "\"tenantId\": \"tenant-a\", \"tenantId\": \"other\"", StringComparison.Ordinal), "tenantId"];

        foreach (var value in new[] { "null", "[]", "\"FindAir\"" })
        {
            yield return [WithProperty("algorithmNames", value), "algorithmNames"];
        }
        yield return [WithoutProperty("algorithmNames"), "algorithmNames"];
        foreach (var value in new[] { "[\"Der\"]", "[\"findair\"]", "[0]", "[null]", "[{}]", "[\"FindAir,Rpn\"]" })
        {
            yield return [WithProperty("algorithmNames", value), "algorithmNames[0]"];
        }
        yield return [WithProperty("algorithmNames", "[\"FindAir\",\"FindAir\"]"), "algorithmNames[1]"];

        foreach (var value in new[] { "null", "[]", "{}" })
        {
            yield return [WithProperty("tilingConfigs", value), "tilingConfigs"];
        }
        yield return [WithoutProperty("tilingConfigs"), "tilingConfigs"];
        yield return [WithProperty("tilingConfigs", "[null]"), "tilingConfigs[0]"];
        yield return [WithTilingProperty("tileSizeWidth", "\"500\""), "tilingConfigs[0].tileSizeWidth"];
        yield return [WithTilingProperty("tileSizeWidth", "1.5"), "tilingConfigs[0].tileSizeWidth"];
        yield return [WithTilingProperty("tileSizeHeight", "2147483648"), "tilingConfigs[0].tileSizeHeight"];
        yield return [WithTilingProperty("extra", "true"), "tilingConfigs[0].extra"];
        yield return [WithTilingProperty("tileSizeWidth", "0"), "tilingConfigs[0]"];
        yield return [WithTilingProperty("tileSizeHeight", "-1"), "tilingConfigs[0]"];
        yield return [WithTilingProperty("tileOverlapWidth", "-1"), "tilingConfigs[0]"];
        yield return [WithTilingProperty("tileOverlapWidth", "500"), "tilingConfigs[0]"];
        yield return [WithTilingProperty("tileOverlapHeight", "400"), "tilingConfigs[0]"];
        yield return [ValidRunParams.Replace("\"tileOverlapHeight\": 20", "\"tileOverlapHeight\": 20, \"tileOverlapHeight\": 0", StringComparison.Ordinal), "tilingConfigs[0].tileOverlapHeight"];

        var missingOverlap = JsonNode.Parse(ValidRunParams)!.AsObject();
        missingOverlap["tilingConfigs"]![0]!.AsObject().Remove("tileOverlapWidth");
        yield return [missingOverlap.ToJsonString(), "tilingConfigs[0].tileOverlapWidth"];
    }

    private static string WithProperty(string name, string value)
    {
        var parameters = JsonNode.Parse(ValidRunParams)!.AsObject();
        parameters[name] = JsonNode.Parse(value);
        return parameters.ToJsonString();
    }

    private static string WithoutProperty(string name)
    {
        var parameters = JsonNode.Parse(ValidRunParams)!.AsObject();
        parameters.Remove(name);
        return parameters.ToJsonString();
    }

    private static string WithTilingProperty(string name, string value)
    {
        var parameters = JsonNode.Parse(ValidRunParams)!.AsObject();
        parameters["tilingConfigs"]![0]![name] = JsonNode.Parse(value);
        return parameters.ToJsonString();
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static PipelineDispatchContext Context() => new(
        TaskId: "image-1:gateway-task:rule-1:tenant-a",
        RuleId: "rule-1",
        ImageId: "image-1",
        RoiFootprint: Json("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}"""),
        PhotoTime: new DateTimeOffset(2026, 6, 30, 6, 54, 7, TimeSpan.Zero),
        SensorType: "EO",
        ImageUrl: "/images/image-1.tiff",
        ImageWidth: 4096,
        ImageHeight: 3072,
        BestResolution: 25.9,
        SensorName: "cam-001",
        AreaOfInterest: "region-alpha",
        GridType: "regular",
        GridUri: "/grids/image-1.json");
}
