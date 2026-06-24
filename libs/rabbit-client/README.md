# ImagingPipeline RabbitMQ client

`ImagingPipeline.RabbitClient` is a DI-friendly, asynchronous RabbitMQ client built on
`RabbitMQ.Client` 7.2.1. It provides confirmed publishing, single-message and
batch consumption, manual acknowledgements, retry/error routing, durable queue
declaration, and automatic connection/topology recovery.

## Register and configure

Use publisher-only registration for apps that only publish:

```csharp
builder.Services.AddRabbitPublisher(builder.Configuration);
```

Use consumer registration for worker apps that consume and route outcomes:

```csharp
builder.Services.AddRabbitConsumer(builder.Configuration);
```

`AddRabbitClient(...)` remains available as the full publisher + consumer
registration for backwards compatibility.

Publisher-only configuration:

```json
{
  "RabbitMq": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest",
    "VirtualHost": "/",
    "InputQueue": "tb.input",
    "PublisherChannelPoolSize": 4,
    "ReconnectDelaySeconds": 5,
    "MaxRetryCount": 3
  }
}
```

Consumer configuration:

```json
{
  "RabbitMq": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest",
    "VirtualHost": "/",
    "InputQueue": "tb.input",
    "OutputQueue": "tb.output",
    "ErrorQueue": "tb.error",
    "PrefetchCount": 10,
    "ConsumerCount": 1,
    "BatchSize": 25,
    "PublisherChannelPoolSize": 4,
    "ReconnectDelaySeconds": 5,
    "MaxRetryCount": 3,
    "RequeueOnFailure": false
  }
}
```

Every setting can be overridden by standard .NET environment variables, such
as `RabbitMq__Host` and `RabbitMq__InputQueue`.

## Publish

```csharp
public sealed class Sender(IRabbitPublisher publisher)
{
    public Task SendAsync(string json, CancellationToken cancellationToken) =>
        publisher.PublishToInputAsync(
            RabbitMessageEnvelope.FromUtf8(json), cancellationToken);
}
```

Use `PublishAsync(queueName, message, cancellationToken)` for an explicit queue.
The publisher leases a channel from a bounded publish-channel pool, declares a
durable queue, marks the message persistent, enables publisher confirmations,
and awaits the confirmation before returning.

## Consume one message at a time

```csharp
public sealed class Handler : IRabbitMessageHandler
{
    public Task<RabbitMessageProcessingResult> HandleAsync(
        RabbitMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        var output = Encoding.UTF8.GetBytes(message.BodyAsUtf8().ToUpperInvariant());
        return Task.FromResult(RabbitMessageProcessingResult.Success(output));
    }
}

await consumer.ConsumeAsync(handler, stoppingToken);
```

## Consume batches

```csharp
public sealed class BatchHandler : IRabbitBatchMessageHandler
{
    public Task<IReadOnlyList<RabbitMessageProcessingResult>> HandleBatchAsync(
        IReadOnlyList<RabbitMessageEnvelope> messages,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RabbitMessageProcessingResult> results = messages
            .Select(message => RabbitMessageProcessingResult.Success(message.Body))
            .ToArray();
        return Task.FromResult(results);
    }
}

await batchConsumer.ConsumeAsync(batchHandler, stoppingToken);
```

The batch handler must return exactly one result per input. Batches fill up to
`BatchSize` and flush after a short collection window, so low-traffic queues do
not wait indefinitely. `PrefetchCount` bounds unacknowledged work and memory use.

## Scale and observability

- `PublisherChannelPoolSize` controls how many concurrent publish channels can
  be active per process. Publishing leases a channel exclusively and returns it
  to the pool after the publish confirmation.
- `ConsumerCount` controls how many single-message consumer channels run in one
  process. Handlers must be thread-safe when this value is greater than `1`.
- The library emits metrics through the `ImagingPipeline.RabbitClient` meter and traces
  through the `ImagingPipeline.RabbitClient` activity source. Configure OpenTelemetry in
  the hosting app to export them.
- Metrics include publish counts/failures/duration, consumed counts, handler
  failures, processing duration, ack/nack counts, retries, error routing,
  publisher channel count, and connection failures/recoveries.

## Success, failure, and delivery behavior

- Success with an output body publishes to `OutputQueue`; only after that
  confirmed publish does the client acknowledge the input message.
- With `RequeueOnFailure=false`, failures publish the original body and error
  metadata to `ErrorQueue`, then acknowledge the input.
- With `RequeueOnFailure=true`, failures are republished to `InputQueue` with an
  incremented `x-imagingpipeline-retry-count` header. After `MaxRetryCount`, they go to
  `ErrorQueue`.
- If output, retry, or error publishing fails, the input is negatively
  acknowledged with requeue enabled to avoid silently losing it.

This provides **at-least-once delivery**, not exactly-once delivery. A process
failure between publishing output and acknowledging input can create a duplicate.
Handlers and downstream consumers should therefore be idempotent, usually using
`MessageId` as the deduplication key.

## Connection recovery

Initial connections retry up to `MaxRetryCount` with
`ReconnectDelaySeconds` between attempts. RabbitMQ automatic connection and
topology recovery handle unexpected shutdowns. The application-level consumer
worker should also restart `ConsumeAsync` if it exits, as `tb-consumer` does.
Failures are structured-log events; credentials and message bodies are never
logged by the library.
