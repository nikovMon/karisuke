# Rule engine

Matches incoming image updates against rules. Shared by the unified gateway and, later, the Rules API.

- `Input/` parses and validates an incoming update (`GatewayInputMessageParser`). An invalid message throws `InvalidInputMessageException` with a stable error code; it should be dead-lettered.
- `Loading/` reads every active rule from one Elasticsearch index (`ElasticsearchRuleRepository`). The index is a parameter, so one repository serves every pipeline. Unreadable documents are reported in the result; a failed read throws `RuleLoadException`, which is retryable.
- `Rules/` prepares loaded rules for matching (`RuleSnapshotBuilder`) and matches an image against them (`RuleMatcher`). Invalid rules are rejected one by one with a reason. `RuleSnapshot.AllRulesRejected` flags a load that returned rules but none usable, so callers can keep their previous snapshot instead of routing nothing.

All conditions of a rule must hold: photo age, sensor (registration quality and grid type), resolution, then geometry. The intersection of the image and the rule becomes the downstream ROI. A rule with no sensors matches any sensor.

The matcher does not log. It returns the matched rules and, for every other rule, the first condition it failed. Callers log that with `LogRulesEvaluated` inside a scope carrying the image and pipeline IDs, so searching an image ID shows why it did or did not reach each pipeline. Log events use IDs 7001-7999 with static messages; details are structured fields.

This code started as a copy of `apps/gateway`'s rule handling. The old gateway keeps its own copy until the unified gateway replaces it.
