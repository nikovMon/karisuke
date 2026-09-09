namespace ImagingPipeline.Observability;

/// <summary>
/// The business dimensions every workload metric is sliced by. Handlers build this once per
/// message instead of passing the same five values positionally to each recording call.
/// </summary>
public readonly record struct WorkloadDimensions(
    string RuleId,
    string TenantId,
    string? AreaName,
    string SensorName,
    string AlgorithmNames);
