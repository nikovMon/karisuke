using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.DlqRecovery;

public sealed class RabbitMqRecoverySessionFactory(RecoveryOptions options) : IRecoverySessionFactory
{
    public async Task<uint> InspectAsync(RecoveryQueueOptions mapping, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        await using var connection = await ConnectAsync(mapping.SourceConnection, timeout.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
        return (await channel.QueueDeclarePassiveAsync(mapping.SourceQueue, timeout.Token)).MessageCount;
    }

    public async Task<IRecoverySession> OpenAsync(RecoveryQueueOptions mapping, CancellationToken cancellationToken)
    {
        var session = new Session(options, mapping);
        try
        {
            using var timeout = Timeout(cancellationToken);
            await session.InitializeAsync(this, timeout.Token);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private CancellationTokenSource Timeout(CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.OperationTimeoutSeconds));
        return timeout;
    }

    private Task<IConnection> ConnectAsync(string reference, CancellationToken token)
    {
        var connection = options.GetConnection(reference);
        var factory = new ConnectionFactory
        {
            HostName = connection.Host,
            Port = connection.Port,
            VirtualHost = connection.VirtualHost,
            UserName = connection.Username,
            Password = connection.Password,
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            ConsumerDispatchConcurrency = 1,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
            ContinuationTimeout = TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
            Ssl = new SslOption
            {
                Enabled = connection.TlsEnabled,
                ServerName = connection.TlsServerName ?? connection.Host
            }
        };
        return factory.CreateConnectionAsync("dlq-recovery", token);
    }

    private sealed class Session(RecoveryOptions options, RecoveryQueueOptions mapping) : IRecoverySession
    {
        private readonly Channel<RecoveryDelivery> _deliveries = Channel.CreateBounded<RecoveryDelivery>(1);
        private IConnection? _sourceConnection;
        private IConnection? _targetConnection;
        private IChannel? _consumerChannel;
        private IChannel? _retentionChannel;
        private IChannel? _replayChannel;
        private string? _consumerTag;
        public uint InitialCount { get; private set; }

        public async Task InitializeAsync(RabbitMqRecoverySessionFactory factory, CancellationToken token)
        {
            _sourceConnection = await factory.ConnectAsync(mapping.SourceConnection, token);
            _consumerChannel = await _sourceConnection.CreateChannelAsync(cancellationToken: token);
            // Acquire ownership BEFORE taking the snapshot. The exclusive lock is deleted on connection close.
            // This also coordinates managed one-off jobs; CronJob Forbid only coordinates its own schedule.
            var lockName = "dlq-recovery.lock." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mapping.SourceQueue)));
            await _consumerChannel.QueueDeclareAsync(lockName, durable: false, exclusive: true, autoDelete: true,
                arguments: null, cancellationToken: token);
            var snapshot = await _consumerChannel.QueueDeclarePassiveAsync(mapping.SourceQueue, token);
            if (snapshot.ConsumerCount != 0)
                throw new InvalidOperationException("The DLQ already has a consumer.");
            InitialCount = snapshot.MessageCount;
            if (InitialCount == 0) return;

