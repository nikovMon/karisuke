# ADR 0001: `modeSAR` match condition

Status: Accepted (2026-10-20)

## Context

Some pipelines should only receive images taken in specific SAR modes. Source messages carry the mode as an optional top-level string, `modeSAR`. Rules had no way to filter on it.

`match` is shared by every pipeline, so a new condition is a rule-engine change, not a contract change. Contracts reject images a pipeline cannot work with (`ValidateContext`, which dead-letters the message); conditions decide quietly which images a rule selects. Filtering on the mode is selection, so it is a condition.

## Decision

- Rules get `match.modeSAR`: a list of allowed modes. An image matches when its `modeSAR` equals one of them.
- The condition sits at the top level of `match` and applies whatever the sensor. It is not per sensor.
- Omitting `match.modeSAR` means any mode, including images without one. An empty list is rejected, as for every other condition.
- Values are free strings compared exactly and case-sensitively, like `gridTypes`. Empty or whitespace values and duplicates in a rule are rejected. There is no enum, so new modes need no code change.
- An image without a usable `modeSAR` (absent, `null`, not a string, or blank) does not match a rule that lists modes. It still matches rules without the condition, and the message is not rejected.
- A miss is reported with its own reason, logged in 7002 as `unified_gateway.rules.missed.mode_sar.ids` / `.count`.
- The rule field keeps the source spelling, `modeSAR`, everywhere it appears: stored rules, the Rules API and audit `changedFields`.
- The mode is used for matching only. It is not forwarded to pipelines; a contract that needs it adds it to `PipelineDispatchContext` and its payload separately.

## Consequences

- Existing rules and pipelines are unaffected, because the condition is absent from them.
- A typo in a rule's modes is not caught on write; the rule simply never matches. The 7002 miss fields show it.
- The old gateway ignores the field. A v2 rule that uses it routes differently from any v1 rule, so it cannot be compared with the old gateway.
