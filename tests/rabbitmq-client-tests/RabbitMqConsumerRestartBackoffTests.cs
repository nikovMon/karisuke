namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqConsumerRestartBackoffTests
{
    [Fact]
    public void NextDelayExponentiallyIncreasesTheJitterUpperBoundAndCapsIt()
    {
        var backoff = new RabbitMqConsumerRestartBackoff(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            () => 1D);

        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(10), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(20), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
    }

    [Fact]
    public void NextDelayAppliesJitterWithoutAllowingAnImmediateRetry()
    {
        var backoff = new RabbitMqConsumerRestartBackoff(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            () => 0.25D);

        Assert.Equal(TimeSpan.FromSeconds(6.25), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(12.5), backoff.NextDelay());
    }

    [Fact]
    public void ResetRestartsTheExponentialSequence()
    {
        var backoff = new RabbitMqConsumerRestartBackoff(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            () => 1D);

        _ = backoff.NextDelay();
        _ = backoff.NextDelay();
        backoff.Reset();

        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
    }

    [Fact]
    public void StableConsumerUptimeResetsTheExponentialSequence()
    {
        var backoff = new RabbitMqConsumerRestartBackoff(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            () => 1D);

        _ = backoff.NextDelay();
        _ = backoff.NextDelay();

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            backoff.NextDelay(consumerUptime: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ShortConsumerUptimeContinuesTheExponentialSequence()
    {
        var backoff = new RabbitMqConsumerRestartBackoff(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            () => 1D);

        _ = backoff.NextDelay();

        Assert.Equal(
            TimeSpan.FromSeconds(10),
            backoff.NextDelay(consumerUptime: TimeSpan.FromSeconds(29)));
    }

    [Fact]
    public void ZeroBaseDelayAlwaysReturnsImmediately()
    {
        var backoff = new RabbitMqConsumerRestartBackoff(TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, backoff.NextDelay());
    }

    [Fact]
    public void ConfiguredBaseDelayIsCappedAtThirtySeconds()
    {
        var backoff = new RabbitMqConsumerRestartBackoff(TimeSpan.FromMinutes(1));

        Assert.InRange(
            backoff.NextDelay(),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30));
    }
}
