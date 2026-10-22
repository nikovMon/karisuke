# Rule Migrator

One-off tool that copies existing rules into a pipeline's v2 rules index as `PipelineRuleDocument`s. It reads two source schemas: the old gateway's v1 rules (`RuleDto`, index `rules`, SPEC-001 §8) and the old Algo system's rules (index `algo-rules`, into DeepWisdom). Remove it once the old gateway and Algo are retired.

## Run

```bash
dotnet run --project apps/rule-migrator -- --pipeline FindAir
dotnet run --project apps/rule-migrator -- --pipeline FindAir --apply
dotnet run --project apps/rule-migrator -- --pipeline DeepWisdom --source-schema algo
```

| Option | Default | |
|---|---|---|
| `--pipeline` | required | Catalog pipeline ID. Its `RulesIndex` is the target and its contract validates the rules. |
| `--source-schema` | `v1` | `v1` or `algo`: the shape of the source rules. |
| `--source-index` | `rules` (v1), `algo-rules` (algo) | The index to read. |
| `--max-photo-age-days` | `7` (v1), `2` (algo) | `match.maxPhotoAgeDays` for rules marked as old-photo sensitive (`isPhotoOld` / `is_photo_old`). The defaults are each old system's setting; check the live value before running. |
| `--apply` | off | Writes the rules. Without it the run is a dry run that only prints the report. |

Elasticsearch connection settings come from `appsettings.json` or environment variables (`Elasticsearch__Uri`, `Elasticsearch__Username`, `Elasticsearch__Password`); the catalog from `pipelinecatalog.json` or `PipelineCatalogFile`, as in the other apps. The target index must already exist with its mapping (see `deploy/unified-gateway/example/rules-index-mapping.json`).

## Behavior

- Each rule keeps its `_id`, so rerunning overwrites the earlier copies instead of duplicating them. The source index is never changed.
- Each converted rule is checked with `PipelineRuleValidator`, the same check the Rules API and the gateway use, and `match.location` is derived from the WKT as the Rules API does.
- A rule is skipped and listed in the report when it is not readable in the source schema, fails validation (for example no tenants, or a tenant without tiling configs), has a name a different rule in the target already uses, or fails to save. The other rules are still migrated.
- `createdBy` and `updatedBy` are `migration`; the source timestamps are kept. No audit records are written.

## Mapping (v1)

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

## Mapping (algo → DeepWisdom)

| `algo-rules` | v2 |
|---|---|
| `rule_name`, `is_active`, `creation_time`, `update_time` | `ruleName`, `isActive`, `creationTime`, `updateTime` |
| `sensor_names` (`{ name: [gridTypes] }`) | `match.sensors[{ name, gridTypes }]` with no registration qualities; an empty grid-type list accepts any grid type and quality for that sensor; an empty object is omitted (any sensor) |
| `sensor_types` | `match.sensorTypes`; an empty list is omitted |
| `minimum_resolution` / `maximum_resolution` | `match.resolution.min` / `.max`; a minimum of 0 or less is no lower bound |
| `location_wkt` | `match.locationWkt` (required by DeepWisdom); `match.location` is derived from it |
| `location_geojson` | Not copied |
| `is_photo_old: true` | `match.maxPhotoAgeDays: --max-photo-age-days` |
| `customer`, `profile_name`, `hebrew_rule_name`, `algorithm_name`, `priority`, `username` | `runParams[0]`: `customer`, `modelName`, `hebrewRuleName`, `algorithmName`, `priority`, `username` |
| — | `runParams[0].runEveryOtherImage` and `shouldCheckInVip` are `false`: Algo rules have no source for them. Review them per rule after migrating. |
