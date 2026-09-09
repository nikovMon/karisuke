# Pipeline telemetry pattern

How a message handler emits the signals defined in
[observability-contract.md](observability-contract.md). The contract says which
metrics, spans, and logs must exist; this document says how handler code
produces them without burying the business logic in bookkeeping.

`apps/tb-consumer/Application/TbMessageHandler.cs` is the reference
implementation. TB Publisher and Gateway still use the older hand-rolled form
and are expected to migrate.

## The problem this solves

The original handlers tracked telemetry by hand: a `started` timestamp, an
`outcome` local, an `error` local, a mutable state object threaded into every
helper, and a large `finally` block that reconciled them. Roughly half of each
handler was bookkeeping, the reconciliation logic was copy-pasted between
services and had already drifted, and reading the business flow meant skipping
past it.

The pattern replaces that with three primitives in `libs/observability`.

## Primitives

### PipelineStageScope

One per message. Owns every stage-level metric so the handler never touches
`PipelineTelemetry` directly and never threads outcome state through helpers.

| Member | Purpose |
|---|---|
| `Begin(stage, ingressPayloadBytes)` | Starts the clock, records ingress payload size |
| `Rejected(error)` | Malformed or invalid; must not be retried |
| `Retryable(error)` | Failed for a reason another delivery may resolve |
| `Cancelled()` | Shutdown or caller cancellation |
| `Succeeded()` | Message fully processed |
| `Faulted()` | Default for an unexpected exception; returns whether it applied |
| `MessagePublished()` | Increments the published count |
| `RecordBatchSize`, `RecordEgressPayloadSize`, `RecordEndToEndDuration` | Stage-scoped passthroughs |
| `Dispose()` | Records egress messages, fan-out, ingress message, stage duration |

Rules:

- **Classify every path that returns.** The scope starts pessimistic
  (`failure` / `unknown`) and `Dispose` does not reinterpret that default.
  Reaching disposal unclassified means a path was missed.
- **Classification is first-wins.** The innermost classifier decides the
  outcome, so a specific cause such as `dependency` survives the catch-all on
  the way out. This is why `Faulted()` returns a `bool`: it applies only when
  nothing has classified yet, which keeps nested catch blocks idempotent.
- **Call `Faulted()` from the handler's catch-all**, so an unexpected exception
  becomes `retry` / `handler` instead of the meaningless `failure` / `unknown`.

### PipelineSpanScope

A stage span that tags itself on creation and sets `Ok` on disposal unless a
failure was reported. Replaces the `StartStageActivity` helper that had been
copy-pasted into three services.

| Member | Purpose |
|---|---|
| `StartStage(stage, operation, context)` | Internal span named `{stage}.{operation}`, e.g. `tb_consumer.projection` |
| `StartProducer(stage, name, context)` | Producer span under a caller-chosen name, for spans that cross into a downstream stage's vocabulary |
| `SetTag(key, value)` | Guarded by `IsAllDataRequested` internally |
| `Failed(category, exception, recordException)` | Marks the span failed |
| `Cancelled(exception)` | Marks the span cancelled |
| `Activity` | The underlying activity, for trace-context injection |

Rules:

- **Never guard tag writes at the call site.** `SetTag` already checks
  `IsAllDataRequested`; repeating that check is what made the old code noisy.
- **`recordException` stays explicit.** The pipeline attaches exception events
  only where the handler boundary does not already log the exception. The
  RabbitMQ handler boundary owns the exception-bearing error log, so spans
  inside a handler generally pass `recordException: false`.
- **A span's extent is the work it names.** See "Span extent" below.

### WorkloadDimensions

The five business dimensions every workload metric is sliced by — rule, tenant,
area, sensor, algorithms. Build it once per message and pass it to the
`WorkloadTelemetry` overloads instead of repeating five positional arguments.

