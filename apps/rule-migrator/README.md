# Rule Migrator

One-off tool that copies the v1 rules (`RuleDto`, index `rules`) into a pipeline's v2 rules index as `PipelineRuleDocument`s (SPEC-001 §8). Remove it together with `RuleDto` once the old gateway is retired.

## Run

```bash
dotnet run --project apps/rule-migrator -- --pipeline FindAir
dotnet run --project apps/rule-migrator -- --pipeline FindAir --apply
```

| Option | Default | |
|---|---|---|
| `--pipeline` | required | Catalog pipeline ID. Its `RulesIndex` is the target and its contract validates the rules. |
| `--source-index` | `rules` | The v1 index. |
| `--max-photo-age-days` | `7` | `match.maxPhotoAgeDays` for v1 rules with `isPhotoOld: true`. |
| `--apply` | off | Writes the rules. Without it the run is a dry run that only prints the report. |

Elasticsearch connection settings come from `appsettings.json` or environment variables (`Elasticsearch__Uri`, `Elasticsearch__Username`, `Elasticsearch__Password`); the catalog from `pipelinecatalog.json` or `PipelineCatalogFile`, as in the other apps. The target index must already exist with its mapping (see `deploy/unified-gateway/example/rules-index-mapping.json`).

## Behavior

- Each rule keeps its `_id`, so rerunning overwrites the earlier copies instead of duplicating them. The source index is never changed.
- Each converted rule is checked with `PipelineRuleValidator`, the same check the Rules API and the gateway use, and `match.location` is derived from the WKT as the Rules API does.
- A rule is skipped and listed in the report when it is not a readable v1 rule, fails validation (for example no tenants, or a tenant without tiling configs), has a name a different rule in the target already uses, or fails to save. The other rules are still migrated.
- `createdBy` and `updatedBy` are `migration`; the v1 timestamps are kept. No audit records are written.

## Mapping

| v1 | v2 |
|---|---|
| `ruleName`, `description`, `isActive`, `creationTime`, `updateTime` | Same field |
| `sensors` | `match.sensors`; an empty list is omitted (any sensor) |
| `minimumResolution` / `maximumResolution` | `match.resolution.min` / `.max` |
| `locationWkt` | `match.locationWkt`; `match.location` is derived from it |
| `locationGeoJson` | Not copied |
| `isPhotoOld: true` | `match.maxPhotoAgeDays: --max-photo-age-days` |
| `isPhotoOld: false` / absent | Omitted |
| `tenantsInfo[i]` | `runParams[i]`: `tenantId`, `tilingConfigs` and `algorithmNames` (from `algorithmName`) |
| `area` | Dropped; non-empty values are listed in the report |
