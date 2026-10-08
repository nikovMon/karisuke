namespace ImagingPipeline.PipelineCatalog;

public sealed class PipelineCatalogOptions
{
    public const string SectionName = "PipelineCatalog";

    public Dictionary<string, RabbitMqConnectionOptions> RabbitMqConnections { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, PipelineSettings> Pipelines { get; set; } = new(StringComparer.Ordinal);
}

public record PipelineSettings
{
    public bool Enabled { get; init; }
    public string ContractId { get; init; } = string.Empty;
    public string RulesIndex { get; init; } = string.Empty;
    public PipelineExtraData ExtraData { get; init; } = PipelineExtraData.Empty;
    public PipelineTransportOptions Transport { get; init; } = new();
}

public sealed record PipelineDefinition : PipelineSettings
{
    public string PipelineId { get; init; } = string.Empty;
}

/// <summary>How a pipeline receives its work. Configured as "rabbitmq" or "http".</summary>
public enum PipelineTransportKind
{
    RabbitMq = 1,
    Http = 2
}

public static class PipelineTransportKindExtensions
{
    /// <summary>The configured spelling, also used as the telemetry value.</summary>
    public static string Value(this PipelineTransportKind kind) => kind switch
    {
        PipelineTransportKind.RabbitMq => "rabbitmq",
        PipelineTransportKind.Http => "http",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transport kind.")
    };
}

public sealed record PipelineTransportOptions
{
    // Nullable, so a missing Kind fails validation instead of defaulting to a transport.
    public PipelineTransportKind? Kind { get; init; }
    public RabbitMqTransportOptions? RabbitMq { get; init; }
    public HttpTransportOptions? Http { get; init; }
}

public sealed record RabbitMqTransportOptions
{
    public string ConnectionRef { get; init; } = string.Empty;
    public RabbitMqQueueOptions Output { get; init; } = new();
}

public sealed record RabbitMqConnectionOptions
{
    public string Hostname { get; init; } = string.Empty;
    public int Port { get; init; } = 5672;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string VirtualHost { get; init; } = "/";

    public override string ToString() => "RabbitMqConnectionOptions { Credentials = [redacted] }";
}

public sealed record RabbitMqQueueOptions
{
    public string QueueName { get; init; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; init; } = new(StringComparer.Ordinal);
    public RabbitMqExchangeOptions ExchangeSettings { get; init; } = new();

    public string GetEffectiveRoutingKey() => ExchangeSettings.ExchangeName.Length == 0
        ? QueueName
        : ExchangeSettings.RoutingKey;
}

public sealed record RabbitMqExchangeOptions
{
    public bool ShouldBindToExchange { get; init; }
    public string ExchangeName { get; init; } = string.Empty;
    public string ExchangeType { get; init; } = "direct";
    public string RoutingKey { get; init; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> BindingArguments { get; init; } = new(StringComparer.Ordinal);
}

public sealed record HttpTransportOptions
{
    public const int MaximumTimerSeconds = 4_294_967;

    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "POST";
    public int TimeoutSeconds { get; init; } = 20;
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
