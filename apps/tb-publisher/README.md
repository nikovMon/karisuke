# TB Publisher

Consumes FindAir task messages, validates source data, maps the ROI through
Projection Mapper, and publishes one Tile Builder request per tiling configuration.
Configure the `RabbitMq`, `ProjectionMapper`, and `Observability` sections in
`appsettings.json` or their environment-variable equivalents.

Transient Projection Mapper failures use the existing three 10-second retries.
Invalid source data is classified as `InvalidData`; invalid mapped geometry or
malformed dependency responses are `DependencyResponse`. Authentication and
configuration failures are `Configuration`; unclassified failures are `Unknown`.
Only typed transient dependency failures qualify for automatic delayed recovery.
Full-task retries can repeat outputs already published before a failure.

The checked-in configuration enables `RabbitMq:EnrichedDeadLetterEnabled` and
sets `RabbitMq:FailureServiceName` to `tb-publisher`. Retained failures carry safe
classification metadata and preserve the original input body. Classification
does not add Projection Mapper probes or change concurrency or retry delays.

Run `npx nx build tb-publisher` and `npx nx test tb-publisher-tests` from the
repository root. Broker integration tests require the explicit test broker.
