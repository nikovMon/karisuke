namespace ImagingPipeline.Gateway;

using ImagingPipeline.Gateway.Errors;
using ImagingPipeline.Gateway.Contracts.Messages;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.Gateway.Processing.Rules;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

public sealed class GatewayWorker : BackgroundService, IRabbitMqMessageHandler
{
    private readonly IRabbitMqConsumer _consumer;
    private readonly ActiveRuleCache _ruleCache;
    private readonly GatewayInputMessageParser _inputParser;
    private readonly RuleMatcher _ruleMatcher;
    private readonly GatewayOutputMessageBuilder _outputBuilder;
    private readonly GatewayHealthState _healthState;
    private readonly ILogger<GatewayWorker> _logger;
    private readonly RabbitMqConsumerRestartBackoff _consumerRestartBackoff;

    public GatewayWorker(
        IRabbitMqConsumer consumer,
        ActiveRuleCache ruleCache,
        GatewayInputMessageParser inputParser,
        RuleMatcher ruleMatcher,
        GatewayOutputMessageBuilder outputBuilder,
        GatewayHealthState healthState,
        ILogger<GatewayWorker> logger,
        IOptions<RabbitMqClientOptions> rabbitMqOptions)
        : this(
            consumer,
            ruleCache,
            inputParser,
            ruleMatcher,
            outputBuilder,
            healthState,
            logger,
            TimeSpan.FromSeconds(rabbitMqOptions.Value.ReconnectDelaySeconds))
    {
    }

    internal GatewayWorker(
        IRabbitMqConsumer consumer,
        ActiveRuleCache ruleCache,
        GatewayInputMessageParser inputParser,
        RuleMatcher ruleMatcher,
        GatewayOutputMessageBuilder outputBuilder,
        GatewayHealthState healthState,
        ILogger<GatewayWorker> logger,
        TimeSpan consumerRestartDelay)
    {
        _consumer = consumer;
        _ruleCache = ruleCache;
        _inputParser = inputParser;
        _ruleMatcher = ruleMatcher;
        _outputBuilder = outputBuilder;
        _healthState = healthState;
        _logger = logger;
        _consumerRestartBackoff = new RabbitMqConsumerRestartBackoff(consumerRestartDelay);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var shouldRestart = false;
            var restartDelay = TimeSpan.Zero;
            var consumerStarted = Stopwatch.GetTimestamp();
            _healthState.MarkConsumerStarted();

            try
            {
                await _consumer.ConsumeAsync(this, stoppingToken);
                shouldRestart = !stoppingToken.IsCancellationRequested;
                if (shouldRestart)
                {
                    restartDelay = _consumerRestartBackoff.NextDelay(
                        Stopwatch.GetElapsedTime(consumerStarted));
                    _logger.LogWarning(
                        "RabbitMQ consumer exited unexpectedly; restarting in {RestartDelay}.",
                        restartDelay);
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
                    restartDelay = _consumerRestartBackoff.NextDelay(
                        Stopwatch.GetElapsedTime(consumerStarted));
                    _logger.LogWarning(
                        ex,
                        "RabbitMQ consumer failed; restarting in {RestartDelay}.",
                        restartDelay);
                }
            }
            finally
            {
                _healthState.MarkConsumerStopped();
            }

            if (shouldRestart)
            {
                await DelayBeforeRestartAsync(restartDelay, stoppingToken);
            }
        }
    }

    private static async Task DelayBeforeRestartAsync(TimeSpan restartDelay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(restartDelay, stoppingToken);
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
            var outputs = _outputBuilder.BuildOutputs(message.MessageId, input, matches);
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
            return Task.FromResult(RabbitMqMessageProcessingResult.NonRetryableFailure($"{ex.ErrorCode}: {ex.Message}"));
        }
        catch (GatewayProcessingException ex)
        {
            return Task.FromResult(RabbitMqMessageProcessingResult.RetryableFailure(ex.Message));
        }
    }

    private static string CreateOutputMessageId(
        string inputMessageId,
        GatewayOutputMessage output,
        int outputIndex) =>
        $"{inputMessageId}:gateway-output:{output.RuleId}:{output.TenantId}:{outputIndex}";
}
