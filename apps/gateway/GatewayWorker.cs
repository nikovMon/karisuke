namespace ImagingPipeline.Gateway;

using ImagingPipeline.Gateway.Errors;
using ImagingPipeline.Gateway.Contracts.Messages;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.Gateway.Processing.Rules;
using ImagingPipeline.Observability;
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
        _logger.ConsumerStarting();

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
                    MessagingTelemetry.RecordConsumerRestart(TelemetryErrorCategory.Unknown);
                    _logger.ConsumerRestartScheduled(restartDelay.TotalSeconds);
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
                    MessagingTelemetry.RecordConsumerRestart(TelemetryErrorCategory.Connection);
                    _logger.ConsumerRestartAfterFailure(ex, restartDelay.TotalSeconds);
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

        _logger.ConsumerStopped();
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
        var started = TelemetryTiming.StartTimestamp();
        var outcome = TelemetryOutcome.Failure;
        var error = TelemetryErrorCategory.Unknown;
        var rulesEvaluated = 0;
        var rulesMatched = 0;
        var outputCount = 0;

        PipelineTelemetry.RecordPayloadSize(PipelineStage.Gateway, PipelineDirection.Ingress, message.Body.LongLength);
        if (PipelineTimingHeaders.TryGetElapsedSeconds(message.Headers, out var elapsedSeconds))
        {
            PipelineTelemetry.RecordEndToEndDuration(PipelineStage.Gateway, elapsedSeconds);
        }

        try
        {
            var rules = _ruleCache.Current;
            rulesEvaluated = rules.Count;
            PipelineTelemetry.RecordBatchSize(PipelineStage.Gateway, PipelineItem.Rule, rulesEvaluated);

            GatewayInputMessage input;
            using (var parseActivity = StartStageActivity("parse"))
            {
                try
                {
                    input = _inputParser.Parse(message.Body);
                    parseActivity
                        .AddPipelineContext(imageId: input.ImageId)
                        .SetTelemetrySuccess();
                }
                catch (GatewayValidationException ex)
                {
                    parseActivity.SetTelemetryError(TelemetryErrorCategory.Validation, ex);
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    parseActivity.SetTelemetryError(
                        TelemetryErrorCategory.Cancelled,
                        ex,
                        recordException: false);
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    parseActivity.SetTelemetryError(TelemetryErrorCategory.Serialization, ex);
                    throw;
                }
            }

            Activity.Current.AddPipelineContext(
                imageId: input.ImageId,
                areaName: input.AreaOfInterest,
                sensorName: input.SensorName);

            using var pipelineScope = _logger.BeginTelemetryScope(new TelemetryLogContext(
                ImageId: input.ImageId,
                AreaName: input.AreaOfInterest,
                SensorName: input.SensorName));
            if (input.AreaOfInterest is null)
            {
                _logger.MissingAreaOfInterest(input.ImageId);
            }

            IReadOnlyList<RuleMatchResult> matches;
            using (var matchActivity = StartStageActivity("match"))
            {
                try
                {
                    matches = _ruleMatcher.Match(input, rules);
                    rulesMatched = matches.Count;
                    matchActivity.AddPipelineContext(imageId: input.ImageId);
                    if (matchActivity?.IsAllDataRequested == true)
                    {
                        matchActivity.SetTag("findair.gateway.rules.evaluated", rulesEvaluated);
                        matchActivity.SetTag("findair.gateway.rules.matched", rulesMatched);
                    }
                    matchActivity.SetTelemetrySuccess();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    matchActivity.SetTelemetryError(TelemetryErrorCategory.Handler, ex);
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    matchActivity.SetTelemetryError(
                        TelemetryErrorCategory.Cancelled,
                        ex,
                        recordException: false);
                    throw;
                }
            }

            GatewayTelemetry.RecordRuleMatching(rulesEvaluated, rulesMatched);
            PipelineTelemetry.RecordBatchSize(PipelineStage.Gateway, PipelineItem.Match, rulesMatched);

            IReadOnlyList<GatewayOutputMessage> outputs;
            using (var buildActivity = StartStageActivity("build"))
            {
                try
                {
                    outputs = _outputBuilder.BuildOutputs(input, matches);
                    outputCount = outputs.Count;
                    buildActivity.AddPipelineContext(imageId: input.ImageId);
                    if (buildActivity?.IsAllDataRequested == true)
                    {
                        buildActivity.SetTag("findair.output.count", outputCount);
                    }
                    buildActivity.SetTelemetrySuccess();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    buildActivity.SetTelemetryError(TelemetryErrorCategory.Serialization, ex);
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    buildActivity.SetTelemetryError(
                        TelemetryErrorCategory.Cancelled,
                        ex,
                        recordException: false);
                    throw;
                }
            }

            if (Activity.Current?.IsAllDataRequested == true)
            {
                Activity.Current.SetTag("findair.gateway.rules.evaluated", rulesEvaluated);
                Activity.Current.SetTag("findair.gateway.rules.matched", rulesMatched);
                Activity.Current.SetTag("findair.output.count", outputCount);
            }

            var outputMessages = new RabbitMqMessageEnvelope[outputs.Count];

            for (var outputIndex = 0; outputIndex < outputs.Count; outputIndex++)
            {
                outputMessages[outputIndex] =
                    message with
                    {
                        MessageId = CreateOutputMessageId(input.ImageId, outputs[outputIndex]),
                        Body = outputs[outputIndex].Body,
                        ContentType = "application/json",
                        Headers = FindAirMessageHeaders.Forward(
                            message.Headers,
                            outputs[outputIndex].AlgorithmNames),
                        CorrelationId = null
                    };

                PipelineTelemetry.RecordPayloadSize(
                    PipelineStage.Gateway,
                    PipelineDirection.Egress,
                    outputMessages[outputIndex].Body.LongLength);
            }

            outcome = TelemetryOutcome.Success;
            error = TelemetryErrorCategory.None;
            PipelineTelemetry.RecordFanOut(PipelineStage.Gateway, outputCount);
            // Output publication happens after the handler returns. Generated-output size
            // and fan-out are recorded here; confirmed transport outcomes come from the
            // RabbitMQ client metrics in RabbitMqOutcomeRouter.
            _logger.MessageProcessed(rulesEvaluated, rulesMatched, outputCount);
            WorkloadTelemetry.RecordImage(
                TelemetryOutcome.Success,
                input.AreaOfInterest,
                input.SensorName);
            foreach (var match in matches)
            {
                var algorithmNames = string.Join(",", match.Rule.AlgorithmNames);
                foreach (var tenant in match.Rule.TenantsInfo)
                {
                    WorkloadTelemetry.RecordTask(
                        PipelineDirection.Egress,
                        TelemetryOutcome.Success,
                        match.Rule.Id,
                        tenant.TenantId,
                        input.AreaOfInterest,
                        input.SensorName,
                        algorithmNames);
                }
            }

            return Task.FromResult(RabbitMqMessageProcessingResult.Success(outputMessages));
        }
        catch (GatewayValidationException ex)
        {
            outcome = TelemetryOutcome.Rejected;
            error = TelemetryErrorCategory.Validation;
            _logger.MessageRejected(ex.ErrorCode, ex.Message);
            return Task.FromResult(RabbitMqMessageProcessingResult.NonRetryableFailure($"{ex.ErrorCode}: {ex.Message}"));
        }
        catch (GatewayProcessingException ex)
        {
            outcome = TelemetryOutcome.Retry;
            error = ex is GatewayDependencyException
                ? TelemetryErrorCategory.Dependency
                : TelemetryErrorCategory.Handler;
            _logger.MessageScheduledForRetry(ex, error.ToString());
            return Task.FromResult(RabbitMqMessageProcessingResult.RetryableFailure(ex.Message));
        }
        catch (OperationCanceledException)
        {
            outcome = TelemetryOutcome.Cancelled;
            error = TelemetryErrorCategory.Cancelled;
            throw;
        }
        catch
        {
            outcome = TelemetryOutcome.Retry;
            error = TelemetryErrorCategory.Handler;
            throw;
        }
        finally
        {
            PipelineTelemetry.RecordMessage(PipelineStage.Gateway, PipelineDirection.Ingress, outcome, error);
            PipelineTelemetry.RecordStageDuration(
                PipelineStage.Gateway,
                TelemetryTiming.ElapsedSeconds(started),
                outcome,
                error);
        }
    }

    private static Activity? StartStageActivity(string operation)
    {
        var spanName = operation switch
        {
            "parse" => "gateway.parse",
            "match" => "gateway.match",
            "build" => "gateway.build",
            _ => "gateway.stage"
        };
        var activity = TelemetrySources.Gateway.StartActivity(spanName, ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, "gateway");
            activity.SetTag("findair.operation", operation);
        }
        return activity;
    }

    private static string CreateOutputMessageId(
        string imageId,
        GatewayOutputMessage output) =>
        $"{imageId}:gateway-output:{output.RuleId}:{output.TenantId}";
}
