# Rule engine

Matches incoming image updates against rules. Shared by the unified gateway and, later, the Rules API.

- `Input/` parses and validates an incoming update (`GatewayInputMessageParser`). An invalid message throws `InvalidInputMessageException` with a stable error code; it should be dead-lettered.
- `Loading/` reads every active rule from one Elasticsearch index (`ElasticsearchRuleRepository`). The index is a parameter, so one repository serves every pipeline. Unreadable documents are reported in the result; a failed read throws `RuleLoadException`, which is retryable.
- `Rules/` prepares one pipeline's loaded rules for matching (`RuleSnapshotBuilder`) and matches an image against them (`RuleMatcher`). Each rule is checked against the document rules and its run parameters against the pipeline's contract, the same checks the Rules API runs on write. Invalid rules are rejected one by one with a reason. `RuleSnapshot.AllRulesRejected` flags a load that returned rules but none usable, so callers can keep their previous snapshot instead of routing nothing.

Rules are v2 documents (`PipelineRuleDocument` in `libs/common-dtos`): `match` holds the conditions and each `runParams` entry is one run of the pipeline, in its contract's shape. All present conditions must hold: photo age, sensor (registration quality and grid type), resolution, then geometry. An absent condition means no constraint. A rule with no conditions is rejected unless it sets `matchAll: true`, and empty collections such as `sensors: []` are rejected rather than read as "any". The intersection of the image and the rule's location becomes the downstream ROI; a rule without a location covers the whole image.

v1 rules (`RuleDto`) are not read here. The old gateway keeps serving them until the migration converts them to v2.

The matcher does not log. It returns the matched rules and, for every other rule, the first condition it failed. Callers log that with `LogRulesEvaluated` inside a scope carrying the image and pipeline IDs, so searching an image ID shows why it did or did not reach each pipeline. Log events use IDs 7001-7999 with static messages; details are structured fields.

This code started as a copy of `apps/gateway`'s rule handling. The old gateway keeps its own copy until the unified gateway replaces it.
