using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.DlqRecovery;

public sealed class RecoveryRunner(RecoveryOptions options, IRecoverySessionFactory sessions,
    ILogger<RecoveryRunner> logger, TimeProvider clock)
{
    private long? _lastPublication;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (!options.Enabled) return 0;
        var runId = Guid.NewGuid().ToString("N");
        DlqRecoveryLog.Started(logger, runId, options.DryRun);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.MaxRunSeconds - options.ShutdownReserveSeconds));
        var failed = false;
        foreach (var (name, mapping) in options.Queues.Where(pair => pair.Value.Enabled))
        {
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (options.DryRun)
                {
                    var count = await sessions.InspectAsync(mapping, deadline.Token);
                    DlqRecoveryLog.Inspected(logger, name, count);
                }
                else
                    await ProcessQueueAsync(name, mapping, runId, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                DlqRecoveryLog.Stopping(logger, runId);
                break;
            }
            catch (Exception ex)
            {
                // Connection exception messages can contain deployment details. Log the type only.
                DlqRecoveryLog.Failed(logger, name, ex.GetType().Name);
                failed = true;
            }
        }
        return failed ? 1 : 0;
    }

    private async Task ProcessQueueAsync(string name, RecoveryQueueOptions mapping, string runId, CancellationToken token)
    {
        await using var session = await sessions.OpenAsync(mapping, token);
        var budget = (int)Math.Min(session.InitialCount, (uint)options.MaxMessagesPerQueue);
        var scanned = 0;
        var replayed = 0;
        var retained = 0;
        var reason = "initial-cohort-complete";
        try
        {
            for (var i = 0; i < budget; i++)
            {
                token.ThrowIfCancellationRequested();
                if (await session.ReadInputDepthAsync(token) >= options.InputQueueBacklogLimit)
                {
                    reason = "input-backlog";
                    break;
                }
                var delivery = await session.ReceiveAsync(token);
                if (RecoveryPolicy.IsFromRun(delivery, runId))
                {
                    reason = "same-run-return";
                    break;
                }
                scanned++;
                var decision = RecoveryPolicy.Decide(delivery, options);
                var prepared = RecoveryPolicy.Prepare(delivery, decision, mapping, runId, clock.GetUtcNow());
                await PaceAsync(token);
                if (decision.Replay)
                    await session.PublishReplayAsync(prepared, token);
                else
                    await session.PublishManualAsync(prepared, token);
                if (decision.Replay) replayed++;
                else retained++;
                // Never settle the source before the destination broker confirms a routed publication.
                await session.AckAsync(delivery.Tag, token);
                TryLogFailure(name, mapping, prepared, decision);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "cancelled-or-operation-timeout";
            throw;
        }
        catch
        {
            reason = "broker-error";
            throw;
        }
        finally
        {
            DlqRecoveryLog.Completed(logger, name, scanned, replayed, retained, reason);
        }
    }

    private async Task PaceAsync(CancellationToken token)
    {
        if (_lastPublication is { } last)
        {
            var wait = TimeSpan.FromSeconds(1 / options.MessagesPerSecond) - clock.GetElapsedTime(last);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, token);
        }
        _lastPublication = clock.GetTimestamp();
    }

    private void TryLogFailure(string name, RecoveryQueueOptions mapping, RecoveryDelivery delivery, RecoveryDecision decision)
    {
        try
        {
            RabbitMqFailureLog.Log(logger, delivery.ToEnvelope(), decision.Failure, name, mapping.SourceQueue,
                decision.Replay ? mapping.ReplayQueue : mapping.ManualQueue,
                new RabbitMqFailureLogOptions
                {
                    ExcludedPaths = mapping.FailureLogging.ExcludedPaths,
                    RetryCountHeader = mapping.RetryCountHeader
                });
        }
        catch (Exception ex)
        {
            try { DlqRecoveryLog.LoggingFailed(logger, ex.GetType().Name); }
            catch { /* Logging must not affect settlement. */ }
        }
    }
}
