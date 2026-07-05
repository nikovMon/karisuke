namespace ImagingPipeline.Gateway.Contracts.Messages;

public sealed record GatewayOutputMessage(byte[] Body, string RuleId, string TenantId);
