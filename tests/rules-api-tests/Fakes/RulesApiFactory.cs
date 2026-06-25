using ImagingPipeline.Rules.Api.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

internal sealed class RulesApiFactory : WebApplicationFactory<Program>
{
    private readonly IRuleRepository _repository;

    public RulesApiFactory(IRuleRepository repository)
    {
        _repository = repository;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRuleRepository>();
            services.AddSingleton<IRuleRepository>(_repository);
        });
    }
}
