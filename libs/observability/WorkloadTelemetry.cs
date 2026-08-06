using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability;

public static class WorkloadTelemetry
{
    private static readonly Counter<long> Images = TelemetryMeters.Pipeline.CreateCounter<long>(
        TelemetryMetricNames.Images, "{image}", "Logical image messages processed by Gateway.");
    private static readonly Counter<long> Tasks = TelemetryMeters.Pipeline.CreateCounter<long>(
        TelemetryMetricNames.Tasks, "{task}", "Logical rule and tenant tasks processed.");
    private static readonly Counter<long> TileRequests = TelemetryMeters.Pipeline.CreateCounter<long>(
        TelemetryMetricNames.TileRequests, "{request}", "Tile Builder requests published.");
    private static readonly Counter<long> TileBatches = TelemetryMeters.Pipeline.CreateCounter<long>(
        TelemetryMetricNames.TileBatches, "{batch}", "Tile Builder output batches processed.");
    private static readonly Counter<long> Tiles = TelemetryMeters.Pipeline.CreateCounter<long>(
        TelemetryMetricNames.Tiles, "{tile}", "Logical tiles processed.");
    private static readonly Counter<long> TilePublishAttempts = TelemetryMeters.Pipeline.CreateCounter<long>(
        TelemetryMetricNames.TilePublishAttempts, "{attempt}", "Attempts to publish tiles to Embedder.");

    public static void RecordImage(
        TelemetryOutcome outcome,
        string areaName,
        string sensorName)
    {
        var tags = OutcomeTags(outcome);
        AddIfPresent(ref tags, TelemetryAttributeNames.AreaName, areaName);
        AddIfPresent(ref tags, TelemetryAttributeNames.SensorName, sensorName);
        Images.Add(1, tags);
    }

    public static void RecordTask(
        PipelineDirection direction,
        TelemetryOutcome outcome,
        string ruleId,
        string tenantId,
        string areaName,
        string sensorName,
        string algorithmNames,
        long count = 1)
    {
        var tags = BusinessTags(outcome, ruleId, tenantId, areaName, sensorName, algorithmNames);
        tags.Add(TelemetryAttributeNames.PipelineDirection, direction.Value());
        Tasks.Add(Math.Max(0, count), tags);
    }

    public static void RecordTileRequest(
        TelemetryOutcome outcome,
        string ruleId,
        string tenantId,
        string areaName,
        string sensorName,
        string algorithmNames,
        long count = 1) =>
        TileRequests.Add(
            Math.Max(0, count),
            BusinessTags(outcome, ruleId, tenantId, areaName, sensorName, algorithmNames));

    public static void RecordTileBatch(
        TelemetryOutcome outcome,
        string ruleId,
        string tenantId,
        string areaName,
        string sensorName,
        string algorithmNames) =>
        TileBatches.Add(
            1,
            BusinessTags(outcome, ruleId, tenantId, areaName, sensorName, algorithmNames));

    public static void RecordTiles(
        TelemetryOutcome outcome,
        string ruleId,
        string tenantId,
        string areaName,
        string sensorName,
        string algorithmNames,
        int tileWidth,
        int tileHeight,
        long count)
    {
        var tags = BusinessTags(outcome, ruleId, tenantId, areaName, sensorName, algorithmNames);
        tags.Add("findair.tile.size", $"{tileWidth}x{tileHeight}");
        Tiles.Add(Math.Max(0, count), tags);
    }

    public static void RecordTilePublishAttempt(
        TelemetryOutcome outcome,
        string ruleId,
        string tenantId,
        string areaName,
        string sensorName,
        string algorithmNames)
    {
        var tags = BusinessTags(outcome, ruleId, tenantId, areaName, sensorName, algorithmNames);
        TilePublishAttempts.Add(1, tags);
    }

    private static TagList BusinessTags(
        TelemetryOutcome outcome,
        string ruleId,
        string tenantId,
        string areaName,
        string sensorName,
        string algorithmNames)
    {
        var tags = OutcomeTags(outcome);
        AddIfPresent(ref tags, TelemetryAttributeNames.PipelineRuleId, ruleId);
        AddIfPresent(ref tags, TelemetryAttributeNames.PipelineTenantId, tenantId);
        AddIfPresent(ref tags, TelemetryAttributeNames.AreaName, areaName);
        AddIfPresent(ref tags, TelemetryAttributeNames.SensorName, sensorName);
        AddIfPresent(ref tags, TelemetryAttributeNames.PipelineAlgorithmName, algorithmNames);
        return tags;
    }

    private static TagList OutcomeTags(TelemetryOutcome outcome) => new()
    {
        { TelemetryAttributeNames.PipelineOutcome, outcome.Value() }
    };

    private static void AddIfPresent(ref TagList tags, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            tags.Add(key, value);
        }
    }
}
