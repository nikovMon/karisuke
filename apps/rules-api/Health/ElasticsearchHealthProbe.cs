using ImagingPipeline.Observability;
using ImagingPipeline.Rules.Api;
using Nest;

namespace ImagingPipeline.Rules.Api.Health;

public sealed class ElasticsearchHealthProbe : IElasticsearchHealthProbe
{
    private readonly IElasticClient _client;
    private readonly ILogger<ElasticsearchHealthProbe> _logger;
    private int _isUnhealthy;

    public ElasticsearchHealthProbe(
        IElasticClient client,
        ILogger<ElasticsearchHealthProbe> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = TelemetryTiming.StartTimestamp();
        var outcome = TelemetryOutcome.Failure;
        var error = TelemetryErrorCategory.Dependency;

        try
        {
            var response = await _client.PingAsync(descriptor => descriptor, cancellationToken);
            if (response.IsValid)
            {
                outcome = TelemetryOutcome.Success;
                error = TelemetryErrorCategory.None;
                Interlocked.Exchange(ref _isUnhealthy, 0);
                return true;
            }

            if (Interlocked.Exchange(ref _isUnhealthy, 1) == 0)
            {
                _logger.ElasticsearchHealthInvalidResponse(
                    response.ApiCall?.HttpStatusCode,
                    response.OriginalException?.GetType().Name);
            }

            return false;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            outcome = TelemetryOutcome.Cancelled;
            error = TelemetryErrorCategory.Cancelled;
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            error = exception is OperationCanceledException or TimeoutException
                ? TelemetryErrorCategory.Timeout
                : TelemetryErrorCategory.Dependency;
            if (Interlocked.Exchange(ref _isUnhealthy, 1) == 0)
            {
                _logger.ElasticsearchHealthProbeFailed(exception);
            }

            return false;
        }
        finally
        {
            RulesTelemetry.RecordOperation(
                RulesOperation.Health,
                TelemetryTiming.ElapsedSeconds(startedAt),
                outcome,
                error: error);
        }
    }
}
