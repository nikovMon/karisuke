namespace ImagingPipeline.PipelineContracts;

/// <summary>Preserves the existing contract ID for catalogs used by other hosts.</summary>
public sealed class AsdPipelineContract : FindAirPipelineContract
{
    public override string ContractId => "asd";
}
