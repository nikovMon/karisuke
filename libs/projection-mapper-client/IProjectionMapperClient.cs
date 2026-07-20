namespace ImagingPipeline.ProjectionMapperClient;

public interface IProjectionMapperClient
{
    Task<IReadOnlyList<IReadOnlyList<double>>> MapAsync(
        string overlayId,
        ProjectionMapperRequestDto request,
        CancellationToken cancellationToken = default);
}
