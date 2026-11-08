using System.Net.Sockets;

namespace ImagingPipeline.DlqRecovery.Tests;

public sealed class RecoveryBrokerFactAttribute : FactAttribute
{
    public RecoveryBrokerFactAttribute()
    {
        foreach (var port in new[] { 5672, 5673 })
        {
            if (!IsReachable(port))
            {
                Skip = $"RabbitMQ broker tests require localhost:5672 and localhost:5673; localhost:{port} is unavailable.";
                return;
            }
        }
    }

    private static bool IsReachable(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("localhost", port).Wait(TimeSpan.FromMilliseconds(500))
                && client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or AggregateException)
        {
            return false;
        }
    }
}
