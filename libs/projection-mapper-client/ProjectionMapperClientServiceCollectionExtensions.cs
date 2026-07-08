using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.ProjectionMapperClient;

public static class ProjectionMapperClientServiceCollectionExtensions
{
    public static IServiceCollection AddProjectionMapperClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ProjectionMapperOptions>()
            .Bind(configuration.GetSection(ProjectionMapperOptions.SectionName))
            .Validate(options => options.IsValid(out _), "ProjectionMapper configuration is invalid.")
            .ValidateOnStart();

        services.AddHttpClient<IProjectionMapperClient, ProjectionMapperClient>(ConfigureHttpClient);

        return services;
    }

    private static void ConfigureHttpClient(IServiceProvider services, HttpClient client)
    {
        var options = services.GetRequiredService<IOptions<ProjectionMapperOptions>>().Value;
        client.BaseAddress = new Uri(options.Host);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        client.DefaultRequestHeaders.Add("X-Sending-System", options.SendingSystem);
    }
}
