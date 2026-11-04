# TB Consumer

Consumes Tile Builder output batches, maps four corners per tile through
Projection Mapper, and publishes one Embedder input per tile. Configure the
`RabbitMq`, `ProjectionMapper`, and `Observability` sections in `appsettings.json`
or their environment-variable equivalents.

Transient Projection Mapper failures use the existing three 10-second retries.
Invalid input JSON or contract data is `InvalidData`. Malformed dependency
responses, incorrect result counts, and invalid coordinate shapes are
`DependencyResponse`, retained for review rather than treated as invalid source
data. Authentication/configuration failures are `Configuration`; other
unclassified failures are `Unknown`. Typed transient dependency failures qualify
for automatic delayed recovery. Caller cancellation is not a dependency failure.

The checked-in configuration enables `RabbitMq:EnrichedDeadLetterEnabled` and
sets `RabbitMq:FailureServiceName` to `tb-consumer`. Original failure payloads and
safe classification metadata are retained. No health probes or processing-rate
changes are introduced. A partial publication failure can repeat previously
published tiles when the batch is replayed; downstream tile deduplication remains
necessary where duplicates matter.

Run `npx nx build tb-consumer` and `npx nx test tb-consumer-tests` from the
repository root.
