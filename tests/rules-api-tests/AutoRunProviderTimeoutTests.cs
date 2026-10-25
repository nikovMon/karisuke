using System.Net;
using System.Text;
using System.Text.Json;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunProviderTimeoutTests
{
    private const string Secret = "private-provider-token";
    private static readonly byte[] Prefix = Encoding.UTF8.GetBytes("{\"entities_list\":[");

    [Theory]
    [InlineData(true, false, "overlay lookup")]
    [InlineData(false, false, "initial search page")]
    [InlineData(false, true, "continuation page")]
    public async Task Timeout_cancels_stalled_headers_and_identifies_request_without_secrets(
        bool lookup, bool continuation, string operation)
    {
        using var handler = new AsyncHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var provider = Provider(client, 1);

        var error = await Assert.ThrowsAsync<ElasticProviderException>(() =>
            Send(provider, lookup, continuation).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains(operation, error.Message);
        Assert.Contains("waiting for response headers", error.Message);
        Assert.Contains("1 seconds (AutoRun.Provider.TimeoutSeconds)", error.Message);
        Assert.Contains("response bytes read: 0", error.Message);
        Assert.DoesNotContain(Secret, error.Message);
        Assert.DoesNotContain("provider.example", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Timeout_also_cancels_stalled_body_after_successful_headers_and_reports_received_bytes()
    {
        using var stream = new StalledResponseStream();
        using var handler = new AsyncHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        using var client = new HttpClient(handler);
        var provider = Provider(client, 1);

        var error = await Assert.ThrowsAsync<ElasticProviderException>(() =>
            Send(provider, lookup: false, continuation: true).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("continuation page", error.Message);
        Assert.Contains("reading the response body", error.Message);
        Assert.Contains($"response bytes read: {Prefix.Length}", error.Message);
        Assert.DoesNotContain(Secret, error.Message);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_during_headers_or_body_is_not_reported_as_provider_timeout(bool readingBody)
    {
        var enteredHeaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new StalledResponseStream();
        using var handler = new AsyncHandler(async (_, token) =>
        {
            if (!readingBody)
            {
                enteredHeaders.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var provider = Provider(client, 30);
        var pending = provider.SearchPageAsync(JsonSerializer.SerializeToElement(new { }), null, cancellation.Token);
        await (readingBody ? stream.Stalled.Task : enteredHeaders.Task).WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, handler.Calls);
        if (readingBody) Assert.True(stream.Disposed);
    }

    private static ElasticOverlayProvider Provider(HttpClient client, int timeoutSeconds) => new(client,
        Options.Create(new ElasticProviderOptions
        {
            BaseUrl = "https://provider.example",
            TimeoutSeconds = timeoutSeconds,
            Headers = new() { ["Authorization"] = "Bearer " + Secret }
        }));

    private static Task Send(ElasticOverlayProvider provider, bool lookup, bool continuation) => lookup
        ? provider.GetAsync(Secret, CancellationToken.None)
        : provider.SearchPageAsync(JsonSerializer.SerializeToElement(new { value = Secret }),
            continuation ? "/next?searchAfter=" + Secret : null, CancellationToken.None);

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return response(request, cancellationToken);
        }
    }

    private sealed class StalledResponseStream : Stream
    {
        private bool _prefixRead;
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefixRead)
            {
                Prefix.CopyTo(buffer);
                _prefixRead = true;
                return Prefix.Length;
            }
            Stalled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
