namespace ImagingPipeline.RabbitMqClient;

/// <summary>
/// Produces failure-only restart delays with exponential growth and jitter.
/// Normal message processing does not use this code path.
/// </summary>
public sealed class RabbitMqConsumerRestartBackoff
{
    public static readonly TimeSpan DefaultMaximumDelay = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly Func<double> _nextJitter;
    private int _failureCount;

    public RabbitMqConsumerRestartBackoff(TimeSpan baseDelay)
        : this(
            baseDelay > DefaultMaximumDelay ? DefaultMaximumDelay : baseDelay,
            DefaultMaximumDelay,
            Random.Shared.NextDouble)
    {
    }

    public RabbitMqConsumerRestartBackoff(TimeSpan baseDelay, TimeSpan maximumDelay)
        : this(baseDelay, maximumDelay, Random.Shared.NextDouble)
    {
    }

    internal RabbitMqConsumerRestartBackoff(
        TimeSpan baseDelay,
        TimeSpan maximumDelay,
        Func<double> nextJitter)
    {
        if (baseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay));
        }

        if (maximumDelay < baseDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDelay),
                "The maximum restart delay must be greater than or equal to the base delay.");
        }

        ArgumentNullException.ThrowIfNull(nextJitter);
        _baseDelay = baseDelay;
        _maximumDelay = maximumDelay;
        _nextJitter = nextJitter;
    }

    public TimeSpan NextDelay()
    {
        if (_baseDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var failure = Interlocked.Increment(ref _failureCount);
        var exponent = Math.Min(failure - 1, 30);
        var upperBoundTicks = _baseDelay.Ticks;
        for (var step = 0; step < exponent && upperBoundTicks < _maximumDelay.Ticks; step++)
        {
            upperBoundTicks = upperBoundTicks > _maximumDelay.Ticks / 2
                ? _maximumDelay.Ticks
                : upperBoundTicks * 2;
        }

        upperBoundTicks = Math.Min(upperBoundTicks, _maximumDelay.Ticks);
        var jitter = Math.Clamp(_nextJitter(), 0D, 1D);
        var jitteredTicks = (upperBoundTicks / 2D) + ((upperBoundTicks / 2D) * jitter);
        return TimeSpan.FromTicks((long)jitteredTicks);
    }

    public TimeSpan NextDelay(TimeSpan consumerUptime)
    {
        if (consumerUptime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(consumerUptime));
        }

        if (consumerUptime >= _maximumDelay)
        {
            Reset();
        }

        return NextDelay();
    }

    public void Reset() => Interlocked.Exchange(ref _failureCount, 0);
}
