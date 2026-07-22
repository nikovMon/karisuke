namespace ImagingPipeline.Observability;

public enum TelemetryOutcome
{
    Success,
    Failure,
    Rejected,
    Retry,
    DeadLetter,
    Requeue,
    Exhausted,
    Cancelled
}

public enum TelemetryErrorCategory
{
    None,
    Timeout,
    Cancelled,
    Validation,
    Serialization,
    Connection,
    Unavailable,
    Dependency,
    Handler,
    Publish,
    Unknown
}

public enum MessagingOperation
{
    Send,
    Consume,
    Process,
    Ack,
    Nack
}

public enum MessagingChannelRole
{
    Publisher,
    Consumer
}

public enum RabbitMqConnectionEvent
{
    ConnectSuccess,
    ConnectFailure,
    Shutdown,
    RecoverySuccess,
    RecoveryFailure
}

public enum PipelineStage
{
    RulesApi,
    Gateway,
    TbPublisher,
    TileBuilder,
    TbConsumer,
    Embedder
}

public enum PipelineDirection
{
    Ingress,
    Egress
}

public enum PipelineItem
{
    Message,
    Rule,
    Match,
    TilingConfig,
    GroundPoint,
    Coordinate,
    Tile,
    Document
}

public enum TimingHeaderKind
{
    PipelineOrigin,
    MessagePublished
}

public enum TimingHeaderRejectionReason
{
    Malformed,
    Future,
    TooOld
}

public enum DependencyName
{
    Elasticsearch,
    ProjectionMapper,
    RabbitMq,
    Http
}

public enum DependencyOperation
{
    Search,
    Index,
    Delete,
    Ping,
    GroundToImage,
    ImageToGround,
    Get,
    Post
}

public enum RulesOperation
{
    Search,
    GetById,
    GetByName,
    Create,
    Update,
    BulkUpdate,
    SetActivity,
    AddSensor,
    RemoveSensor,
    Delete,
    Health
}

internal static class TelemetryDimensionValues
{
    public static string Value(this TelemetryOutcome value) => value switch
    {
        TelemetryOutcome.Success => "success",
        TelemetryOutcome.Failure => "failure",
        TelemetryOutcome.Rejected => "rejected",
        TelemetryOutcome.Retry => "retry",
        TelemetryOutcome.DeadLetter => "dead_letter",
        TelemetryOutcome.Requeue => "requeue",
        TelemetryOutcome.Exhausted => "exhausted",
        TelemetryOutcome.Cancelled => "cancelled",
        _ => "unknown"
    };

    public static string Value(this TelemetryErrorCategory value) => value switch
    {
        TelemetryErrorCategory.None => "none",
        TelemetryErrorCategory.Timeout => "timeout",
        TelemetryErrorCategory.Cancelled => "cancelled",
        TelemetryErrorCategory.Validation => "validation",
        TelemetryErrorCategory.Serialization => "serialization",
        TelemetryErrorCategory.Connection => "connection",
        TelemetryErrorCategory.Unavailable => "unavailable",
        TelemetryErrorCategory.Dependency => "dependency",
        TelemetryErrorCategory.Handler => "handler",
        TelemetryErrorCategory.Publish => "publish",
        _ => "unknown"
    };

    public static string Value(this MessagingOperation value) => value switch
    {
        MessagingOperation.Send => "send",
        MessagingOperation.Consume => "consume",
        MessagingOperation.Process => "process",
        MessagingOperation.Ack => "ack",
        MessagingOperation.Nack => "nack",
        _ => "unknown"
    };

    public static string OperationType(this MessagingOperation value) => value switch
    {
        MessagingOperation.Send => "send",
        MessagingOperation.Consume => "receive",
        MessagingOperation.Process => "process",
        MessagingOperation.Ack or MessagingOperation.Nack => "settle",
        _ => "unknown"
    };

    public static string Value(this MessagingChannelRole value) => value switch
    {
        MessagingChannelRole.Publisher => "publisher",
        MessagingChannelRole.Consumer => "consumer",
        _ => "unknown"
    };

    public static string Value(this RabbitMqConnectionEvent value) => value switch
    {
        RabbitMqConnectionEvent.ConnectSuccess => "connect_success",
        RabbitMqConnectionEvent.ConnectFailure => "connect_failure",
        RabbitMqConnectionEvent.Shutdown => "shutdown",
        RabbitMqConnectionEvent.RecoverySuccess => "recovery_success",
        RabbitMqConnectionEvent.RecoveryFailure => "recovery_failure",
        _ => "unknown"
    };

    public static string Value(this PipelineStage value) => value switch
    {
        PipelineStage.RulesApi => "rules_api",
        PipelineStage.Gateway => "gateway",
        PipelineStage.TbPublisher => "tb_publisher",
        PipelineStage.TileBuilder => "tile_builder",
        PipelineStage.TbConsumer => "tb_consumer",
        PipelineStage.Embedder => "embedder",
        _ => "unknown"
    };

    public static string Value(this PipelineDirection value) => value switch
    {
        PipelineDirection.Ingress => "ingress",
        PipelineDirection.Egress => "egress",
        _ => "unknown"
    };

    public static string Value(this PipelineItem value) => value switch
    {
        PipelineItem.Message => "message",
        PipelineItem.Rule => "rule",
        PipelineItem.Match => "match",
        PipelineItem.TilingConfig => "tiling_config",
        PipelineItem.GroundPoint => "ground_point",
        PipelineItem.Coordinate => "coordinate",
        PipelineItem.Tile => "tile",
        PipelineItem.Document => "document",
        _ => "unknown"
    };

    public static string Value(this TimingHeaderKind value) => value switch
    {
        TimingHeaderKind.PipelineOrigin => "pipeline_origin",
        TimingHeaderKind.MessagePublished => "message_published",
        _ => "unknown"
    };

    public static string Value(this TimingHeaderRejectionReason value) => value switch
    {
        TimingHeaderRejectionReason.Malformed => "malformed",
        TimingHeaderRejectionReason.Future => "future",
        TimingHeaderRejectionReason.TooOld => "too_old",
        _ => "unknown"
    };

    public static string Value(this DependencyName value) => value switch
    {
        DependencyName.Elasticsearch => "elasticsearch",
        DependencyName.ProjectionMapper => "projection_mapper",
        DependencyName.RabbitMq => "rabbitmq",
        DependencyName.Http => "http",
        _ => "unknown"
    };

    public static string Value(this DependencyOperation value) => value switch
    {
        DependencyOperation.Search => "search",
        DependencyOperation.Index => "index",
        DependencyOperation.Delete => "delete",
        DependencyOperation.Ping => "ping",
        DependencyOperation.GroundToImage => "ground_to_image",
        DependencyOperation.ImageToGround => "image_to_ground",
        DependencyOperation.Get => "get",
        DependencyOperation.Post => "post",
        _ => "unknown"
    };

    public static string Value(this RulesOperation value) => value switch
    {
        RulesOperation.Search => "search",
        RulesOperation.GetById => "get_by_id",
        RulesOperation.GetByName => "get_by_name",
        RulesOperation.Create => "create",
        RulesOperation.Update => "update",
        RulesOperation.BulkUpdate => "bulk_update",
        RulesOperation.SetActivity => "set_activity",
        RulesOperation.AddSensor => "add_sensor",
        RulesOperation.RemoveSensor => "remove_sensor",
        RulesOperation.Delete => "delete",
        RulesOperation.Health => "health",
        _ => "unknown"
    };
}
