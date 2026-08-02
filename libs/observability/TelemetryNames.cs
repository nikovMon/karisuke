namespace ImagingPipeline.Observability;

public static class TelemetrySourceNames
{
    public const string RabbitMqClientPublisher = "RabbitMQ.Client.Publisher";
    public const string RabbitMqClientSubscriber = "RabbitMQ.Client.Subscriber";
    public const string Observability = "ImagingPipeline.Observability";
    public const string RabbitMq = "ImagingPipeline.RabbitMq";
    public const string ProjectionMapper = "ImagingPipeline.ProjectionMapper";
    public const string Elasticsearch = "ImagingPipeline.Elasticsearch";
    public const string Dependencies = "ImagingPipeline.Dependencies";
    public const string Pipeline = "ImagingPipeline.Pipeline";
    public const string RulesApi = "ImagingPipeline.RulesApi";
    public const string Gateway = "ImagingPipeline.Gateway";
    public const string TbPublisher = "ImagingPipeline.TbPublisher";
    public const string TileBuilder = "ImagingPipeline.TileBuilder";
    public const string TbConsumer = "ImagingPipeline.TbConsumer";
    public const string Embedder = "ImagingPipeline.Embedder";

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
    public const string LogsDropped = "imaging_pipeline.logs.dropped";
    public const string LogExportRequests = "imaging_pipeline.logs.export.requests";
    public const string LogExportDuration = "imaging_pipeline.logs.export.duration";
    public const string LogQueueSize = "imaging_pipeline.logs.queue.size";

    public const string MessagingSent = "messaging.client.sent.messages";
    public const string MessagingConsumed = "messaging.client.consumed.messages";
    public const string MessagingClientDuration = "messaging.client.operation.duration";
    public const string MessagingProcessDuration = "messaging.process.duration";
    public const string MessagingBodySize = "imaging_pipeline.messaging.message.body.size";
    public const string MessagingDeliveryDelay = "imaging_pipeline.messaging.delivery.delay";
    public const string MessagingInFlight = "imaging_pipeline.messaging.inflight";
    public const string MessagingSettlements = "imaging_pipeline.messaging.settlements";
    public const string MessagingRetries = "imaging_pipeline.messaging.retries";
    public const string RabbitMqConnections = "imaging_pipeline.rabbitmq.connections";
    public const string RabbitMqConnectionEvents = "imaging_pipeline.rabbitmq.connection.events";
    public const string RabbitMqChannels = "imaging_pipeline.rabbitmq.channels";
    public const string RabbitMqChannelWaitDuration = "imaging_pipeline.rabbitmq.publisher.channel_wait.duration";
    public const string RabbitMqConsumerRestarts = "imaging_pipeline.rabbitmq.consumer.restarts";

    public const string DependencyOperations = "imaging_pipeline.dependency.operations";
    public const string DependencyDuration = "imaging_pipeline.dependency.operation.duration";
    public const string DependencyPayloadSize = "imaging_pipeline.dependency.payload.size";
    public const string DependencyBatchSize = "imaging_pipeline.dependency.batch.size";

    public const string PipelineMessages = "imaging_pipeline.pipeline.messages";
    public const string PipelinePayloadSize = "imaging_pipeline.pipeline.payload.size";
    public const string PipelineStageDuration = "imaging_pipeline.pipeline.stage.duration";
    public const string PipelineExternalStageDuration = "imaging_pipeline.pipeline.external_stage.duration";
    public const string PipelineFanOut = "imaging_pipeline.pipeline.fanout";
    public const string PipelineBatchSize = "imaging_pipeline.pipeline.batch.size";
    public const string PipelineEndToEndDuration = "imaging_pipeline.pipeline.end_to_end.duration";
    public const string InvalidTimingHeaders = "imaging_pipeline.telemetry.invalid_timing_headers";

    public const string GatewayRuleCacheEntries = "imaging_pipeline.gateway.rule_cache.entries";
    public const string GatewayRuleCacheSkippedRules = "imaging_pipeline.gateway.rule_cache.skipped_rules";
    public const string GatewayRuleCacheAge = "imaging_pipeline.gateway.rule_cache.age";
    public const string GatewayRuleCacheRefreshes = "imaging_pipeline.gateway.rule_cache.refreshes";
    public const string GatewayRuleCacheRefreshDuration = "imaging_pipeline.gateway.rule_cache.refresh.duration";
    public const string GatewayRulesEvaluated = "imaging_pipeline.gateway.rules.evaluated";
    public const string GatewayRulesMatched = "imaging_pipeline.gateway.rules.matched";
    public const string GatewayRulesFilteredPhotoAge = "imaging_pipeline.gateway.rules.filtered_photo_age";

    public const string RulesOperations = "imaging_pipeline.rules.operations";
    public const string RulesOperationDuration = "imaging_pipeline.rules.operation.duration";
    public const string RulesDocuments = "imaging_pipeline.rules.documents";
    public const string RulesBatchSize = "imaging_pipeline.rules.batch.size";
    public const string RulesValidationFailures = "imaging_pipeline.rules.validation_failures";
}

public static class TelemetryAttributeNames
{
    public const string ErrorCategory = "imaging_pipeline.error.category";
    public const string PipelineStage = "imaging_pipeline.pipeline.stage";
    public const string PipelineDirection = "imaging_pipeline.pipeline.direction";
    public const string PipelineOutcome = "imaging_pipeline.pipeline.outcome";
    public const string PipelineItem = "imaging_pipeline.pipeline.item";
    public const string PipelineTaskId = "imaging_pipeline.task.id";
    public const string PipelineRequestId = "imaging_pipeline.request.id";
    public const string PipelineImageId = "imaging_pipeline.image.id";
    public const string PipelineRuleId = "imaging_pipeline.rule.id";
    public const string PipelineTenantId = "imaging_pipeline.tenant.id";
    public const string PipelineAlgorithmName = "imaging_pipeline.algorithm.name";
    public const string RetryAttempt = "imaging_pipeline.retry.attempt";
    public const string DependencyName = "imaging_pipeline.dependency.name";
    public const string DependencyOperation = "imaging_pipeline.dependency.operation";
}
