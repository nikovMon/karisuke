using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.AutoRuns;

public sealed class AutoRunMemoryOptions
{
    // Zero derives the limit from the runtime's container-aware available-memory value.
    public long LimitBytes { get; set; }
    public int MaxUsagePercent { get; set; } = 80;
    public long MinimumFreeBytes { get; set; } = 256L * 1024 * 1024;

    public bool IsValid() => LimitBytes >= 0 && MaxUsagePercent is > 0 and < 100 && MinimumFreeBytes >= 0;
}

public sealed record AutoRunMemorySnapshot(
    long AvailableBytes,
    long ProcessBytes,
    long ManagedBytes,
    long LimitBytes,
    long HeadroomBytes,
    bool IsUnderPressure);

public interface IAutoRunMemoryGuard
{
    AutoRunMemorySnapshot Snapshot();
    bool CanAllocate(long additionalBytes = 0);
    void ThrowIfUnsafe(long additionalBytes = 0);
}

public sealed class AutoRunMemoryLimitException : Exception
{
    public AutoRunMemoryLimitException() : base(
        "Auto Run stopped collecting overlays because the service is approaching its memory limit or memory availability could not be measured. Already collected overlays can still be prepared for publication.") { }
}

public readonly record struct AutoRunMemoryUsage(long ProcessBytes, long ManagedBytes);

/// <summary>Supplies independent process and managed-memory measurements without counting either twice.</summary>
public interface IAutoRunMemoryProbe
{
    long GetAvailableBytes();
    AutoRunMemoryUsage GetUsage();
}

/// <summary>
/// An admission check for Auto Run allocations, not a process-wide hard memory cap.
/// Other requests, native allocations and garbage collection can change usage between checks.
/// </summary>
public sealed class AutoRunMemoryGuard : IAutoRunMemoryGuard
{
    private readonly IAutoRunMemoryProbe _probe;
    private readonly long _availableBytes;
    private readonly long _limitBytes;

    public AutoRunMemoryGuard(IOptions<AutoRunOptions> options)
        : this(options.Value.Memory, new RuntimeMemoryProbe()) { }

    public AutoRunMemoryGuard(AutoRunMemoryOptions options, IAutoRunMemoryProbe probe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        if (!options.IsValid())
            throw new ArgumentException("Auto Run memory settings are invalid.", nameof(options));

        _probe = probe;
        try
        {
            // Read once at singleton creation. GC reports its effective available memory,
            // which can be lower than the container's limit if the runtime has its own cap.
            _availableBytes = Math.Max(0, probe.GetAvailableBytes());
        }
        catch (Exception exception) when (IsMeasurementFailure(exception))
        {
            _availableBytes = 0;
        }

        // Split before multiplying to avoid overflowing for large reported limits.
        var percentageLimit = _availableBytes / 100 * options.MaxUsagePercent +
                              _availableBytes % 100 * options.MaxUsagePercent / 100;
        _limitBytes = Math.Min(percentageLimit, Math.Max(0, _availableBytes - options.MinimumFreeBytes));
        if (options.LimitBytes > 0)
            _limitBytes = Math.Min(_limitBytes, options.LimitBytes);
    }

    public AutoRunMemorySnapshot Snapshot()
    {
        AutoRunMemoryUsage usage;
        try
        {
            usage = _probe.GetUsage();
        }
        catch (Exception exception) when (IsMeasurementFailure(exception))
        {
            // Negative values explicitly mark an unavailable measurement for diagnostics.
            return new(_availableBytes, -1, -1, _limitBytes, 0, true);
        }

        if (usage.ProcessBytes < 0 || usage.ManagedBytes < 0)
            return new(_availableBytes, usage.ProcessBytes, usage.ManagedBytes, _limitBytes, 0, true);

        // The managed heap is already part of process memory; summing double-counts it.
        // Taking the maximum also protects against swapped-out managed pages understating usage.
        var usedBytes = Math.Max(usage.ProcessBytes, usage.ManagedBytes);
        var headroom = Math.Max(0, _limitBytes - usedBytes);
        return new(_availableBytes, usage.ProcessBytes, usage.ManagedBytes, _limitBytes, headroom,
            _limitBytes == 0 || usedBytes >= _limitBytes);
    }

    public bool CanAllocate(long additionalBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalBytes);
        var snapshot = Snapshot();
        // Compare remaining bytes rather than adding projected usage, avoiding overflow.
        return !snapshot.IsUnderPressure && additionalBytes < snapshot.HeadroomBytes;
    }

    public void ThrowIfUnsafe(long additionalBytes = 0)
    {
        if (!CanAllocate(additionalBytes))
            throw new AutoRunMemoryLimitException();
    }

    private static bool IsMeasurementFailure(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private sealed class RuntimeMemoryProbe : IAutoRunMemoryProbe
    {
        public long GetAvailableBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        public AutoRunMemoryUsage GetUsage()
        {
            using var process = Process.GetCurrentProcess();
            return new(process.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false));
        }
    }
}
