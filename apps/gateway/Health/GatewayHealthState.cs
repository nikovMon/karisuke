namespace ImagingPipeline.Gateway.Health;

public sealed class GatewayHealthState
{
    private volatile bool _rulesLoaded;
    private volatile bool _consumerStarted;

    public bool RulesLoaded => _rulesLoaded;
    public bool ConsumerStarted => _consumerStarted;

    public void MarkRulesLoaded() => _rulesLoaded = true;

    public void MarkConsumerStarted() => _consumerStarted = true;

    public void MarkConsumerStopped() => _consumerStarted = false;
}
