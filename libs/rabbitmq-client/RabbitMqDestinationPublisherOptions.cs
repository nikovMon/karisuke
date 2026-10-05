namespace ImagingPipeline.RabbitMqClient;

public sealed class RabbitMqDestinationPublisherOptions
{
    public const string SectionName = "RabbitMq:DestinationPublisher";

    /// <summary>Concurrent confirmed publish channels per destination connection.</summary>
    public int PublisherChannelPoolSize { get; set; } = 4;

    public int ReconnectDelaySeconds { get; set; } = 5;

    internal bool IsValid(out string error)
    {
        if (PublisherChannelPoolSize < 1 || ReconnectDelaySeconds < 1)
        {
            error = "RabbitMq DestinationPublisher channel pool and reconnect settings must be greater than zero.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
