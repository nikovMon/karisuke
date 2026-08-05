namespace ImagingPipeline.ProjectionMapperClient;

public interface IProjectionMapperClient
{
    Task<IReadOnlyList<IReadOnlyList<double>>> MapAsync(
        string overlayId,
        ProjectionMapperRequestDto request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IReadOnlyList<double>>> ProcessBatchAsync(
        string overlayId,
        IReadOnlyList<IReadOnlyList<double>> coordinates,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IReadOnlyList<double>>> ProcessBatchByRegistrationAsync(
        string overlayId,
        IReadOnlyList<IReadOnlyList<double>> coordinates,
        string gridType,
        string? gridUri,
        CancellationToken cancellationToken = default);
}
