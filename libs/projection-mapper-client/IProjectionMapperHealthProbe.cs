namespace ImagingPipeline.ProjectionMapperClient;

public interface IProjectionMapperHealthProbe
{
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}
