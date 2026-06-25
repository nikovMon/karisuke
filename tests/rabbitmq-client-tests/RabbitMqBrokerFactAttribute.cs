using System.Net.Sockets;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqBrokerFactAttribute : FactAttribute
{
    public RabbitMqBrokerFactAttribute()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("localhost", 5672);
            if (!connect.Wait(TimeSpan.FromMilliseconds(500)) || !client.Connected)
            {
                Skip = "RabbitMQ is not reachable on localhost:5672.";
            }
        }
        catch
        {
            Skip = "RabbitMQ is not reachable on localhost:5672.";
        }
    }
}
