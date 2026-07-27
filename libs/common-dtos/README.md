# ImagingPipeline common DTOs

`ImagingPipeline.Common.Dtos` contains DTO contracts shared by multiple apps.
Keep app-local DTOs in the owning app until another app needs the same contract.

## Rules contracts

Rules contracts are grouped by responsibility:

- `Rules/Models` contains the rule configuration document and nested models.
- `Rules/Requests` contains activity, sensor, and partial-update request contracts.
- `Rules/Responses` contains single-rule and bulk-operation result contracts.

## Gateway contracts

- `Gateway/Messages/GatewayInputMessageDto.cs` describes the typed input
  envelope and overlay fields consumed by the gateway.
- `Gateway/Messages/GatewayOutputMessageDto.cs` describes the output published
  by the gateway and consumed by tb-publisher. Its `algorithmName` JSON field is
  a non-empty list containing `FindAir`, `Rpn`, or both.

## Messaging contracts

`Messaging/TilingConfigValidator` contains the shared tile-size/overlap
invariant enforced on gateway output `TilingConfigs`.

`Messaging/TbPublisherOutputMessageDto` (with its nested `FrameMetadataDto`, `ModelMetadataDto`, `MissionMetadataDto`, and `OverlayDto`) describes the message tb-publisher publishes for tb-consumer to consume.
