using System.Text.Json;

namespace ImagingPipeline.PipelineContracts.Tests;

public sealed class PipelineContractRegistryTests
{
    [Fact]
    public void RegistryReturnsRegisteredInstancesByExactId()
    {
        var contract = new FindAirPipelineContract();
        var registry = new PipelineContractRegistry([contract]);

        Assert.Same(contract, registry.GetRequired("FindAir"));
        Assert.True(registry.TryGet("FindAir", out var found));
        Assert.Same(contract, found);
        Assert.False(registry.TryGet("FINDAIR", out var wrongCase));
        Assert.Null(wrongCase);
    }

    [Fact]
    public void RegistryRejectsDuplicateContractIds()
    {
        Assert.Throws<ArgumentException>(() =>
            new PipelineContractRegistry([new FindAirPipelineContract(), new FindAirPipelineContract()]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void RegistryRejectsMissingContractIdentity(string? contractId)
    {
        Assert.Throws<ArgumentException>(() => new PipelineContractRegistry([new StubContract(contractId!)]));
    }

    [Fact]
    public void RegistryRejectsNullEntries()
    {
        Assert.Throws<ArgumentException>(() => new PipelineContractRegistry([null!]));
    }

    [Fact]
    public void UnknownContractCannotBeResolvedOrSilentlyReplacedWithFindAir()
    {
        var registry = new PipelineContractRegistry([new FindAirPipelineContract()]);

        Assert.False(registry.TryGet("DeepWisdom", out var contract));
        Assert.Null(contract);
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("DeepWisdom"));
    }

    [Fact]
    public void RegistrySupportsEmptyRegistrationAndSnapshotsItsInput()
    {
        var empty = new PipelineContractRegistry([]);
        Assert.False(empty.TryGet("FindAir", out _));

        var contracts = new List<IPipelineContract> { new FindAirPipelineContract() };
        var registry = new PipelineContractRegistry(contracts);
        contracts.Clear();

        Assert.IsType<FindAirPipelineContract>(registry.GetRequired("FindAir"));
    }

    private sealed class StubContract(string contractId) : IPipelineContract
    {
        public string ContractId { get; } = contractId;
        public IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams) => [];
        public PipelinePayload BuildPayload(PipelineDispatchContext context, JsonElement runParams, JsonElement extraData = default) =>
            throw new NotSupportedException();
    }
}
