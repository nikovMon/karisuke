# ImagingPipeline RabbitMQ client

`ImagingPipeline.RabbitMqClient` is a DI-friendly, asynchronous RabbitMQ client built on
`RabbitMQ.Client` 7.2.1. It provides confirmed publishing, single-message
consumption, manual acknowledgements, DLX/DLQ failure routing, durable
queue/exchange declaration, and automatic connection/topology recovery.

## Register and configure

Use publisher-only registration for apps that only publish:

```csharp
builder.Services.AddRabbitMqPublisher(builder.Configuration);
```

Use consumer registration for worker apps that consume and route outcomes:

```csharp
builder.Services.AddRabbitMqConsumer(builder.Configuration);
```

`AddRabbitMqClient(...)` registers the full publisher + consumer surface.

Publisher-only configuration:

```json
{
  "RabbitMq": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "admin",
    "Password": "admin",
    "VirtualHost": "/",
    "InputQueue": "int.algo.gateway_rules",
    "InputExchange": "",
    "InputRoutingKey": "int.algo.gateway_rules",
    "HeadersArguments": {
      "x-dead-letter-exchange": "",
      "x-dead-letter-routing-key": "int.algo.gateway_rules.dlq"
    },
    "PublisherChannelPoolSize": 4,
    "OutputPublishConcurrency": 4,
    "ReconnectDelaySeconds": 5
  }
}
```

Consumer configuration:

```json
{
  "RabbitMq": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "admin",
    "Password": "admin",
    "VirtualHost": "/",
    "InputQueue": "int.algo.gateway_rules",
    "OutputQueue": "int.algo.gateway_rules.output",
    "RetryQueue": "int.algo.gateway_rules.retry",
    "DeadLetterQueue": "int.algo.gateway_rules.dlq",
    "InputExchange": "",
    "OutputExchange": "",
    "RetryExchange": "",
    "DeadLetterExchange": "",
    "InputExchangeType": "direct",
    "OutputExchangeType": "direct",
    "RetryExchangeType": "headers",
    "DeadLetterExchangeType": "direct",
    "InputRoutingKey": "int.algo.gateway_rules",
    "OutputRoutingKey": "int.algo.gateway_rules.output",
    "RetryRoutingKey": "int.algo.gateway_rules.retry",
    "DeadLetterRoutingKey": "int.algo.gateway_rules.dlq",
    "HeadersArguments": {
      "x-message-ttl": 60000
    },
    "OutputQueueHeaders": {},
    "RetryQueueHeaders": {},
    "DeadLetterQueueHeaders": {},
    "InputExchangeHeaders": {},
    "OutputExchangeHeaders": {},
    "RetryExchangeHeaders": {},
    "DeadLetterExchangeHeaders": {},
    "PrefetchCount": 1,
    "ConsumerConcurrency": 1,
    "PublisherChannelPoolSize": 4,
    "OutputPublishConcurrency": 4,
    "RetryDelayMilliseconds": 10000,
    "MaxRetryAttempts": 3,
    "RetryCountHeader": "x-retry-count",
    "RetryQueues": [
      {
        "RetryCount": 1,
        "Queue": "int.algo.gateway_rules.retry.1",
        "DelayMilliseconds": 10000,
        "BindingArguments": {
          "x-match": "all",
          "x-retry-count": 1
        }
      },
      {
        "RetryCount": 2,
        "Queue": "int.algo.gateway_rules.retry.2",
        "DelayMilliseconds": 10000,
        "BindingArguments": {
          "x-match": "all",
          "x-retry-count": 2
        }
      },
      {
        "RetryCount": 3,
        "Queue": "int.algo.gateway_rules.retry.3",
        "DelayMilliseconds": 10000,
        "BindingArguments": {
          "x-match": "all",
          "x-retry-count": 3
        }
      }
    ],
    "ReconnectDelaySeconds": 5
  }
}
```

Every setting can be overridden by standard .NET environment variables, such
as `RabbitMq__Host` and `RabbitMq__InputQueue`.

### Configuration reference

Connection settings:

- `Host`, `Port`, `Username`, `Password`, and `VirtualHost` are passed directly
  to the RabbitMQ connection factory.
- `ReconnectDelaySeconds` is used as RabbitMQ client's network recovery
  interval after an established connection drops. The client does not run its
  own connection retry loop.

