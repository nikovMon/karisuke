using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace ImagingPipeline.Observability.Tests;

public sealed class MessagingTimingHeadersTests
{
    [Fact]
    public void StampPublishedReplacesThePreviousHopTimestamp()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var headers = new Dictionary<string, object?>
        {
            [MessagingTimingHeaders.PublishedUnixMilliseconds] = 7_500L,
            ["FINDAIR-PUBLISHED-AT-UNIX-MS"] = 5_000L,
            [PipelineTimingHeaders.StartUnixMilliseconds] = 1_000L
        };

        MessagingTimingHeaders.StampPublished(headers, clock);

        Assert.DoesNotContain("FINDAIR-PUBLISHED-AT-UNIX-MS", headers.Keys);
        Assert.Equal(10_000L, headers[MessagingTimingHeaders.PublishedUnixMilliseconds]);
        Assert.Equal(1_000L, headers[PipelineTimingHeaders.StartUnixMilliseconds]);
    }

    [Fact]
    public void TryGetDeliveryDelaySecondsReadsRabbitMqByteHeader()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(15_000));
        var headers = new Dictionary<string, object?>
        {
            [MessagingTimingHeaders.PublishedUnixMilliseconds] = Encoding.UTF8.GetBytes("12500")
        };

        var found = MessagingTimingHeaders.TryGetDeliveryDelaySeconds(headers, out var delay, clock);

        Assert.True(found);
        Assert.Equal(2.5, delay);
    }

    [Fact]
    public void TryReadPublishedUnixMillisecondsPreservesSupportedTypesAndStrictParsing()
    {
        var unsigned = new Dictionary<string, object?>
        {
            [MessagingTimingHeaders.PublishedUnixMilliseconds] = 12_500U
        };
        var paddedUtf8 = new Dictionary<string, object?>
        {
            [MessagingTimingHeaders.PublishedUnixMilliseconds] = Encoding.UTF8.GetBytes(" 12500 ")
        };
        var trailingData = new Dictionary<string, object?>
        {
            [MessagingTimingHeaders.PublishedUnixMilliseconds] = Encoding.UTF8.GetBytes("12500ms")
        };

        Assert.True(MessagingTimingHeaders.TryReadPublishedUnixMilliseconds(unsigned, out var unsignedValue));
        Assert.Equal(12_500L, unsignedValue);
        Assert.True(MessagingTimingHeaders.TryReadPublishedUnixMilliseconds(paddedUtf8, out var paddedValue));
        Assert.Equal(12_500L, paddedValue);
        Assert.False(MessagingTimingHeaders.TryReadPublishedUnixMilliseconds(trailingData, out _));
    }

    [Fact]
    public void TryGetDeliveryDelaySecondsRejectsTimestampBeyondClockSkewOrMalformedTimestamp()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var future = new Dictionary<string, object?>
        {
            [MessagingTimingHeaders.PublishedUnixMilliseconds] = 15_001L
        };
        var malformed = new Dictionary<string, object?>
        {
            [MessagingTimingHeaders.PublishedUnixMilliseconds] = "later"
        };

        Assert.False(MessagingTimingHeaders.TryGetDeliveryDelaySeconds(future, out _, clock));
        Assert.False(MessagingTimingHeaders.TryGetDeliveryDelaySeconds(malformed, out _, clock));
    }

    [Fact]
    public void TryGetDeliveryDelaySecondsAcceptsSmallCrossNodeClockSkewAndClampsToZero()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var headers = new Dictionary<string, object?>
        {
            [MessagingTimingHeaders.PublishedUnixMilliseconds] = 11_000L
        };

        var found = MessagingTimingHeaders.TryGetDeliveryDelaySeconds(headers, out var delay, clock);

        Assert.True(found);
        Assert.Equal(0, delay);
    }

    [Fact]
    public void TryGetDeliveryDelaySecondsRejectsImplausiblyOldTimestamp()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero));
        var headers = new Dictionary<string, object?>
        {
            [MessagingTimingHeaders.PublishedUnixMilliseconds] =
                clock.GetUtcNow().Subtract(TimeSpan.FromDays(2)).ToUnixTimeMilliseconds()
        };

        Assert.False(MessagingTimingHeaders.TryGetDeliveryDelaySeconds(headers, out _, clock));
    }
}
