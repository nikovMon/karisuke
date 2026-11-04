using ImagingPipeline.RabbitMqClient;

namespace ImagingPipeline.DlqRecovery;

public sealed class RecoveryOptions
{
    public bool Enabled { get; set; } = true;
    public bool DryRun { get; set; } = true;
    public int MaxMessagesPerQueue { get; set; } = 50;
    public double MessagesPerSecond { get; set; } = 1;
    public int MaxRedrives { get; set; } = 3;
    public int MaxRunSeconds { get; set; } = 210;
    public int ShutdownReserveSeconds { get; set; } = 15;
    public int OperationTimeoutSeconds { get; set; } = 10;
    public uint InputQueueBacklogLimit { get; set; } = 1000;
    public bool ReplayDependencyResponseFailures { get; set; }
    public Dictionary<string, RecoveryConnectionOptions> Connections { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, RecoveryQueueOptions> Queues { get; set; } = new(StringComparer.Ordinal);

    public void Validate()
    {
        if (!Enabled) return;
        if (MaxMessagesPerQueue < 1 || !double.IsFinite(MessagesPerSecond) || MessagesPerSecond <= 0
            || MessagesPerSecond > 1000 || MaxRedrives < 1 || MaxRedrives == int.MaxValue
            || OperationTimeoutSeconds < 1 || ShutdownReserveSeconds < 1
            || MaxRunSeconds <= ShutdownReserveSeconds + OperationTimeoutSeconds || InputQueueBacklogLimit < 1)
            throw new InvalidOperationException("Recovery limits must be positive and leave time for operation timeout and shutdown.");

        if (Queues.Count == 0)
            throw new InvalidOperationException("Recovery requires at least one configured queue mapping, or Enabled=false.");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, queue) in Queues.Where(pair => pair.Value.Enabled))
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(queue.SourceQueue)
                || string.IsNullOrWhiteSpace(queue.ReplayQueue) || string.IsNullOrWhiteSpace(queue.ManualQueue)
                || string.IsNullOrWhiteSpace(queue.RetryCountHeader)
                || queue.RetryCountHeader.StartsWith("redrive-", StringComparison.OrdinalIgnoreCase)
                || queue.RetryCountHeader.StartsWith("failure-", StringComparison.OrdinalIgnoreCase)
                || new[] { "last-redrive-at", "first-failure-at", "last-failure-at", "dependency", "operation", "http-status" }
                    .Contains(queue.RetryCountHeader, StringComparer.OrdinalIgnoreCase)
                || queue.SourceQueue == queue.ManualQueue)
                throw new InvalidOperationException($"Recovery mapping '{name}' has missing or conflicting queue/header settings.");
            var source = GetConnection(queue.SourceConnection);
            var target = GetConnection(queue.ReplayConnection);
            source.Validate(queue.SourceConnection);
            target.Validate(queue.ReplayConnection);
            var sourceIdentity = $"{source.Host.ToLowerInvariant()}:{source.Port}/{source.VirtualHost}/{queue.SourceQueue}";
            if (!sources.Add(sourceIdentity))
                throw new InvalidOperationException("A source DLQ can appear only once in the enabled recovery mappings.");
            if (source.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && source.Port == target.Port
                && source.VirtualHost == target.VirtualHost
                && (queue.SourceQueue == queue.ReplayQueue || queue.ManualQueue == queue.ReplayQueue))
                throw new InvalidOperationException($"Recovery mapping '{name}' must replay to a separate input queue.");
        }
    }

    public RecoveryConnectionOptions GetConnection(string name) =>
        Connections.TryGetValue(name, out var connection)
            ? connection
            : throw new InvalidOperationException($"Recovery connection '{name}' is not configured.");
}

public sealed class RecoveryConnectionOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool TlsEnabled { get; set; }
    public string? TlsServerName { get; set; }

    internal void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Contains("REPLACE", StringComparison.OrdinalIgnoreCase)
            || Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(VirtualHost)
            || string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
            throw new InvalidOperationException($"Recovery connection '{name}' requires host, port, vhost and credential configuration.");
    }
}

public sealed class RecoveryQueueOptions
{
    public bool Enabled { get; set; } = true;
    public string SourceConnection { get; set; } = string.Empty;
    public string SourceQueue { get; set; } = string.Empty;
    public string ReplayConnection { get; set; } = string.Empty;
    public string ReplayQueue { get; set; } = string.Empty;
    public string ManualQueue { get; set; } = string.Empty;
    public string RetryCountHeader { get; set; } = "retry-count";
    public RabbitMqFailureLogOptions FailureLogging { get; set; } = new();
}