Queue settings:

- `InputQueue` is the queue consumed by `IRabbitMqConsumer` and the default
  target for `PublishToInputAsync`.
- `OutputQueue` receives successful handler output when
  `RabbitMqMessageProcessingResult.Success(outputBody)` is returned.
- `RetryQueue` is the legacy single retry queue. `RetryQueues` can declare one
  retry queue per retry count.
- `DeadLetterQueue` is declared as the DLQ and is bound to the configured DLX.
- Queues are always declared as durable, non-exclusive, and non-auto-delete.
- `HeadersArguments` is passed as declaration arguments for `InputQueue`.
- `OutputQueueHeaders`, `RetryQueueHeaders`, and `DeadLetterQueueHeaders` are
  declaration arguments for `OutputQueue`, retry queues, and
  `DeadLetterQueue`.

Exchange and routing settings:

- `InputExchange`, `OutputExchange`, `RetryExchange`, and
  `DeadLetterExchange` are declared when their names are not empty.
- `InputExchangeType`, `OutputExchangeType`, `RetryExchangeType`, and
  `DeadLetterExchangeType` default to `direct`. Use `topic` when using
  wildcard routing keys like `#`.
- `InputRoutingKey`, `OutputRoutingKey`, and `DeadLetterRoutingKey` bind queues
  to their exchanges. If a routing key is omitted, the matching queue name is
  used. `RetryRoutingKey` is the routing key used when publishing to
  `RetryExchange`; with a headers retry exchange, queue selection is done by
  headers instead.
- If an exchange name is empty, the client skips declaring that exchange and
  skips binding the queue to it. Publishing with an empty exchange uses
  RabbitMQ's default exchange.

DLQ settings:

- The input queue always needs `x-dead-letter-exchange` and
  `x-dead-letter-routing-key` so failed messages can move to the DLQ.
- The code adds those two input queue arguments automatically when they are not
  already present in `HeadersArguments`.
- If `HeadersArguments` contains `x-dead-letter-exchange` or
  `x-dead-letter-routing-key`, those values are used as the effective DLX and
  DLQ routing key.
- On non-retryable handler failure, the client does not republish the message.
  It sends `BasicNack(requeue: false)`, and RabbitMQ performs the dead-letter
  routing.

Retry settings:

- Retryable failures are republished after the client increments the
  `RetryCountHeader` header, which defaults to `x-retry-count`. The message body
  is not changed.
- When `RetryQueues` is configured, `RetryExchangeType` must be `headers`.
  The client publishes the retry message once to `RetryExchange`; RabbitMQ
  routes it to the retry queue whose binding matches `RetryCountHeader`.
  The topology builder adds `x-match = all` and the retry count header binding
  for each configured retry queue.
- If `RetryQueues` is empty, the legacy `RetryQueue` / `RetryRoutingKey` pair
  is used for every attempt.
- `RetryDelayMilliseconds` is the default retry queue `x-message-ttl`.
  A `RetryQueues` entry can override it with `DelayMilliseconds`. When the retry
  message expires, RabbitMQ dead-letters it back to `InputExchange` /
  `InputRoutingKey`.
- `MaxRetryAttempts` caps header-level retry attempts. After the cap is reached,
  the input is negatively acknowledged with `requeue: false` and routed to the
  DLQ. Set it to `0` to disable retry.
- If the retry count header exists but is not a non-negative integer, the
  message goes directly to the DLQ.

Operational settings:

- `PrefetchCount` limits how many unacknowledged messages RabbitMQ can deliver
  to each consumer channel at once. It must be greater than zero.
- `ConsumerConcurrency` controls how many consumer channels run in one process.
  It must be greater than zero. The maximum unacknowledged input messages per
  process is roughly `ConsumerConcurrency * PrefetchCount`.
- `PublisherChannelPoolSize` limits concurrent publisher channels in one
  process. Each publish leases one confirmed channel from this pool.
- `OutputPublishConcurrency` limits how many output messages from one handler
  result can be published in parallel before the input message is acknowledged.
- `RetryDelayMilliseconds`, `MaxRetryAttempts`, `RetryCountHeader`, and
  `RetryQueues` control retry queue delay, retry cap, the header used for retry
  count, and optional per-attempt retry routing.

Important RabbitMQ behavior:

