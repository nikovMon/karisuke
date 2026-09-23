using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace ImagingPipeline.PipelineContracts;

public interface IPipelineContractRegistry
{
    IPipelineContract GetRequired(string contractId);

    bool TryGet(string contractId, [NotNullWhen(true)] out IPipelineContract? contract);
}

public sealed class PipelineContractRegistry : IPipelineContractRegistry
{
    private readonly FrozenDictionary<string, IPipelineContract> _contracts;

    public PipelineContractRegistry(IEnumerable<IPipelineContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        var registered = new Dictionary<string, IPipelineContract>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            if (contract is null || string.IsNullOrWhiteSpace(contract.ContractId))
            {
                throw new ArgumentException("Every pipeline contract must have a nonempty ID.", nameof(contracts));
            }

            if (!registered.TryAdd(contract.ContractId, contract))
            {
                throw new ArgumentException(
                    $"Pipeline contract '{contract.ContractId}' is registered more than once.",
                    nameof(contracts));
            }
        }

        _contracts = registered.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IPipelineContract GetRequired(string contractId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        return TryGet(contractId, out var contract)
            ? contract
            : throw new KeyNotFoundException($"Pipeline contract '{contractId}' is not registered.");
    }

    public bool TryGet(string contractId, [NotNullWhen(true)] out IPipelineContract? contract)
    {
        contract = null;
        return !string.IsNullOrWhiteSpace(contractId) && _contracts.TryGetValue(contractId, out contract);
    }
}
