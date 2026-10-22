using ImagingPipeline.Rules.Api.AutoRuns;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunMemoryGuardTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    [Fact]
    public void Automatic_limit_uses_runtime_available_memory_and_does_not_double_count_managed_memory()
    {
        var probe = new Probe(12 * GiB, new(6 * GiB, 4 * GiB));
        var guard = new AutoRunMemoryGuard(new(), probe);

        var snapshot = guard.Snapshot();

        Assert.Equal(12 * GiB, snapshot.AvailableBytes);
        Assert.Equal(6 * GiB, snapshot.ProcessBytes);
        Assert.Equal(4 * GiB, snapshot.ManagedBytes);
        Assert.Equal(12 * GiB * 80 / 100, snapshot.LimitBytes);
        Assert.Equal(snapshot.LimitBytes - 6 * GiB, snapshot.HeadroomBytes);
        Assert.False(snapshot.IsUnderPressure);
        Assert.True(guard.CanAllocate());
        Assert.Equal(1, probe.AvailableReads);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 8)]
    public void Explicit_limit_can_lower_but_never_raise_the_automatic_threshold(long limitGiB, long expectedGiB)
    {
        var guard = new AutoRunMemoryGuard(new() { LimitBytes = limitGiB * GiB },
            new Probe(10 * GiB, new(0, 0)));

        Assert.Equal(expectedGiB * GiB, guard.Snapshot().LimitBytes);
    }

    [Fact]
    public void Reserved_free_memory_can_lower_the_percentage_limit()
    {
        var guard = new AutoRunMemoryGuard(new() { MinimumFreeBytes = 4 * GiB },
            new Probe(10 * GiB, new(2 * GiB, GiB)));

        Assert.Equal(6 * GiB, guard.Snapshot().LimitBytes);
        Assert.Equal(4 * GiB, guard.Snapshot().HeadroomBytes);
    }

    [Fact]
    public void Projected_allocation_stops_at_the_threshold_without_overflow()
    {
        var guard = new AutoRunMemoryGuard(new() { LimitBytes = 3 * GiB },
            new Probe(12 * GiB, new(2 * GiB, GiB)));

        Assert.True(guard.CanAllocate(GiB - 1));
        Assert.False(guard.CanAllocate(GiB));
        Assert.False(guard.CanAllocate(long.MaxValue));
        Assert.Throws<AutoRunMemoryLimitException>(() => guard.ThrowIfUnsafe(GiB));
        Assert.Throws<ArgumentOutOfRangeException>(() => guard.CanAllocate(-1));
    }

    [Fact]
    public void Managed_usage_is_used_when_larger_and_pressure_recovers_as_memory_is_released()
    {
        var probe = new Probe(10 * GiB, new(3 * GiB, 8 * GiB));
        var guard = new AutoRunMemoryGuard(new(), probe);

        Assert.True(guard.Snapshot().IsUnderPressure);
        Assert.Equal(0, guard.Snapshot().HeadroomBytes);
        Assert.False(guard.CanAllocate());

        probe.Usage = new(4 * GiB, 2 * GiB);
        Assert.False(guard.Snapshot().IsUnderPressure);
        Assert.Equal(4 * GiB, guard.Snapshot().HeadroomBytes);
        Assert.True(guard.CanAllocate(GiB));
        Assert.Equal(1, probe.AvailableReads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(128 * MiB)]
    public void Unavailable_or_too_small_total_memory_stops_collection(long availableBytes)
    {
        var guard = new AutoRunMemoryGuard(new(), new Probe(availableBytes, new(0, 0)));

        Assert.Equal(0, guard.Snapshot().LimitBytes);
        Assert.True(guard.Snapshot().IsUnderPressure);
        Assert.False(guard.CanAllocate());
    }

    [Fact]
    public void Measurement_failures_fail_closed_even_when_an_explicit_limit_is_set()
    {
        var unavailable = new Probe(12 * GiB, new(GiB, GiB))
        {
            AvailableError = new InvalidOperationException("Cannot read runtime memory")
        };
        var guard = new AutoRunMemoryGuard(new() { LimitBytes = 4 * GiB }, unavailable);
        Assert.Equal(0, guard.Snapshot().AvailableBytes);
        Assert.False(guard.CanAllocate());

        var probe = new Probe(12 * GiB, new(GiB, GiB))
        {
            UsageError = new InvalidOperationException("Cannot read process memory")
        };
        guard = new AutoRunMemoryGuard(new(), probe);
        Assert.Equal(-1, guard.Snapshot().ProcessBytes);
        Assert.Equal(-1, guard.Snapshot().ManagedBytes);
        Assert.True(guard.Snapshot().IsUnderPressure);
        Assert.Throws<AutoRunMemoryLimitException>(() => guard.ThrowIfUnsafe());

        probe.UsageError = null;
        Assert.True(guard.CanAllocate());
    }

    [Fact]
    public void Out_of_memory_failures_are_not_swallowed()
    {
        var probe = new Probe(12 * GiB, new(GiB, GiB)) { AvailableError = new OutOfMemoryException() };
        Assert.Throws<OutOfMemoryException>(() => new AutoRunMemoryGuard(new(), probe));

        probe.AvailableError = null;
        var guard = new AutoRunMemoryGuard(new(), probe);
        probe.UsageError = new OutOfMemoryException();
        Assert.Throws<OutOfMemoryException>(() => guard.Snapshot());
    }

    [Fact]
    public void Large_available_memory_does_not_overflow_percentage_calculation()
    {
        var guard = new AutoRunMemoryGuard(new() { MaxUsagePercent = 99, MinimumFreeBytes = 0 },
            new Probe(long.MaxValue, new(0, 0)));

        Assert.Equal((long)((decimal)long.MaxValue * 99 / 100), guard.Snapshot().LimitBytes);
        Assert.True(guard.CanAllocate(GiB));
    }

    [Theory]
    [InlineData(-1, 80, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 100, 0)]
    [InlineData(0, 80, -1)]
    public void Invalid_options_are_rejected(long limitBytes, int maxUsagePercent, long minimumFreeBytes)
    {
        var options = new AutoRunMemoryOptions
        {
            LimitBytes = limitBytes, MaxUsagePercent = maxUsagePercent, MinimumFreeBytes = minimumFreeBytes
        };

        Assert.False(options.IsValid());
        Assert.Throws<ArgumentException>(() => new AutoRunMemoryGuard(options, new Probe(12 * GiB, new(0, 0))));
    }

    private sealed class Probe(long availableBytes, AutoRunMemoryUsage usage) : IAutoRunMemoryProbe
    {
        public AutoRunMemoryUsage Usage { get; set; } = usage;
        public Exception? AvailableError { get; set; }
        public Exception? UsageError { get; set; }
        public int AvailableReads { get; private set; }

        public long GetAvailableBytes()
        {
            AvailableReads++;
            if (AvailableError is not null) throw AvailableError;
            return availableBytes;
        }

        public AutoRunMemoryUsage GetUsage()
        {
            if (UsageError is not null) throw UsageError;
            return Usage;
        }
    }
}