            // Retention queues are provisioned explicitly, never recreated with guessed expiry/overflow policies.
            await _consumerChannel.QueueDeclarePassiveAsync(mapping.ManualQueue, token);
            _targetConnection = mapping.ReplayConnection == mapping.SourceConnection
                ? _sourceConnection : await factory.ConnectAsync(mapping.ReplayConnection, token);
            var confirmOptions = new CreateChannelOptions(publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);
            _replayChannel = await _targetConnection.CreateChannelAsync(confirmOptions, token);
            _retentionChannel = await _sourceConnection.CreateChannelAsync(confirmOptions, token);
            await _replayChannel.QueueDeclarePassiveAsync(mapping.ReplayQueue, token);
            await _consumerChannel.BasicQosAsync(0, 1, false, token);
            var consumer = new AsyncEventingBasicConsumer(_consumerChannel);
            consumer.ReceivedAsync += (_, args) =>
            {
                // Copy before returning: RabbitMQ owns the delivery buffer after this callback completes.
                var properties = new BasicProperties(args.BasicProperties);
                if (properties.Headers is not null)
                    properties.Headers = new Dictionary<string, object?>(properties.Headers, StringComparer.Ordinal);
                if (!_deliveries.Writer.TryWrite(new RecoveryDelivery(args.DeliveryTag, args.Body.ToArray(), properties)))
                    _deliveries.Writer.TryComplete(new InvalidOperationException("Unexpected delivery beyond prefetch."));
                return Task.CompletedTask;
            };
            consumer.ShutdownAsync += (_, _) =>
            {
                _deliveries.Writer.TryComplete(new IOException("Source consumer channel closed."));
                return Task.CompletedTask;
            };
            consumer.UnregisteredAsync += (_, _) =>
            {
                _deliveries.Writer.TryComplete();
                return Task.CompletedTask;
            };
            _consumerTag = await _consumerChannel.BasicConsumeAsync(mapping.SourceQueue, autoAck: false,
                consumerTag: string.Empty, noLocal: false, exclusive: true, arguments: null,
                consumer: consumer, cancellationToken: token);
        }

        public async Task<RecoveryDelivery> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var timeout = Timeout(cancellationToken);
            return await _deliveries.Reader.ReadAsync(timeout.Token);
        }

        public async Task<uint> ReadInputDepthAsync(CancellationToken cancellationToken)
        {
            using var timeout = Timeout(cancellationToken);
            return (await _replayChannel!.QueueDeclarePassiveAsync(mapping.ReplayQueue, timeout.Token)).MessageCount;
        }

        public Task PublishReplayAsync(RecoveryDelivery delivery, CancellationToken cancellationToken) =>
            PublishAsync(_replayChannel!, mapping.ReplayQueue, delivery, cancellationToken);

        public Task PublishManualAsync(RecoveryDelivery delivery, CancellationToken cancellationToken) =>
            PublishAsync(_retentionChannel!, mapping.ManualQueue, delivery, cancellationToken);

        private async Task PublishAsync(IChannel channel, string queue, RecoveryDelivery delivery, CancellationToken token)
        {
            using var timeout = Timeout(token);
            // Tracked confirmations throw on broker NACK and mandatory basic.return (unroutable).
            await channel.BasicPublishAsync(string.Empty, queue, mandatory: true,
                basicProperties: delivery.Properties, body: delivery.Body, cancellationToken: timeout.Token);
        }

        public async Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken)
        {
            using var timeout = Timeout(cancellationToken);
            await _consumerChannel!.BasicAckAsync(deliveryTag, multiple: false, timeout.Token);
        }

        private CancellationTokenSource Timeout(CancellationToken token)
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.OperationTimeoutSeconds));
            return timeout;
        }

        public async ValueTask DisposeAsync()
        {
            // Closing requeues any prefetched extra arrival or interrupted transfer. Never ACK on shutdown.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.ShutdownReserveSeconds));
            try
            {
                if (_consumerChannel is { IsOpen: true } && _consumerTag is not null)
                    await _consumerChannel.BasicCancelAsync(_consumerTag, noWait: false, timeout.Token);
            }
            catch { /* A broken connection already returns unsettled deliveries to the broker. */ }
            _deliveries.Writer.TryComplete();
            foreach (var channel in new[] { _consumerChannel, _retentionChannel, _replayChannel })
            {
                if (channel is null) continue;
                try { await channel.CloseAsync(cancellationToken: timeout.Token); }
                catch { /* Dispose below aborts a failed channel. */ }
                channel.Dispose();
            }
            if (_targetConnection is not null && !ReferenceEquals(_targetConnection, _sourceConnection))
            {
                try { await _targetConnection.CloseAsync(cancellationToken: timeout.Token); }
                catch { /* Dispose the failed connection. */ }
                _targetConnection.Dispose();
            }
            if (_sourceConnection is not null)
            {
                try { await _sourceConnection.CloseAsync(cancellationToken: timeout.Token); }
                catch { /* Dispose also releases the exclusive lock. */ }
                _sourceConnection.Dispose();
            }
        }
    }
}