Only `RecordTileBatch`, `RecordTiles`, and `RecordTilePublishAttempt` currently
have overloads, because those are the ones TB Consumer uses. Add the others
(`RecordTask`, `RecordTileRequest`, `RecordImage`) when the service that needs
them migrates; do not add unused overloads ahead of time.

### Supporting extensions

- `TelemetrySources.For(PipelineStage)` maps a stage to its `ActivitySource`.
- `AddPipelineContext(this Activity?, in TelemetryLogContext)` attaches a whole
  log context to a span, so a handler that already built its logging context
  does not repeat the same values argument by argument.

## Span extent

Whichever span is current when a log or metric is emitted becomes that record's
`span.id` and its metric exemplar target. Span extent therefore decides where a
signal shows up in a trace view, and it must be a decision rather than an
accident of syntax.

This is easy to get wrong in C#: `using var` extends to the end of the method,
while `using (...) { }` closes at the end of the block. A handler that mixes
both forms gets span extents it never chose. **Pick the extent deliberately and
say so in a comment**, because the next reader cannot tell an intentional
long-lived span from a misplaced `using var`.

TB Consumer's choices:

| Signal | Attributed to | Why |
|---|---|---|
| Per-tile published event (4017) | `embedder.publish_batch` | The batch span did that work |
| Sub-stage counts and results | That sub-stage's span | Same |
| End-of-message summary log (4015) | `embedder.publish_batch` | Keeps the batch outcome legible on the span operators actually open |
| Whole-message workload counters and `findair.end_to_end.duration` | `embedder.publish_batch` | Emitted alongside the summary |

Note the trade-off in the last two rows. By extent alone those signals describe
the whole message — 4015 even reports the mapped-coordinate count produced by
the sibling `tb_consumer.projection` span — so the strictly correct home is a
message-level span. TB Consumer keeps them on the batch span anyway, because
operational legibility won: the batch span is the one an operator opens when
asking "did this batch go out?", and a summary that is not on it is a summary
nobody finds.

The handler makes that explicit by creating the batch span in
`HandleValidatedMessageAsync` and passing it into `PublishToEmbedderAsync`,
rather than letting the publish method own it. The cost is that the span's
lifetime and its use sit in different methods; the comment at the creation site
explains why.

If a service prefers strict extent, the tidy shape is a handler-owned
`{stage}.message` span to carry whole-message signals. No service has one
today — the only span covering exactly one message is the shared client's
`rabbitmq handler`, which is named for the transport. That remains an open
question rather than an oversight.

## Canonical handler shape

```csharp
public async Task<RabbitMqMessageProcessingResult> HandleAsync(
    RabbitMqMessageEnvelope message,
    CancellationToken cancellationToken = default)
{
    using var stage = PipelineStageScope.Begin(PipelineStage.TbConsumer, message.Body.LongLength);

    try
    {
        if (!TryDeserialize(message, out var input, out var deserializationError))
        {
            stage.Rejected(TelemetryErrorCategory.Serialization);
            _logger.DeserializationRejected(deserializationError);
            return RabbitMqMessageProcessingResult.Failure(deserializationError);
        }

        // Built from unvalidated input, so every field is normalised defensively.
        var context = CreateTelemetryContext(input);
        using var pipelineScope = _logger.BeginTelemetryScope(context);
        Activity.Current.AddPipelineContext(context);

        var validationFailure = Validate(input);
        if (validationFailure is not null)
        {
            stage.Rejected(TelemetryErrorCategory.Validation);
            _logger.MessageRejected(/* structured failure detail */);
            return RabbitMqMessageProcessingResult.Failure(validationFailure.Message);
        }

        return await HandleValidatedMessageAsync(input, message, stage, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        stage.Cancelled();
        throw;
    }
    catch
    {
        stage.Faulted();
        throw;
    }
}
```

A sub-stage inside the handler:

