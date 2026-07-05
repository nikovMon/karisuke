using ImagingPipeline.TbPublisher.Dtos.Inbound;
using ImagingPipeline.TbPublisher.Dtos.Outbound;

namespace ImagingPipeline.TbPublisher.Application;

public interface ITilingConfigMapper
{
    TilingConfigMappingResult Map(TbMessageDto message, IReadOnlyList<IReadOnlyList<double>> coordinates);
}

public sealed record TilingConfigMappingResult(bool IsSuccess, IReadOnlyList<TilingConfigIngestMessageDto>? Messages, string? Error)
{
    public static TilingConfigMappingResult Success(IReadOnlyList<TilingConfigIngestMessageDto> messages) => new(true, messages, null);

    public static TilingConfigMappingResult Failure(string error) => new(false, null, error);
}
