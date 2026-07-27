using ImagingPipeline.RabbitMqClient;
using OpenTelemetry;

namespace ImagingPipeline.TbPublisher.Tests.Fakes;

public sealed class FakeRabbitMqPublisher : IRabbitMqPublisher
{
    private readonly int? _failAfterCount;
    private readonly Exception? _failureException;

    public FakeRabbitMqPublisher()
    {
    }

    private FakeRabbitMqPublisher(int failAfterCount, Exception failureException)
    {
        _failAfterCount = failAfterCount;
        _failureException = failureException;
    }

    public List<RabbitMqMessageEnvelope> PublishedToOutput { get; } = [];

    public List<IReadOnlyDictionary<string, string>> BaggageSnapshots { get; } = [];

    public CancellationToken LastCancellationToken { get; private set; }

    /// <summary>
    /// Publishes successfully <paramref name="successCount"/> times, then throws
    /// <paramref name="failureException"/> on the next call, to simulate a mid-fan-out publish
    /// failure (e.g. a broker outage partway through publishing tiling-config output messages).
    /// </summary>
    public static FakeRabbitMqPublisher ThatFailsAfter(int successCount, Exception? failureException = null) =>
        new(successCount, failureException ?? new InvalidOperationException("Simulated publish failure."));

    public Task PublishAsync(
        string exchange,
        string routingKey,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishToInputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task PublishToOutputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default)
    {
        LastCancellationToken = cancellationToken;
        BaggageSnapshots.Add(Baggage.Current.GetBaggage().ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.Ordinal));

        if (_failAfterCount is not null && PublishedToOutput.Count >= _failAfterCount)
        {
            throw _failureException!;
        }

        PublishedToOutput.Add(message);
        return Task.CompletedTask;
    }
}