- Queue and exchange declarations are idempotent only when the existing broker
  entity has the same durable/auto-delete/argument settings. Changing arguments
  on an already-created queue can cause RabbitMQ to reject the declaration with
  a precondition failure.
- Consumer channels declare the configured topology when consumption starts.
  Publisher channels declare the topology once when they are created, then reuse
  that channel for later publishes.

## Publish

```csharp
public sealed class Sender(IRabbitMqPublisher publisher)
{
    public Task SendAsync(string json, CancellationToken cancellationToken) =>
        publisher.PublishToInputAsync(
            RabbitMqMessageEnvelope.FromUtf8(json), cancellationToken);
}
```

Use `PublishAsync(exchange, routingKey, message, cancellationToken)` for an
explicit exchange/routing-key pair. The publisher leases a channel from a
bounded publish-channel pool, marks the message persistent, enables publisher
confirmations, and awaits the confirmation before returning.

## Consume one message at a time

```csharp
public sealed class Handler : IRabbitMqMessageHandler
{
    public Task<RabbitMqMessageProcessingResult> HandleAsync(
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        var output = Encoding.UTF8.GetBytes(message.BodyAsUtf8().ToUpperInvariant());
        return Task.FromResult(RabbitMqMessageProcessingResult.Success(output));
    }
}

await consumer.ConsumeAsync(handler, stoppingToken);
```

Use `RabbitMqMessageProcessingResult.RetryableFailure(error)` for transient
failures. Use `Failure(error)` or `NonRetryableFailure(error)` for validation
and other permanent failures that should go straight to the DLQ. Exceptions
thrown by handlers are treated as retryable failures.

## Scale and observability

- `PublisherChannelPoolSize` controls how many concurrent publish channels can
  be active per process. Publishing leases a channel exclusively and returns it
  to the pool after the publish confirmation.
- `OutputPublishConcurrency` controls bounded fan-out when a handler returns
  multiple output messages. Higher values can reduce latency for large fan-out
  results, but may publish a partial subset before a later failure causes the
  input message to be requeued.
- `PrefetchCount` limits how many unacknowledged messages RabbitMQ can deliver
  to each consumer channel at once.
- `ConsumerConcurrency` controls how many consumer channels process messages in
  parallel inside one process. Prefer modest values when the service also scales
  horizontally across pods.
- The library emits metrics through the `ImagingPipeline.RabbitMqClient` meter and traces
  through the `ImagingPipeline.RabbitMqClient` activity source. Configure OpenTelemetry in
  the hosting app to export them.
- Metrics include publish counts/failures/duration, consumed counts, handler
  failures, processing duration, ack/nack counts, retry/DLQ routing,
  publisher channel count, and connection failures/recoveries.

## Success, failure, and delivery behavior

- Success with an output body publishes to `OutputQueue`; only after that
  confirmed publish does the client acknowledge the input message. The output
  message has `RetryCountHeader` reset to `0` for the next service.
- Success with multiple output messages publishes them with bounded parallelism
  controlled by `OutputPublishConcurrency`; only after all confirmed publishes
  complete does the client acknowledge the input message. Each output message
  has `RetryCountHeader` reset to `0`.
- Non-retryable failures are negatively acknowledged with `requeue: false`.
  RabbitMQ routes them from `InputQueue` through the configured
  `DeadLetterExchange` into `DeadLetterQueue`.
- Retryable failures are published to the retry queue for the incremented
  `x-retry-count` header. If retry is disabled, exhausted, or the retry count
  header is invalid, they are routed to the DLQ instead.
- If output publishing or acknowledgement fails, the input is negatively
  acknowledged with requeue enabled to avoid silently losing it.

This provides **at-least-once delivery**, not exactly-once delivery. A process
failure between publishing output and acknowledging input can create a duplicate.
Handlers and downstream consumers should therefore be idempotent, usually using
`MessageId` as the deduplication key. When a delivered message has no broker
message id, the client derives a stable `body-sha256:...` id from the body.

## Connection recovery

RabbitMQ automatic connection and topology recovery handle unexpected
shutdowns, using `ReconnectDelaySeconds` as the network recovery interval. The
application-level consumer worker should also restart `ConsumeAsync` if it
exits. Failures are structured-log events; credentials and message bodies are
never logged by the library.
