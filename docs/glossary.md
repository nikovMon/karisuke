# Glossary

Terms used by the unified gateway, the Rules API and the rule engine.

| Term | Meaning |
|---|---|
| Pipeline | A downstream consumer of images, configured in the pipeline catalog by ID, with its own rules index, contract and transport. |
| Contract | Code that owns a pipeline's data shapes: validates its run parameters, rule location and the image fields it needs, and builds its payload. |
| Rule | A v2 `PipelineRuleDocument` in a pipeline's index: `match` decides which images qualify, `runParams` what each run sends. |
| Match condition | One field of `match` (`sensors`, `sensorTypes`, `resolution`, `locationWkt`, `maxPhotoAgeDays`, `modeSAR`). All present conditions must hold; an absent one means no constraint; an empty list is rejected. |
| `matchAll` | Explicit flag for a rule with no conditions, so matching every image is always deliberate. |
| Run params | One entry of `runParams`: the parameters of one run of the pipeline, in the shape its contract defines. Each entry becomes one dispatch. |
| Dispatch unit | One message to one pipeline for one image, rule and run-params entry. Identified by a hash of the four, which deduplicates identical runs. |
| Miss reason | Why a rule did not match an image: the first failed condition (photo age, sensor, resolution, `modeSAR`, geometry), logged in event 7002. |
| `modeSAR` | The SAR acquisition mode of an image: an optional top-level string in the source message, and a match condition listing allowed modes ([ADR 0001](adr/0001-modesar-match-condition.md)). |
