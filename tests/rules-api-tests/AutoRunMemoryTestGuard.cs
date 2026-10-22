using ImagingPipeline.Rules.Api.AutoRuns;

namespace ImagingPipeline.Rules.Api.Tests;

internal sealed class AutoRunMemoryTestGuard : IAutoRunMemoryGuard
{
    public bool Blocked { get; set; }
    public AutoRunMemorySnapshot Snapshot() => new(10000, Blocked ? 9000 : 100, 100,
        8000, Blocked ? 0 : 7900, Blocked);
    public bool CanAllocate(long additionalBytes = 0) => !Blocked;
    public void ThrowIfUnsafe(long additionalBytes = 0)
    {
        if (Blocked) throw new AutoRunMemoryLimitException();
    }
}
