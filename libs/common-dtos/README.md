# ImagingPipeline common DTOs

`ImagingPipeline.Common.Dtos` contains DTO contracts shared by multiple apps.
Keep app-local DTOs in the owning app until another app needs the same contract.

## Rules contracts

Rules contracts are grouped by responsibility:

- `Rules/Models` contains the rule configuration document and nested models.
- `Rules/Requests` contains activity, sensor, and partial-update request contracts.
- `Rules/Responses` contains single-rule and bulk-operation result contracts.
