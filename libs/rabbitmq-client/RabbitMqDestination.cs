namespace ImagingPipeline.RabbitMqClient;

/// <summary>
/// A broker connection used by <see cref="IRabbitMqDestinationPublisher"/>. Publishers share one
/// connection and channel pool per distinct value, so reuse the same settings for the same broker.
/// </summary>
public sealed record RabbitMqDestinationConnection(
    string Name,
    string Host,
    int Port,
    string Username,
    string Password,
    string VirtualHost)
{
    public override string ToString() =>
        $"RabbitMqDestinationConnection {{ Name = {Name}, Host = {Host}, Port = {Port}, VirtualHost = {VirtualHost}, Credentials = [redacted] }}";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) ||
            string.IsNullOrWhiteSpace(Host) ||
            Port is < 1 or > 65535 ||
            VirtualHost is null)
        {
            throw new ArgumentException(
                "RabbitMQ destination connection requires a name, host, valid port and virtual host.",
                "destination");
        }
    }
}

/// <summary>
/// One output queue, optionally reached through a named exchange. The queue, the exchange and
/// their binding are declared on first use per publisher channel, like the client's own topology.
/// </summary>
public sealed record RabbitMqDestination
{
    public required RabbitMqDestinationConnection Connection { get; init; }
    public required string QueueName { get; init; }
    public IReadOnlyDictionary<string, object?> QueueArguments { get; init; } = EmptyArguments;

    /// <summary>Empty publishes through the default exchange, routed by queue name.</summary>
    public string ExchangeName { get; init; } = string.Empty;
    public string ExchangeType { get; init; } = RabbitMQ.Client.ExchangeType.Direct;
    public string RoutingKey { get; init; } = string.Empty;
    public bool BindQueueToExchange { get; init; }
    public IReadOnlyDictionary<string, object?> ExchangeArguments { get; init; } = EmptyArguments;
    public IReadOnlyDictionary<string, object?> BindingArguments { get; init; } = EmptyArguments;

    internal string EffectiveRoutingKey => ExchangeName.Length == 0 ? QueueName : RoutingKey;

    // Identifies the declared entities on a channel. Arguments are not part of it: two
    // destinations that differ only in arguments would fail broker declaration anyway.
    internal string DeclarationKey => $"{ExchangeName}\0{QueueName}\0{RoutingKey}\0{BindQueueToExchange}";

    internal string TelemetryName => ExchangeName.Length == 0 ? QueueName : ExchangeName;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Connection, "destination");
        Connection.Validate();
        if (string.IsNullOrWhiteSpace(QueueName))
        {
            throw new ArgumentException("RabbitMQ destination queue name must not be empty.", "destination");
        }

        if (ExchangeName is null || ExchangeType is null || RoutingKey is null ||
            QueueArguments is null || ExchangeArguments is null || BindingArguments is null)
        {
            throw new ArgumentException("RabbitMQ destination fields must not be null.", "destination");
        }

        if (BindQueueToExchange && ExchangeName.Length == 0)
        {
            throw new ArgumentException("RabbitMQ destination binding requires a named exchange.", "destination");
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
