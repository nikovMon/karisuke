using ImagingPipeline.ProjectionMapperClient;

namespace ImagingPipeline.TbPublisher.Tests.Fakes;

public sealed class FakeProjectionMapperClient : IProjectionMapperClient
{
    private readonly IReadOnlyList<IReadOnlyList<double>>? _result;
    private readonly Exception? _exception;

    private FakeProjectionMapperClient(IReadOnlyList<IReadOnlyList<double>>? result, Exception? exception)
    {
        _result = result;
        _exception = exception;
    }

    public string? LastOverlayId { get; private set; }

    public ProjectionMapperRequestDto? LastRequest { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public static FakeProjectionMapperClient ReturningSuccess(IReadOnlyList<IReadOnlyList<double>> result) => new(result, null);

    public static FakeProjectionMapperClient ThrowingFailure(Exception exception) => new(null, exception);

    public Task<IReadOnlyList<IReadOnlyList<double>>> MapAsync(
        string overlayId,
        ProjectionMapperRequestDto request,
        CancellationToken cancellationToken = default)
    {
        LastOverlayId = overlayId;
        LastRequest = request;
        LastCancellationToken = cancellationToken;

        if (_exception is not null)
        {
            throw _exception;
        }

        return Task.FromResult(_result!);
    }
}
