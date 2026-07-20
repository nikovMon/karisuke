using RabbitMQ.Client;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class RabbitMqBrokerFixture : IAsyncLifetime
{
    private readonly List<string> _queues = [];
    private readonly List<string> _exchanges = [];

    public string CreateName(string suffix) => $"karisuke.test.tbpublisher.{suffix}.{Guid.NewGuid():N}";

    public void TrackQueue(string queue) => _queues.Add(queue);

    public void TrackExchange(string exchange)
    {
        if (!string.IsNullOrWhiteSpace(exchange))
        {
            _exchanges.Add(exchange);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_queues.Count == 0 && _exchanges.Count == 0)
        {
            return;
        }

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = "localhost",
                Port = 5672,
                UserName = "admin",
                Password = "admin",
                VirtualHost = "/"
            };

            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            foreach (var queue in _queues.Distinct())
            {
                try
                {
                    await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
                }
                catch
                {
                }
            }

            foreach (var exchange in _exchanges.Distinct())
            {
                try
                {
                    await channel.ExchangeDeleteAsync(exchange, ifUnused: false);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
