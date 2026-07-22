using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace ImagingPipeline.Observability.Tests;

public sealed class PipelineTimingHeadersTests
{
    [Fact]
    public void EnsureStarted_AddsOneOriginAndPreservesItAcrossStages()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero));
        var headers = new Dictionary<string, object?>();

        PipelineTimingHeaders.EnsureStarted(headers, clock);
        var origin = Assert.IsType<long>(headers[PipelineTimingHeaders.StartUnixMilliseconds]);
        clock.Advance(TimeSpan.FromSeconds(5));
        PipelineTimingHeaders.EnsureStarted(headers, clock);

        Assert.Equal(origin, headers[PipelineTimingHeaders.StartUnixMilliseconds]);
        Assert.True(PipelineTimingHeaders.TryGetElapsedSeconds(headers, out var elapsed, clock));
        Assert.Equal(5, elapsed);
    }

    [Fact]
    public void EnsureStarted_CanonicalizesValidOriginAndRepairsMalformedOrigin()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var valid = new Dictionary<string, object?>
        {
            ["X-PIPELINE-START-UNIX-MS"] = Encoding.UTF8.GetBytes("2500")
        };
        var malformed = new Dictionary<string, object?>
        {
            ["X-PIPELINE-START-UNIX-MS"] = "yesterday"
        };

        PipelineTimingHeaders.EnsureStarted(valid, clock);
        PipelineTimingHeaders.EnsureStarted(malformed, clock);

        Assert.Single(valid);
        Assert.Equal(2_500L, valid[PipelineTimingHeaders.StartUnixMilliseconds]);
        Assert.Single(malformed);
        Assert.Equal(10_000L, malformed[PipelineTimingHeaders.StartUnixMilliseconds]);
    }

    [Fact]
    public void EnsureStartedRemovesConflictingCaseVariantAndKeepsCanonicalOrigin()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var headers = new Dictionary<string, object?>
        {
            [PipelineTimingHeaders.StartUnixMilliseconds] = 2_500L,
            ["X-PIPELINE-START-UNIX-MS"] = 3_500L
        };

        PipelineTimingHeaders.EnsureStarted(headers, clock);

        Assert.Single(headers);
        Assert.Equal(2_500L, headers[PipelineTimingHeaders.StartUnixMilliseconds]);
    }

    [Fact]
    public void TryGetElapsedSeconds_AcceptsRabbitMqByteHeader()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(15_000));
        var headers = new Dictionary<string, object?>
        {
            [PipelineTimingHeaders.StartUnixMilliseconds] = Encoding.UTF8.GetBytes("12500")
        };

        var found = PipelineTimingHeaders.TryGetElapsedSeconds(headers, out var elapsed, clock);

        Assert.True(found);
        Assert.Equal(2.5, elapsed);
    }

    [Fact]
    public void TryGetElapsedSeconds_RejectsOriginBeyondClockSkewOrMalformedOrigin()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var future = new Dictionary<string, object?>
        {
            [PipelineTimingHeaders.StartUnixMilliseconds] = 15_001L
        };
        var malformed = new Dictionary<string, object?>
        {
            [PipelineTimingHeaders.StartUnixMilliseconds] = "yesterday"
        };

        Assert.False(PipelineTimingHeaders.TryGetElapsedSeconds(future, out _, clock));
        Assert.False(PipelineTimingHeaders.TryGetElapsedSeconds(malformed, out _, clock));
    }

    [Fact]
    public void TryGetElapsedSeconds_AcceptsSmallCrossNodeClockSkewAndClampsToZero()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var headers = new Dictionary<string, object?>
        {
            [PipelineTimingHeaders.StartUnixMilliseconds] = 11_000L
        };

        var found = PipelineTimingHeaders.TryGetElapsedSeconds(headers, out var elapsed, clock);

        Assert.True(found);
        Assert.Equal(0, elapsed);
    }

    [Fact]
    public void ImplausiblyOldOrigin_IsRejectedAndResetBeforeRepublishing()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero));
        var oldOrigin = clock.GetUtcNow().Subtract(TimeSpan.FromDays(2)).ToUnixTimeMilliseconds();
        var headers = new Dictionary<string, object?>
        {
            [PipelineTimingHeaders.StartUnixMilliseconds] = oldOrigin
        };

        Assert.False(PipelineTimingHeaders.TryGetElapsedSeconds(headers, out _, clock));

        PipelineTimingHeaders.EnsureStarted(headers, clock);

        Assert.Equal(
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            headers[PipelineTimingHeaders.StartUnixMilliseconds]);
    }
}
