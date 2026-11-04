using System.Security.Cryptography;
using ImagingPipeline.RabbitMqClient;
using RabbitMQ.Client;

namespace ImagingPipeline.DlqRecovery;

public sealed record RecoveryDelivery(ulong Tag, byte[] Body, BasicProperties Properties)
{
    public RabbitMqMessageEnvelope ToEnvelope() => new(
        Properties.MessageId ?? Convert.ToHexString(SHA256.HashData(Body)),
        Body, Properties.ContentType ?? "application/octet-stream",
        Properties.Headers is null ? null : new Dictionary<string, object?>(Properties.Headers),
        Properties.CorrelationId);
}

public interface IRecoverySession : IAsyncDisposable
{
    uint InitialCount { get; }
    Task<RecoveryDelivery> ReceiveAsync(CancellationToken cancellationToken);
    Task<uint> ReadInputDepthAsync(CancellationToken cancellationToken);
    Task PublishReplayAsync(RecoveryDelivery delivery, CancellationToken cancellationToken);
    Task PublishManualAsync(RecoveryDelivery delivery, CancellationToken cancellationToken);
    Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken);
}

public interface IRecoverySessionFactory
{
    Task<uint> InspectAsync(RecoveryQueueOptions mapping, CancellationToken cancellationToken);
    Task<IRecoverySession> OpenAsync(RecoveryQueueOptions mapping, CancellationToken cancellationToken);
}
