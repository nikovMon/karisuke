using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImagingPipeline.PipelineContracts.Tests;

public sealed class AlgoPipelineContractTests
{
    private const string Rule = """
        {"customer":"customer-a","profile_name":"profile-a","hebrew_rule_name":"Mission",
         "algorithm_name":"custom-algorithm","priority":7,"username":null,
         "run_every_other_image":true,"should_check_in_vip":true}
        """;
    private const string Settings = """
        {"XUserName":"configured-user","Origin":"configured-origin","QueueType":"configured-queue","SaveDetections":true}
        """;
    private readonly AlgoPipelineContract _contract = new();

    [Fact]
    public void BuildsLegacyMissionBodyFromRuleOverlayAndSettings()
    {
        const string expected = """
            {"modelName":"profile-a","focusedWkt":"POLYGON ((0 0, 2 0, 2 2, 0 0))","origin":"configured-origin","queueType":"configured-queue","priority":7,"saveDetections":true,"algorithmName":"custom-algorithm","missionName":"Mission 23/09/2026","username":"configured-user","displayName":"customer-a","requestingUnit":"customer-a","runEveryOtherImage":true,"shouldCheckInVip":true,"tasksData":[{"imageId":"image-a","legId":"leg-a","prevOverlayId":"image-prev","nextOverlayId":"image-next","photoTime":"2026-09-23T00:15:00"}]}
            """;
        var payload = _contract.BuildPayload(Context(), Json(Rule), Json(Settings));
        Assert.Equal(expected, Encoding.UTF8.GetString(payload.Body));
        Assert.Equal("application/json", payload.ContentType);
        Assert.Empty(payload.Attributes);
        Assert.Null(payload.RabbitMqAttributes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FlagsControlTaskAssignmentsAndKeepUnassignedPropertiesAsNull(bool everyOther, bool vip)
    {
        var rule = JsonNode.Parse(Rule)!;
        rule["run_every_other_image"] = everyOther;
        rule["should_check_in_vip"] = vip;
        using var body = Body(Context(), rule.ToJsonString());
        var tasks = body.RootElement.GetProperty("tasksData");
        Assert.Equal(1, tasks.GetArrayLength());
        var task = tasks[0];
        Assert.Equal(5, task.EnumerateObject().Count());
        Assert.Equal("image-a", task.GetProperty("imageId").GetString());
        Assert.Equal(everyOther ? "leg-a" : null, task.GetProperty("legId").GetString());
        Assert.Equal(everyOther ? "image-prev" : null, task.GetProperty("prevOverlayId").GetString());
        Assert.Equal(everyOther ? "image-next" : null, task.GetProperty("nextOverlayId").GetString());
        Assert.Equal(vip ? JsonValueKind.String : JsonValueKind.Null, task.GetProperty("photoTime").ValueKind);
    }

    [Theory]
    [InlineData(null, "configured-user")]
    [InlineData("", "")]
    [InlineData("rule-user", "rule-user")]
    public void UsernameFallbackAppliesOnlyToNull(string? username, string expected)
    {
        var rule = JsonNode.Parse(Rule)!;
        rule["username"] = username;
        using var body = Body(Context(), rule.ToJsonString());
        Assert.Equal(expected, body.RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public void NullableRuleNamesAndMissingNeighborIdsPreserveLegacyNulls()
    {
        var rule = JsonNode.Parse(Rule)!;
        rule["profile_name"] = null;
        rule["hebrew_rule_name"] = null;
        using var body = Body(Context() with { LegId = null, PrevOverlayId = null, NextOverlayId = null }, rule.ToJsonString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("modelName").ValueKind);
        Assert.Equal(" 23/09/2026", body.RootElement.GetProperty("missionName").GetString());
        var task = body.RootElement.GetProperty("tasksData")[0];
        Assert.Equal(JsonValueKind.Null, task.GetProperty("legId").ValueKind);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("prevOverlayId").ValueKind);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("nextOverlayId").ValueKind);
    }

    [Fact]
    public void LegacyClockValueControlsDateAndPhotoTimeInsteadOfUtcProjection()
    {
        var timestamp = new DateTimeOffset(2026, 9, 23, 0, 15, 0, TimeSpan.FromHours(3));
        var context = Context() with { PhotoTime = timestamp.ToUniversalTime(), OverlayPhotoTime = timestamp.DateTime };
        using var body = Body(context);
        Assert.Equal(22, context.PhotoTime.Day);
        Assert.Equal("Mission 23/09/2026", body.RootElement.GetProperty("missionName").GetString());
        Assert.Equal("2026-09-23T00:15:00", body.RootElement.GetProperty("tasksData")[0].GetProperty("photoTime").GetString());
    }

    [Fact]
    public void FallbackPreservesDateTimeOffsetClockWithoutConvertingToUtc()
    {
        var context = Context() with
        {
            PhotoTime = new DateTimeOffset(2026, 9, 23, 0, 15, 0, TimeSpan.FromHours(3)), OverlayPhotoTime = null
        };
        using var body = Body(context);
        Assert.Equal("Mission 23/09/2026", body.RootElement.GetProperty("missionName").GetString());
    }

    [Fact]
    public void MissionDateIsIndependentOfProcessCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            using var body = Body(Context());
            Assert.Equal("Mission 23/09/2026", body.RootElement.GetProperty("missionName").GetString());
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public void RuleGeometryIsUsedEvenWhenInputRoiIsCompletelyDifferent()
    {
        var context = Context() with { RoiFootprint = Json("""{"type":"Point","coordinates":[100,50]}""") };
        using var body = Body(context);
        Assert.Equal("POLYGON ((0 0, 2 0, 2 2, 0 0))", body.RootElement.GetProperty("focusedWkt").GetString());
    }

    [Fact]
    public void FocusedWktIsTheRuleLocationAsGiven()
    {
        const string multiPolygon = "MULTIPOLYGON (((0 0, 2 0, 2 2, 0 0)), ((10 10, 12 10, 12 12, 10 10)))";
        using var body = Body(Context() with { RuleLocationWkt = multiPolygon });
        Assert.Equal(multiPolygon, body.RootElement.GetProperty("focusedWkt").GetString());
    }

    [Fact]
    public void RuleWithoutLocationCannotBuild()
    {
        var context = Context() with { RuleLocationWkt = null };
        Assert.Contains(_contract.ValidateContext(context), error => error.Field == "match.locationWkt");
        Assert.NotEmpty(_contract.ValidateRuleLocation(null));
        Assert.Empty(_contract.ValidateRuleLocation("POLYGON ((0 0, 2 0, 2 2, 0 0))"));
        Assert.Throws<ArgumentException>(() => _contract.BuildPayload(context, Json(Rule), Json(Settings)));
    }

    [Fact]
    public void UnknownExtraDataIsAllowedButCannotOverrideOrExtendAlgoBody()
    {
        var settings = JsonNode.Parse(Settings)!;
        settings["priority"] = 99;
        settings["missionName"] = "not-the-mission";
        settings["custom"] = JsonNode.Parse("""{"arbitrary":[null,true,42]}""");
        using var body = Body(Context(), settings: settings.ToJsonString());
        Assert.Equal(7, body.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal("Mission 23/09/2026", body.RootElement.GetProperty("missionName").GetString());
        Assert.False(body.RootElement.TryGetProperty("custom", out _));
        Assert.False(body.RootElement.TryGetProperty("extraData", out _));
        Assert.Empty(_contract.ValidateExtraData(Json(settings.ToJsonString())));
    }

    [Theory]
    // The rule location now comes from the rule's match, so it is no longer a run parameter.
    [InlineData("location_geojson", "{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[2,0],[2,2],[0,0]]]}")]
    [InlineData("algorithm_name", "[\"custom-algorithm\"]")]
    [InlineData("priority", "\"7\"")]
    [InlineData("priority", "2147483648")]
    [InlineData("customer", "null")]
    [InlineData("profile_name", "1")]
    [InlineData("run_every_other_image", "\"true\"")]
    [InlineData("should_check_in_vip", "null")]
    [InlineData("unexpected", "true")]
    public void InvalidRuleFieldsReturnFieldErrorsAndCannotBuild(string field, string value)
    {
        var rule = JsonNode.Parse(Rule)!;
        rule[field] = JsonNode.Parse(value);
        var parameters = Json(rule.ToJsonString());
        Assert.Contains(_contract.ValidateRunParams(parameters), error => error.Field == field);
        Assert.Throws<ArgumentException>(() => _contract.BuildPayload(Context(), parameters, Json(Settings)));
    }

    [Theory]
    [InlineData("XUserName", "null")]
    [InlineData("Origin", "\"\"")]
    [InlineData("QueueType", "42")]
    [InlineData("SaveDetections", "\"true\"")]
    public void SettingsHaveTypedValidationWithoutExposingTheirValues(string field, string value)
    {
        var settings = JsonNode.Parse(Settings)!;
        settings[field] = JsonNode.Parse(value);
        Assert.Contains(_contract.ValidateExtraData(Json(settings.ToJsonString())), error => error.Field == field);
        var exception = Assert.Throws<ArgumentException>(() => _contract.BuildPayload(Context(), Json(Rule), Json(settings.ToJsonString())));
        Assert.Equal("extraData", exception.ParamName);
        Assert.DoesNotContain("configured-user", exception.Message);
    }

    [Fact]
    public void MissingSettingsAndDuplicateKnownFieldsAreRejected()
    {
        Assert.NotEmpty(_contract.ValidateExtraData(default));
        Assert.Equal(4, _contract.ValidateExtraData(Json("{}")).Count);
        var duplicateSettings = Settings.Replace("\"XUserName\":", "\"XUserName\":\"first\",\"XUserName\":", StringComparison.Ordinal);
        Assert.Contains(_contract.ValidateExtraData(Json(duplicateSettings)), error => error.Field == "XUserName");
        var duplicateRule = Rule.Replace("\"priority\":", "\"priority\":1,\"priority\":", StringComparison.Ordinal);
        Assert.Contains(_contract.ValidateRunParams(Json(duplicateRule)), error => error.Field == "priority");
    }

    [Fact]
    public void NoUnsupportedPriorityRangeOrAlgorithmAllowlistIsIntroduced()
    {
        var rule = JsonNode.Parse(Rule)!;
        rule["priority"] = int.MinValue;
        rule["algorithm_name"] = "future-algorithm";
        Assert.Empty(_contract.ValidateRunParams(Json(rule.ToJsonString())));
    }

    [Fact]
    public void ContextRequiresImageAndPhotoTimeButNoAsdMetadata()
    {
        Assert.Empty(_contract.ValidateContext(Context()));
        Assert.Contains(_contract.ValidateContext(Context() with { ImageId = "" }), error => error.Field == "input.id");
        Assert.Contains(_contract.ValidateContext(Context() with { PhotoTime = default, OverlayPhotoTime = null }), error => error.Field == "input.photoTime");
    }

    private JsonDocument Body(PipelineDispatchContext context, string rule = Rule, string settings = Settings) =>
        JsonDocument.Parse(_contract.BuildPayload(context, Json(rule), Json(settings)).Body);

    private static PipelineDispatchContext Context() => new(
        "task-a", "rule-a", "image-a", default, new DateTimeOffset(2026, 9, 23, 0, 15, 0, TimeSpan.Zero),
        "", "", 0, 0, 0, "", null, "", "")
    {
        LegId = "leg-a", PrevOverlayId = "image-prev", NextOverlayId = "image-next",
        OverlayPhotoTime = new DateTime(2026, 9, 23, 0, 15, 0, DateTimeKind.Unspecified),
        RuleLocationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 0))"
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
