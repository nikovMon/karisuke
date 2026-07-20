# ImagingPipeline common DTOs

`ImagingPipeline.Common.Dtos` contains DTO contracts shared by multiple apps.
Keep app-local DTOs in the owning app until another app needs the same contract.

## Rules contracts

Rules contracts are grouped by responsibility:

- `Rules/Models` contains the rule configuration document and nested models.
- `Rules/Requests` contains activity, sensor, and partial-update request contracts.
- `Rules/Responses` contains single-rule and bulk-operation result contracts.

## Gateway contracts

- `Gateway/Messages/GatewayInputMessageSchema.cs` contains the shared input
  field paths read by the gateway.
- `Gateway/Messages/GatewayOutputPayload.cs` contains the output payload
  published by the gateway for downstream services.

## Messaging contracts

`Messaging/GatewayOutputMessageDto` describes the message tb-publisher consumes from gateway's output queue. It has a `TaskId` field for a stable, transport-independent identifier; gateway does not populate it yet, so it is not enforced as required until gateway adopts it. tb-publisher forwards `TaskId` unchanged into every `TbPublisherOutputMessageDto` it derives from the message. `Messaging/TilingConfigValidator` is the shared tile-size/overlap invariant it enforces on `TilingConfigs`.

`Messaging/TbPublisherOutputMessageDto` (with its nested `FrameMetadataDto`, `ModelMetadataDto`, `MissionMetadataDto`, and `OverlayDto`) describes the message tb-publisher publishes for tb-consumer to consume.
