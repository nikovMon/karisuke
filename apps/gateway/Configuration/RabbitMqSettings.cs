namespace ImagingPipeline.Gateway.Configuration;

public sealed class RabbitMqSettings
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string InputQueue { get; set; } = "gateway.input.q";
    public string OutputExchange { get; set; } = "pipeline.x";
    public string OutputRoutingKey { get; set; } = "gateway.output";
    public string DlqExchange { get; set; } = "pipeline.dlx";
    public string DlqRoutingKey { get; set; } = "gateway.dlq";
    public ushort Prefetch { get; set; } = 32;
    public int PublisherConfirmTimeoutSeconds { get; set; } = 10;
    public int RequestedHeartbeatSeconds { get; set; } = 60;
    public string ClientProvidedName { get; set; } = "imaging-pipeline-gateway";

    internal string DlqQueueName => DlqRoutingKey;

    internal bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535)
        {
            error = "RabbitMq Host and Port must be valid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password))
        {
            error = "RabbitMq UserName and Password must not be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(VirtualHost))
        {
            error = "RabbitMq VirtualHost must not be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(InputQueue) ||
            string.IsNullOrWhiteSpace(OutputRoutingKey) ||
            string.IsNullOrWhiteSpace(DlqRoutingKey))
        {
            error = "RabbitMq queue and routing key settings must not be empty.";
            return false;
        }

        if (Prefetch == 0 ||
            PublisherConfirmTimeoutSeconds <= 0 ||
            RequestedHeartbeatSeconds <= 0)
        {
            error = "RabbitMq numeric settings must be greater than zero.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
