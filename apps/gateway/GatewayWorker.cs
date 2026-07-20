namespace ImagingPipeline.Gateway;

using ImagingPipeline.Gateway.Errors;
using ImagingPipeline.Gateway.Contracts.Messages;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.Gateway.Processing.Rules;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging;

public sealed class GatewayWorker : BackgroundService, IRabbitMqMessageHandler
{
    private static readonly TimeSpan DefaultConsumerRestartDelay = TimeSpan.FromSeconds(5);

    private readonly IRabbitMqConsumer _consumer;
    private readonly ActiveRuleCache _ruleCache;
    private readonly GatewayInputMessageParser _inputParser;
    private readonly RuleMatcher _ruleMatcher;
    private readonly GatewayOutputMessageBuilder _outputBuilder;
    private readonly GatewayHealthState _healthState;
    private readonly ILogger<GatewayWorker> _logger;
    private readonly TimeSpan _consumerRestartDelay;

    public GatewayWorker(
        IRabbitMqConsumer consumer,
        ActiveRuleCache ruleCache,
        GatewayInputMessageParser inputParser,
        RuleMatcher ruleMatcher,
        GatewayOutputMessageBuilder outputBuilder,
        GatewayHealthState healthState,
        ILogger<GatewayWorker> logger)
        : this(
            consumer,
            ruleCache,
            inputParser,
            ruleMatcher,
            outputBuilder,
            healthState,
            logger,
            DefaultConsumerRestartDelay)
    {
    }

    public GatewayWorker(
        IRabbitMqConsumer consumer,
        ActiveRuleCache ruleCache,
        GatewayInputMessageParser inputParser,
        RuleMatcher ruleMatcher,
        GatewayOutputMessageBuilder outputBuilder,
        GatewayHealthState healthState,
        ILogger<GatewayWorker> logger,
        TimeSpan consumerRestartDelay)
    {
        if (consumerRestartDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(consumerRestartDelay));
        }

        _consumer = consumer;
        _ruleCache = ruleCache;
        _inputParser = inputParser;
        _ruleMatcher = ruleMatcher;
        _outputBuilder = outputBuilder;
        _healthState = healthState;
        _logger = logger;
        _consumerRestartDelay = consumerRestartDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var shouldRestart = false;
            _healthState.MarkConsumerStarted();

            try
            {
                await _consumer.ConsumeAsync(this, stoppingToken);
                shouldRestart = !stoppingToken.IsCancellationRequested;
                if (shouldRestart)
                {
                    _logger.LogWarning(
                        "RabbitMQ consumer exited unexpectedly; restarting in {RestartDelay}.",
                        _consumerRestartDelay);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                shouldRestart = !stoppingToken.IsCancellationRequested;
                if (shouldRestart)
                {
                    _logger.LogWarning(
                        ex,
                        "RabbitMQ consumer failed; restarting in {RestartDelay}.",
                        _consumerRestartDelay);
                }
            }
            finally
            {
                _healthState.MarkConsumerStopped();
            }

            if (shouldRestart)
            {
                await DelayBeforeRestartAsync(stoppingToken);
            }
        }
    }

    private async Task DelayBeforeRestartAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_consumerRestartDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public Task<RabbitMqMessageProcessingResult> HandleAsync(
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rules = _ruleCache.Current;
            var input = _inputParser.Parse(message.Body);
            var matches = _ruleMatcher.Match(input, rules);
            var outputs = _outputBuilder.BuildOutputs(input, matches);
            var correlationId = message.CorrelationId ?? message.MessageId;
            var outputMessages = new RabbitMqMessageEnvelope[outputs.Count];

            for (var outputIndex = 0; outputIndex < outputs.Count; outputIndex++)
            {
                outputMessages[outputIndex] =
                    message with
                    {
                        MessageId = CreateOutputMessageId(message.MessageId, outputs[outputIndex], outputIndex),
                        Body = outputs[outputIndex].Body,
                        ContentType = "application/json",
                        CorrelationId = correlationId
                    };
            }

            return Task.FromResult(RabbitMqMessageProcessingResult.Success(outputMessages));
        }
        catch (GatewayValidationException ex)
        {
            return Task.FromResult(RabbitMqMessageProcessingResult.Failure($"{ex.ErrorCode}: {ex.Message}"));
        }
        catch (GatewayProcessingException ex)
        {
            return Task.FromResult(RabbitMqMessageProcessingResult.Failure(ex.Message));
        }
    }

    private static string CreateOutputMessageId(
        string inputMessageId,
        GatewayOutputMessage output,
        int outputIndex) =>
        $"{inputMessageId}:gateway-output:{output.RuleId}:{output.TenantId}:{outputIndex}";
}
