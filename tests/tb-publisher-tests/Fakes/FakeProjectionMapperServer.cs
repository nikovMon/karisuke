using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ImagingPipeline.TbPublisher.Tests.Fakes;

public sealed class FakeProjectionMapperServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeProjectionMapperServer(Func<string, string> respond)
    {
        var port = GetFreeTcpPort();
        BaseUrl = $"http://localhost:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(() => LoopAsync(respond));
    }

    public string BaseUrl { get; }

    private async Task LoopAsync(Func<string, string> respond)
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var responseBody = respond(body);
            var bytes = Encoding.UTF8.GetBytes(responseBody);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch
        {
        }

        _listener.Close();
    }
}
