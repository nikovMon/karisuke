using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Responses;

/// <summary>
/// What a rule author needs to know about a configured pipeline. Transport settings are left out
/// on purpose: queue names, endpoints and credentials mean nothing to a rule author.
/// </summary>
public sealed record PipelineResponse(
    [property: JsonPropertyName("pipelineId")] string PipelineId,
    [property: JsonPropertyName("contractId")] string ContractId,
    [property: JsonPropertyName("enabled")] bool Enabled);
