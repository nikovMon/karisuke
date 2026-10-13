namespace ImagingPipeline.PipelineContracts;

/// <summary>Preserves the existing contract ID and settings behavior for catalogs used by other hosts.</summary>
public sealed class AlgoPipelineContract : DeepWisdomPipelineContract
{
    public override string ContractId => "algo";
    protected override bool UsesLegacyExtraData => true;
}
