using Microsoft.Extensions.DependencyInjection;

namespace ImagingPipeline.PipelineContracts;

public static class PipelineContractServiceCollectionExtensions
{
    /// <summary>
    /// Registers every pipeline contract and the registry that looks them up by ID. Hosts that load
    /// the catalog share this list, so a contract added for one host is known to all of them.
    /// </summary>
    public static IServiceCollection AddPipelineContracts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPipelineContract, AsdPipelineContract>();
        services.AddSingleton<IPipelineContract, AlgoPipelineContract>();
        services.AddSingleton<IPipelineContractRegistry, PipelineContractRegistry>();
        return services;
    }
}
