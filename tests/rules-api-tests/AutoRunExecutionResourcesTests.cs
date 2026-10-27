using System.Collections.Concurrent;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunExecutionResourcesTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public async Task Publications_share_the_smaller_pool_limit_and_wait_in_FIFO_order()
    {
        var resources = Resources(publisherConcurrency: 100, channelPool: 2);
        using var first = await resources.AcquirePublicationAsync(default);
        var second = await resources.AcquirePublicationAsync(default);
        var third = resources.AcquirePublicationAsync(default).AsTask();
        var fourth = resources.AcquirePublicationAsync(default).AsTask();

        Assert.Equal(2, resources.PublisherConcurrency);
        Assert.Equal(2, resources.ActivePublicationPermits);
        Assert.Equal(2, resources.WaitingPublicationPermits);
        Assert.False(third.IsCompleted);
        Assert.False(fourth.IsCompleted);

        second.Dispose();
        var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fourth.IsCompleted);
        Assert.Equal(2, resources.ActivePublicationPermits);
        thirdLease.Dispose();
        using var fourthLease = await fourth.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, resources.WaitingPublicationPermits);
        Assert.Equal(2, resources.ActivePublicationPermits);
    }

    [Fact]
    public async Task Cancelled_waiters_do_not_take_capacity_and_release_is_idempotent()
    {
        var resources = Resources(publisherConcurrency: 1);
        var first = await resources.AcquirePublicationAsync(default);
        using var cancellation = new CancellationTokenSource();
        var cancelled = resources.AcquirePublicationAsync(cancellation.Token).AsTask();
        var next = resources.AcquirePublicationAsync(default).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, resources.WaitingPublicationPermits);
        first.Dispose();
        first.Dispose();
        var nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resources.ActivePublicationPermits);
        nextLease.Dispose();
        nextLease.Dispose();
        Assert.Equal(0, resources.ActivePublicationPermits);
        Assert.Equal(0, resources.WaitingPublicationPermits);
        Assert.ThrowsAny<OperationCanceledException>(() => resources.AcquirePublicationAsync(cancellation.Token));
    }

    [Fact]
    public async Task Simultaneous_publications_do_not_exceed_global_capacity()
    {
        var resources = Resources(publisherConcurrency: 4, channelPool: 3);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var tasks = Enumerable.Range(0, 30).Select(async _ =>
        {
            using var lease = await resources.AcquirePublicationAsync(default);
            var count = Interlocked.Increment(ref active);
            int previous;
            do { previous = maximum; } while (count > previous && Interlocked.CompareExchange(ref maximum, count, previous) != previous);
            if (count == 3) entered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
        }).ToArray();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, resources.ActivePublicationPermits);
        Assert.Equal(27, resources.WaitingPublicationPermits);
        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, maximum);
        Assert.Equal(0, resources.ActivePublicationPermits);
    }

    [Fact]
    public void Preparation_admission_reserves_two_responses_atomically_and_releases_once()
    {
        var memory = new Memory(headroom: 800 * MiB, limit: 1200 * MiB);
        var resources = Resources(memory: memory);
        var leases = new ConcurrentBag<IDisposable>();
        Parallel.For(0, 20, _ =>
        {
            if (resources.TryReservePreparation(out var lease)) leases.Add(lease!);
        });

        Assert.Equal(384 * MiB, resources.PreparationReservationBytes);
        Assert.Equal(2, leases.Count);
        Assert.Equal(768 * MiB, resources.ReservedPreparationBytes);
        Assert.False(resources.TryReservePreparation(out var rejected));
        Assert.Null(rejected);

        foreach (var lease in leases) { lease.Dispose(); lease.Dispose(); }
        Assert.Equal(0, resources.ReservedPreparationBytes);
        Assert.True(resources.TryReservePreparation(out var recovered));
        recovered!.Dispose();
    }

    [Fact]
    public void Preparation_waits_when_process_usage_grows_or_measurement_is_unavailable()
    {
        var memory = new Memory(headroom: 1200 * MiB, limit: 1200 * MiB);
        var resources = Resources(memory: memory);
        Assert.True(resources.TryReservePreparation(out var first));
        memory.Headroom = 500 * MiB;
        Assert.False(resources.TryReservePreparation(out _));
        first!.Dispose();
        Assert.True(resources.TryReservePreparation(out var second));
        second!.Dispose();
        memory.UnderPressure = true;
        Assert.False(resources.TryReservePreparation(out _));
    }

    [Fact]
    public void Impossible_preparation_reservations_are_rejected_at_startup()
    {
        var memory = new Memory(headroom: 1000 * MiB, limit: 1000 * MiB);
        var resources = Resources(memory: memory, enabled: true);
        var error = Assert.Throws<OptionsValidationException>(resources.ValidateConfiguration);
        Assert.Contains("MaxConcurrentPreparations", error.Message);
        Assert.Contains("MaxResponseBytes", error.Message);
        Resources(memory: memory, enabled: false).ValidateConfiguration();
        Resources(memory: new Memory(1200 * MiB, 1200 * MiB), enabled: true).ValidateConfiguration();
    }

    [Theory]
    [InlineData(0, 3, 20)]
    [InlineData(3, 0, 20)]
    [InlineData(3, 3, 0)]
    [InlineData(-1, 3, 20)]
    public void Execution_limits_must_be_positive(int preparations, int publications, int queued)
    {
        Assert.False(new AutoRunOptions
        {
            MaxConcurrentPreparations = preparations, MaxConcurrentPublications = publications, MaxQueuedJobs = queued
        }.IsValid());
    }

    private static AutoRunExecutionResources Resources(int publisherConcurrency = 4, int channelPool = 4,
        IAutoRunMemoryGuard? memory = null, bool enabled = false) => new(
            Options.Create(new AutoRunOptions { PublisherConcurrency = publisherConcurrency, Enabled = enabled }),
            Options.Create(new ElasticProviderOptions()), Options.Create(new RabbitMqClientOptions { PublisherChannelPoolSize = channelPool }), memory);

    private sealed class Memory(long headroom, long limit) : IAutoRunMemoryGuard
    {
        public long Headroom { get; set; } = headroom;
        public bool UnderPressure { get; set; }
        public AutoRunMemorySnapshot Snapshot() => new(limit, limit - Headroom, 0, limit, Headroom, UnderPressure);
        public bool CanAllocate(long additionalBytes = 0) => !UnderPressure && additionalBytes < Headroom;
        public void ThrowIfUnsafe(long additionalBytes = 0)
        {
            if (!CanAllocate(additionalBytes)) throw new AutoRunMemoryLimitException();
        }
    }
}
