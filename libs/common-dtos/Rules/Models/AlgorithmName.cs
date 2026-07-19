namespace ImagingPipeline.Common.Dtos.Rules.Models;

/// <summary>
/// Known algorithm identifiers used across the imaging pipeline.
/// </summary>
public enum AlgorithmName
{
    Unknown = 0,
    FindAir,
    FindShip,
    FindVehicle,
    FindBuilding,
    FindChange,
    RPN
}
