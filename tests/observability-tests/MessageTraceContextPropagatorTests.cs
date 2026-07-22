using System.Diagnostics;
using System.Text;
using OpenTelemetry;

namespace ImagingPipeline.Observability.Tests;

public sealed class MessageTraceContextPropagatorTests
{
    private readonly W3CMessageTraceContextPropagator propagator = new();

    [Fact]
    public void InjectAndExtract_RoundTripsW3CContextAndPreservesBusinessHeaders()
    {
        var traceId = ActivityTraceId.CreateFromString("0af7651916cd43dd8448eb211c80319c".AsSpan());
        var spanId = ActivitySpanId.CreateFromString("b7ad6b7169203331".AsSpan());
        var context = new ActivityContext(
            traceId,
            spanId,
            ActivityTraceFlags.Recorded,
            traceState: "vendor=value");
        var headers = new Dictionary<string, object?>
        {
            ["business-header"] = "keep-me",
            ["TraceParent"] = Encoding.UTF8.GetBytes("stale"),
            ["TRACESTATE"] = "stale"
        };

        propagator.Inject(headers, context);

        Assert.Equal("keep-me", headers["business-header"]);
        Assert.DoesNotContain(headers.Keys, key => key is "TraceParent" or "TRACESTATE");
        Assert.Equal(
            "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            Utf8(headers["traceparent"]));
        Assert.Equal("vendor=value", Utf8(headers["tracestate"]));

        var extracted = propagator.Extract(headers);
        Assert.Equal(traceId, extracted.ActivityContext.TraceId);
        Assert.Equal(spanId, extracted.ActivityContext.SpanId);
        Assert.Equal(ActivityTraceFlags.Recorded, extracted.ActivityContext.TraceFlags);
        Assert.True(extracted.ActivityContext.IsRemote);
    }

    [Fact]
    public void Extract_AcceptsCaseInsensitiveStringAndMemoryHeaders()
    {
        var headers = new Dictionary<string, object?>
        {
            ["TraceParent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ["TraceState"] = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("vendor=value"))
        };

        var extracted = propagator.Extract(headers);

        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", extracted.ActivityContext.TraceId.ToHexString());
        Assert.Equal("00f067aa0ba902b7", extracted.ActivityContext.SpanId.ToHexString());
        Assert.Equal("vendor=value", extracted.ActivityContext.TraceState);
    }

    [Theory]
    [InlineData("not-a-trace-parent")]
    [InlineData("")]
    public void Extract_MalformedContextIsTreatedAsMissing(string traceParent)
    {
        var headers = new Dictionary<string, object?> { ["traceparent"] = traceParent };

        var extracted = propagator.Extract(headers);

        Assert.Equal(default, extracted.ActivityContext);
    }

    [Fact]
    public void InjectCurrent_UsesTheActiveSpan()
    {
        using var activity = new Activity("test").Start();
        var headers = new Dictionary<string, object?>();

        propagator.InjectCurrent(headers);
        var extracted = propagator.Extract(headers);

        Assert.Equal(activity.TraceId, extracted.ActivityContext.TraceId);
        Assert.Equal(activity.SpanId, extracted.ActivityContext.SpanId);
    }

    [Fact]
    public void InjectAndExtract_RoundTripsBaggage()
    {
        var previous = Baggage.Current;
        try
        {
            Baggage.SetBaggage("tenant", "north");
            var context = new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded);
            var headers = new Dictionary<string, object?>();

            propagator.Inject(headers, context);
            var extracted = propagator.Extract(headers);

            Assert.Contains("tenant=north", Utf8(headers["baggage"]), StringComparison.Ordinal);
            Assert.Equal("north", extracted.Baggage.GetBaggage("tenant"));
        }
        finally
        {
            Baggage.Current = previous;
        }
    }

    [Fact]
    public void Baggage_IsAllowlistedAndSizeBounded()
    {
        var previous = Baggage.Current;
        try
        {
            Baggage.SetBaggage("tenant", "north");
            Baggage.SetBaggage("secret", "must-not-propagate");
            Baggage.SetBaggage(
                TelemetryAttributeNames.PipelineRequestId,
                new string('x', 300));
            var headers = new Dictionary<string, object?>();

            propagator.Inject(headers, default);
            var extracted = propagator.Extract(headers);

            var baggageHeader = Utf8(headers["baggage"]);
            Assert.Contains("tenant=north", baggageHeader, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", baggageHeader, StringComparison.Ordinal);
            Assert.Null(extracted.Baggage.GetBaggage("secret"));
            Assert.Null(extracted.Baggage.GetBaggage(TelemetryAttributeNames.PipelineRequestId));
        }
        finally
        {
            Baggage.Current = previous;
        }
    }

    [Fact]
    public void OversizedExternalBaggage_IsDroppedWithoutDroppingTraceContext()
    {
        var headers = new Dictionary<string, object?>
        {
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ["baggage"] = Encoding.UTF8.GetBytes($"tenant={new string('x', 2_100)}")
        };

        var extracted = propagator.Extract(headers);

        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", extracted.ActivityContext.TraceId.ToHexString());
        Assert.Empty(extracted.Baggage.GetBaggage());
    }

    private static string Utf8(object? value) =>
        Encoding.UTF8.GetString(Assert.IsType<byte[]>(value));
}
