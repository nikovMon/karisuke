namespace ImagingPipeline.Rules.Api.Health;

public interface IElasticsearchHealthProbe
{
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}
