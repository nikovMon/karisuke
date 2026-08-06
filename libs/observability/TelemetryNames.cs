namespace ImagingPipeline.Observability;

public static class TelemetrySourceNames
{
    public const string RabbitMqClientPublisher = "RabbitMQ.Client.Publisher";
    public const string RabbitMqClientSubscriber = "RabbitMQ.Client.Subscriber";
    public const string Observability = "FindAir.Observability";
    public const string RabbitMq = "FindAir.RabbitMq";
    public const string ProjectionMapper = "FindAir.ProjectionMapper";
    public const string Elasticsearch = "FindAir.Elasticsearch";
    public const string Dependencies = "FindAir.Dependencies";
    public const string Pipeline = "FindAir.Workload";
    public const string RulesApi = "FindAir.RulesApi";
    public const string Gateway = "FindAir.Gateway";
    public const string TbPublisher = "FindAir.TbPublisher";
    public const string TileBuilder = "FindAir.TileBuilder";
    public const string TbConsumer = "FindAir.TbConsumer";
    public const string Embedder = "FindAir.Embedder";

    public static readonly string[] All =
    [
        Observability,
        RabbitMq,
        ProjectionMapper,
        Elasticsearch,
        Dependencies,
        Pipeline,
        RulesApi,
        Gateway,
        TbPublisher,
        TileBuilder,
        TbConsumer,
        Embedder
    ];
}

public static class TelemetryMetricNames
{
    public const string LogsDropped = "findair.logs.dropped";
    public const string LogExportRequests = "findair.logs.export.requests";
    public const string LogExportDuration = "findair.logs.export.duration";
    public const string LogQueueSize = "findair.logs.queue.size";

    public const string MessagingSent = "messaging.client.sent.messages";
    public const string MessagingConsumed = "messaging.client.consumed.messages";
    public const string MessagingClientDuration = "messaging.client.operation.duration";
    public const string MessagingProcessDuration = "messaging.process.duration";
    public const string MessagingBodySize = "findair.messaging.message.body.size";
    public const string MessagingDeliveryDelay = "findair.messaging.delivery.delay";
    public const string MessagingInFlight = "findair.messaging.inflight";
    public const string MessagingSettlements = "findair.messaging.settlements";
    public const string MessagingRetries = "findair.messaging.retries";
    public const string RabbitMqConnections = "findair.rabbitmq.connections";
    public const string RabbitMqConnectionEvents = "findair.rabbitmq.connection.events";
    public const string RabbitMqChannels = "findair.rabbitmq.channels";
    public const string RabbitMqChannelWaitDuration = "findair.rabbitmq.publisher.channel_wait.duration";
    public const string RabbitMqConsumerRestarts = "findair.rabbitmq.consumer.restarts";

    public const string DependencyOperations = "findair.dependency.operations";
    public const string DependencyDuration = "findair.dependency.operation.duration";
    public const string DependencyPayloadSize = "findair.dependency.payload.size";
    public const string DependencyBatchSize = "findair.dependency.batch.size";

    public const string PipelineMessages = "findair.messages";
    public const string PipelinePayloadSize = "findair.payload.size";
    public const string PipelineStageDuration = "findair.stage.duration";
    public const string PipelineExternalStageDuration = "findair.external_stage.duration";
    public const string PipelineFanOut = "findair.fanout";
    public const string PipelineBatchSize = "findair.batch.size";
    public const string PipelineEndToEndDuration = "findair.end_to_end.duration";
    public const string InvalidTimingHeaders = "findair.telemetry.invalid_timing_headers";

    public const string Images = "findair.images";
    public const string Tasks = "findair.tasks";
    public const string TileRequests = "findair.tile.requests";
    public const string TileBatches = "findair.tile.batches";
    public const string Tiles = "findair.tiles";
    public const string TilePublishAttempts = "findair.tile.publish.attempts";

    public const string GatewayRuleCacheEntries = "findair.gateway.rule_cache.entries";
    public const string GatewayRuleCacheSkippedRules = "findair.gateway.rule_cache.skipped_rules";
    public const string GatewayRuleCacheAge = "findair.gateway.rule_cache.age";
    public const string GatewayRuleCacheRefreshes = "findair.gateway.rule_cache.refreshes";
    public const string GatewayRuleCacheRefreshDuration = "findair.gateway.rule_cache.refresh.duration";
    public const string GatewayRulesEvaluated = "findair.gateway.rules.evaluated";
    public const string GatewayRulesMatched = "findair.gateway.rules.matched";
    public const string GatewayRulesFilteredPhotoAge = "findair.gateway.rules.filtered_photo_age";

    public const string RulesOperations = "findair.rules.operations";
    public const string RulesOperationDuration = "findair.rules.operation.duration";
    public const string RulesDocuments = "findair.rules.documents";
    public const string RulesBatchSize = "findair.rules.batch.size";
    public const string RulesValidationFailures = "findair.rules.validation_failures";
}

public static class TelemetryAttributeNames
{
    public const string ErrorCategory = "findair.error.category";
    public const string PipelineStage = "findair.stage";
    public const string PipelineDirection = "findair.direction";
    public const string PipelineOutcome = "findair.outcome";
    public const string PipelineItem = "findair.item";
    public const string PipelineTaskId = "findair.task.id";
    public const string PipelineRequestId = "findair.request.id";
    public const string PipelineImageId = "findair.image.id";
    public const string PipelineRuleId = "findair.rule.id";
    public const string PipelineTenantId = "findair.tenant.id";
    public const string PipelineAlgorithmName = "findair.algorithm.names";
    public const string AreaName = "findair.area.name";
    public const string SensorName = "findair.sensor.name";
    public const string TileId = "findair.tile.id";
    public const string TileIndex = "findair.tile.index";
    public const string TileCount = "findair.tile.count";
    public const string RetryAttempt = "findair.retry.attempt";
    public const string DependencyName = "findair.dependency.name";
    public const string DependencyOperation = "findair.dependency.operation";
    public const string ProjectionMode = "findair.projection.mode";
}
