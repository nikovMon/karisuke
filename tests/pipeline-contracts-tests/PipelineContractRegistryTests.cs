using System.Text.Json;

namespace ImagingPipeline.PipelineContracts.Tests;

public sealed class PipelineContractRegistryTests
{
    [Fact]
    public void RegistryReturnsRegisteredInstancesByExactId()
    {
        var contract = new AsdPipelineContract();
        var registry = new PipelineContractRegistry([contract]);

        Assert.Same(contract, registry.GetRequired("asd"));
        Assert.True(registry.TryGet("asd", out var found));
        Assert.Same(contract, found);
        Assert.False(registry.TryGet("ASD", out var wrongCase));
        Assert.Null(wrongCase);
    }

    [Fact]
    public void RegistryRejectsDuplicateContractIds()
    {
        Assert.Throws<ArgumentException>(() =>
            new PipelineContractRegistry([new AsdPipelineContract(), new AsdPipelineContract()]));
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
    public void UnknownContractCannotBeResolvedOrSilentlyReplacedWithAsd()
    {
        var registry = new PipelineContractRegistry([new AsdPipelineContract()]);

        Assert.False(registry.TryGet("algo", out var contract));
        Assert.Null(contract);
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("algo"));
    }

    [Fact]
    public void RegistrySupportsEmptyRegistrationAndSnapshotsItsInput()
    {
        var empty = new PipelineContractRegistry([]);
        Assert.False(empty.TryGet("asd", out _));

        var contracts = new List<IPipelineContract> { new AsdPipelineContract() };
        var registry = new PipelineContractRegistry(contracts);
        contracts.Clear();

        Assert.IsType<AsdPipelineContract>(registry.GetRequired("asd"));
    }

    private sealed class StubContract(string contractId) : IPipelineContract
    {
        public string ContractId { get; } = contractId;
        public IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams) => [];
        public PipelinePayload BuildPayload(PipelineDispatchContext context, JsonElement runParams, JsonElement extraData = default) =>
            throw new NotSupportedException();
    }
}
