# RabbitMQ argument configuration

Dependency-free argument conversion shared by the pipeline catalog and RabbitMQ client. It does not connect to a broker, validate topology, or mutate input dictionaries.

`RabbitMqArgumentNormalizer.Normalize(arguments, policy)` returns a new dictionary. Known string arguments, including dead-letter exchange/routing keys, remain strings even when their contents resemble numbers or booleans. Already typed values pass through unchanged; nested tables and byte arrays are not cloned or converted.

| Caller / policy | String conversion for other argument keys |
|---|---|
| RabbitMQ client / `Legacy` | Invariant Int32, then Boolean; preserve other strings. |
| Pipeline catalog / `ExtendedNumeric` | Invariant Int32, Int64, Boolean, then finite Double; preserve other strings. |

The separate policies preserve existing arbitrary header-binding wire types while sharing the conversion implementation. The runtime correction is that known string arguments are no longer coerced into integers or booleans. Callers remain responsible for choosing valid argument values.

The catalog retains its scalar-only validation. The RabbitMQ client retains support for already typed nulls, byte arrays and nested tables, and converts an empty argument dictionary to `null` at the transport boundary.

Tests live in `tests/rabbitmq-client-tests`, including captured queue/exchange/binding calls, culture-independent conversion, repeated normalization and input dictionary isolation. Catalog tests cover its additional validation and conversion policy.