```csharp
using var span = PipelineSpanScope.StartStage(PipelineStage.TbConsumer, "projection", context);
span.SetTag(TelemetryAttributeNames.TileCount, input.Tiles.Count);

try
{
    var mapped = await _projectionMapper.ProcessBatchAsync(...);
    span.SetTag("findair.coordinate.count", mapped.Count);
    return new ProjectionResult(mapped, null);
}
catch (OperationCanceledException ex)
{
    stage.Cancelled();
    span.Cancelled(ex);
    throw;
}
catch (Exception ex)
{
    stage.Retryable(TelemetryErrorCategory.Dependency);
    span.Failed(TelemetryErrorCategory.Dependency, ex, recordException: false);
    // RabbitMQ's handler boundary owns the exception-bearing error log.
    _logger.ProjectionScheduledForRetry(input.Tiles.Count);
    throw;
}
```

Note what is inside the `try` and what is not. Span creation and its opening
tags cannot fail and sit outside it; only work that can genuinely throw is
guarded, so the catch blocks describe real failure modes.

## Two log contexts, not one

Handlers build `TelemetryLogContext` twice, and this is deliberate:

- **Pre-validation**, from unvalidated input: every field normalised
  (`NullIfWhiteSpace`, and enum values dropped unless `Enum.IsDefined`). This
  one feeds the handler-wide log scope and `Activity.Current`, both of which
  must tolerate a malformed message.
- **Post-validation**, from raw values: validation has already guaranteed the
  fields are present and bounded. This one feeds spans and per-item log scopes.

Do not merge them. The normalised context exists so a malformed message cannot
put unbounded values into logs; the raw context exists so validated code is not
re-checking what validation already proved.

## Migrating a service

1. Replace the `started` / `outcome` / `error` locals and any handler state
   object with `PipelineStageScope.Begin(...)` and classification calls.
2. Delete the `finally` block that records messages, fan-out, and duration.
   `Dispose` owns it.
3. Delete the service's private `StartStageActivity` helper and switch to
   `PipelineSpanScope.StartStage` / `StartProducer`. Span names are derived
   from the `PipelineStage` enum, so the emitted names do not change.
4. Build one `TelemetryLogContext` per phase and pass it to spans instead of
   spelling out the pipeline fields at each call site.
5. Build one `WorkloadDimensions` per message; add the `WorkloadTelemetry`
   overloads the service needs.
6. Check span extents against the table above — this is where the pre-existing
   `using var` behaviour is most likely to be quietly wrong.
7. Verify with the recipe below before changing anything else.

## Verifying a migration

Existing unit tests pin span names and a few tags, but they do not pin metric
tag sets or which span a log is attributed to. A refactor can pass the whole
suite and still move telemetry. Use a throwaway snapshot harness:

1. Add a temporary test that drives the handler through every outcome —
   success, each rejection, each retryable failure, and cancellation at each
   stage.
2. Attach an `ActivityListener` (recording span name, kind, status, parent,
   sorted tags, events), a `MeterListener` (instrument name, sorted tags, and
   value — normalising duration values, which are not reproducible), and a
   logger that records event id, message, sorted scope attributes, and
   `Activity.Current?.OperationName`.
3. Start a parent activity around the call so `Activity.Current` is non-null,
   as it is at runtime.
4. Write the dump to a file, run it before and after the change, and diff.
5. Delete the harness. It is a checking tool, not a test.

Run the harness **filtered to itself**. Its listeners are process-wide, so
running it alongside the rest of the suite captures telemetry from concurrent
tests and produces a meaningless diff.

Capturing `Activity.Current` at each log site is not optional — it is the only
part of the dump that catches a changed span extent.

## Status

| Service | State |
|---|---|
| TB Consumer | Migrated; reference implementation |
| TB Publisher | Not migrated; still has the hand-rolled form |
| Gateway | Not migrated; still has the hand-rolled form |

The TB Consumer migration changed no emitted telemetry. Every span, metric, and
log — including which span each log is attributed to — was verified identical
before and after using the recipe above.
