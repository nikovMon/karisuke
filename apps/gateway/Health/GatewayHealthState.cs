namespace ImagingPipeline.Gateway.Health;

public sealed class GatewayHealthState
{
    private readonly object _rulesRefreshLock = new();
    private volatile bool _rulesLoaded;
    private volatile bool _consumerStarted;
    private DateTimeOffset? _lastSuccessfulRulesRefreshAt;
    private DateTimeOffset? _lastFailedRulesRefreshAt;
    private int _consecutiveRulesRefreshFailures;

    public bool RulesLoaded => _rulesLoaded;
    public bool ConsumerStarted => _consumerStarted;

    public DateTimeOffset? LastSuccessfulRulesRefreshAt
    {
        get
        {
            lock (_rulesRefreshLock)
            {
                return _lastSuccessfulRulesRefreshAt;
            }
        }
    }

    public DateTimeOffset? LastFailedRulesRefreshAt
    {
        get
        {
            lock (_rulesRefreshLock)
            {
                return _lastFailedRulesRefreshAt;
            }
        }
    }

    public int ConsecutiveRulesRefreshFailures
    {
        get
        {
            lock (_rulesRefreshLock)
            {
                return _consecutiveRulesRefreshFailures;
            }
        }
    }

    public void MarkRulesRefreshSucceeded()
    {
        lock (_rulesRefreshLock)
        {
            _rulesLoaded = true;
            _lastSuccessfulRulesRefreshAt = DateTimeOffset.UtcNow;
            _consecutiveRulesRefreshFailures = 0;
        }
    }

    public void MarkRulesRefreshFailed()
    {
        lock (_rulesRefreshLock)
        {
            _lastFailedRulesRefreshAt = DateTimeOffset.UtcNow;
            _consecutiveRulesRefreshFailures++;
        }
    }

    public void MarkConsumerStarted() => _consumerStarted = true;

    public void MarkConsumerStopped() => _consumerStarted = false;
}
