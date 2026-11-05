# Scheduled DLQ recovery without a new database

Revised 2026-11-03 to reflect the user's latest direction: one configurable OpenShift CronJob handles the three applications' DLQs; invalid and exhausted messages retain their full original payload and failure information indefinitely in RabbitMQ; Elasticsearch receives failure information and the original payload with polygons and other heavy fields omitted. There is no MongoDB/Redis dependency, additional rule-relevance check or Projection Mapper availability check. This supersedes the earlier intake/archive redesign and dependency-probe proposal. The implementation is on feature/dlq/scheduled-recovery; production configuration and queued messages have not been changed. Runtime details and rollout commands are in [the worker README](../apps/dlq-recovery/README.md).

The initial review used dev commit 07039cefa95f27a20c0ee61d90e8b068fd6e7235; implementation started after fast-forwarding dev to origin/dev commit 6c94666. The production facts supplied by the user are RabbitMQ 3.12.11, durable classic input and DLQ queues, three short retries with 10-second delays, retained DLQs, acceptable out-of-order processing and acceptable duplicates with downstream tile-ID deduplication. The deployed queue names below supersede the checked-in appsettings defaults. Production gateway replay must support different brokers for its source DLQ and target input.

Implement one small executable, for example apps/dlq-recovery, deployed as an OpenShift/Kubernetes CronJob. It wakes on a configured schedule, checks the three DLQs, replays a bounded eligible cohort, logs the result and exits. Normal application processing remains on its current RabbitMQ path.

Deploy and validate first in flare-intg, then deploy separately in flare-prod with environment-specific RabbitMQ connections, queue mappings and Secret references. The supplied Kubernetes server version is v1.29.10+67d3387, which supports batch/v1 CronJob. The supplied Client Version 4.6.15 identifies the local CLI, not the OpenShift server release. An exact OpenShift release is not required to finish this design. Follow existing application image-registry, ServiceAccount and Secret conventions; resolve the concrete names when preparing deployment manifests. Each environment's job must access only that environment's configured queues. Checked-in manifests start suspended and in dry-run mode while deployment values remain placeholders.

The user supplied these deployed mappings. All listed input and DLQ queues are classic and durable:

| Environment | Application | DLQ to read | Input queue to replay into |
| --- | --- | --- | --- |
| flare-prod | tb-publisher | findair.publisher.dlq | findair.publisher |
| flare-prod | tb-consumer | findair.consumer.dlq | findair.consumer |
| flare-prod | unified-gateway | ug.dlq | flare.ug |
| flare-intg | tb-publisher | findair.publisher.dlq | findair.publisher |
| flare-intg | tb-consumer | findair.consumer.dlq | findair.consumer |
| flare-intg | unified-gateway | ug.dlq | ug.from.hoshen.prod |

In production, ug.dlq and flare.ug are on different RabbitMQ clusters. In integration, ug.dlq and ug.from.hoshen.prod are on the same cluster. Preserve the integration queue name exactly, including its .prod suffix; it does not indicate which environment the CronJob should access. All input queues have dead-letter exchange and routing-key arguments. For integration gateway input, the supplied exchange is ug.dlx and routing key is ug.dead. RabbitMQ's argument name is x-dead-letter-routing-key; verify that spelling in the deployed arguments, since the user's text wrote x-dead-letter-routing.key. Other dead-letter exchange/routing-key values are not yet supplied and must not be inferred from queue names.

Each recovery mapping has a source connection reference, source DLQ, replay connection reference, replay input queue and manual-retention queue. The source and replay references can identify the same connection or different brokers/vhosts. Use different broker connections for production gateway and the same cluster for integration gateway, with the actual virtual hosts supplied later. Do not infer source DLQ placement from the application's RabbitMq:InputCluster option: the checked-in client declares its input/retry/DLQ topology together, whereas the supplied deployed production gateway topology spans clusters. Use the explicit recovery mapping instead.

Define the manual-retention queue on the source DLQ's broker/vhost, provisionally findair.publisher.dlq.manual, findair.consumer.dlq.manual and ug.dlq.manual in each environment. The recovery job publishes directly to the target input queue; it does not need the application's output destinations or PM connection configuration. Before rollout, verify the existing deployed failure route from each input back to its DLQ, including any inter-cluster transfer for production gateway. An input queue's DLX/routing-key arguments alone do not establish that remote path. Confirm that the path preserves the new failure and redrive headers. This is a rollout verification of existing infrastructure, not a new dependency on PM or an instruction to build a bridge.

Hosts, AMQP ports and virtual hosts will be supplied later and are explicitly not blockers for the design. Leave connection profiles as unresolved deployment placeholders rather than substituting localhost, port 5672 or virtual host / from development defaults. Resolve TLS settings and credential Secret names/key names per source and target connection when preparing manifests. Source credentials cover DLQ consumption, source inspection and manual-retention publishing; target credentials cover replay publication and target input-queue inspection. Verify permissions for the selected topology provisioning approach. The CronJob needs network access to both clusters when a mapping spans them. Secret values are not needed in the design.

Resolve the Pod's serviceAccountName if one is prescribed by the existing deployment convention; otherwise define a dedicated dlq-recovery ServiceAccount. The job does not require Kubernetes API access merely to use mounted/injected Secrets or connect to RabbitMQ. Record imagePullSecrets only if required by the image registry. Existing application logging configuration should be reused; Elasticsearch credentials used by Logstash are not required by the recovery job itself. Actual RabbitMQ Secret and ServiceAccount names are absent from the checked-in application deployment fragments and can be supplied with the other deployment values.

The intended flow is:

```text
Application input -> normal handling and existing short retries -> DLQ
                                                                  |
                                                scheduled recovery job
                                                                  |
                                   classify -> rate and retry limits -> replay
                                                                  |
                                                   original service input
```

A representative starting configuration is:

| Setting | Initial proposal |
| --- | --- |
| Schedule | Every five minutes; configurable |
| Maximum replay per queue per run | 50; tune using observed backlog and dependency behavior |
| Publish pace | At most one replay message per second across the job |
| Maximum automatic redrives | Three per message lineage |
| Job execution | One worker; no overlapping scheduled runs |
| Run deadline | Four minutes; stop taking new work before shutdown |
| Manual controls | Suspend schedule, enable individual queues, reduce rate/batch, source-count-only dry run, controlled one-off run |
| Invalid/unknown/exhausted retention | Indefinite in RabbitMQ manual-retention queues, with full original payload and failure information; no automatic deletion |

These are test values, not production capacity measurements. A publication rate is not the same as a processing or PM-request rate: a replay can fan out and make short retries. Read the input-queue depth through the replay connection and pause when it exceeds a configured backlog threshold, so replay does not flood normal input. Source DLQ depth and ownership use the source connection. These are RabbitMQ queue checks, not PM health or dependency telemetry. If queue depth cannot be read, stop the run rather than treating it as empty.

Use concurrencyPolicy: Forbid for the CronJob, bounded restart/backoff behavior and explicit starting/active deadlines. Forbid applies to that CronJob's scheduled Jobs; it is not a global lock and a scheduler can still produce duplicate execution in some circumstances. Design the transfer to tolerate duplicates. All managed manual runs must follow the same queue ownership protocol. [Kubernetes CronJob behavior](https://kubernetes.io/docs/concepts/workloads/controllers/cron-jobs/)

Each run follows this sequence:

1. Resolve each mapping's source and replay connections, and read queue depths on the appropriate brokers/vhosts. An empty DLQ requires no replay; management/AMQP errors are not treated as empty.
2. Acquire an exclusive transient lock queue on the source connection, snapshot the DLQ ready count while holding that lock, then attach an exclusive DLQ consumer. The broker removes the lock when the connection closes; it is separate from the durable retained-message queues.
3. Inspect at most min(initial ready count, configured scan/batch limit), using small prefetch and manual ACK. Fresh arrivals and messages that return from this run's retries are left for a later run.
4. Classify each message from its recorded failure metadata. Move invalid, unknown and attempt-exhausted work into the manual-retention queue on the source broker/vhost with confirmed publish-before-ACK; never repeatedly NACK the same ineligible head in a tight loop.
5. For a recoverable message within its redrive allowance, apply the configured publication rate and input-queue backlog limit. Do not call PM, check its health, or wait for a processing outcome before releasing the next eligible message.
6. Publish eligible messages persistently through the explicit replay connection directly to the allowlisted input queue, using the default exchange and queue name when appropriate. For production gateway this crosses from the ug.dlq cluster to the flare.ug cluster. Do not republish to a shared exchange that could fan out into additional queues, or use an input queue's dead-letter routing key as the replay target. Preserve original body, MessageId/CorrelationId and required typed headers. Clear AMQP user_id before republishing under the recovery connection and retain it as original-user-id for audit. Increment the automatic-redrive counter and reset only the existing short-retry counter.
7. Wait for mandatory-routing success and publisher confirmation on the target broker before acknowledging the DLQ delivery on its original source consumer channel. If target publication fails or is uncertain, retain/requeue the source delivery and stop or back off. If target publication succeeds but the source ACK is lost, a duplicate can result; there is no atomic cross-broker publish/ACK transaction.
8. Stop on the run deadline, batch limit, excessive input-queue backlog or RabbitMQ errors. Report scanned, replay-published, held, skipped and failed counts. PM availability does not control the run.

In the initial version, the schedule provides the delay; there is no arbitrary per-message next-attempt scheduler or new TTL/DLX loop. Scan only the initial bounded FIFO cohort, with no rotations of its ineligible messages back into the same queue. This prevents a quickly failing item from being repeatedly replayed within one run. Use a run ID for diagnostics and additional duplicate protection where feasible. The run deadline must leave time to finish a transfer or close the channel with its delivery still unacknowledged.

An exclusive AMQP consumer coordinates cooperating consumers, not every possible broker operation. Direct management Get messages/basic.get or purges can bypass that ownership model; operators must not use those against a queue being processed. Disable other DLQ consumers for that queue during the run. Shutdown/failed runs leave unacknowledged work available for later delivery.

There is a required application change before reliable classification is possible. [RabbitMqMessageProcessingResult](../libs/rabbitmq-client/RabbitMqMessageEnvelope.cs) currently has a string error plus retry/dead-letter action. [RabbitMqOutcomeRouter](../libs/rabbitmq-client/RabbitMqOutcomeRouter.cs) sends BasicNack(requeue: false) and does not place the detailed reason into the DLQ message. PM wraps different failures in the same [exception type](../libs/projection-mapper-client/ProjectionMapperClientException.cs). The job cannot infer a transient PM error from queue count, x-death or retry-count alone.

Add typed failure information in the dependency client and handler results, then preserve bounded metadata on final failure:

```text
failure-class
failure-code
failure-message / failure-service
dependency / operation
http-status                 when applicable
first-failure-at / last-failure-at
redrive-count
last-redrive-at / run-id
```

A BasicNack cannot attach new metadata to the broker's existing message. For the enriched final failure, explicitly publish the retained message to the configured failure destination with mandatory routing and confirms, then ACK the application's input delivery. Resolve the actual destination broker/vhost or the existing confirmed failure route rather than assuming InputCluster also owns the retained DLQ. For production gateway, verify that the deployed inter-cluster failure route preserves original body and failure/redrive metadata. Retain an emergency broker DLQ for unforeseen broker/application rejection paths.

| Failure classification | Automatic action |
| --- | --- |
| Known timeout, connection failure, retryable 408/429/5xx | Eligible for bounded replay on a later scheduled run, subject to rate and redrive limits |
| Locally verified invalid JSON or source fields | No automatic replay |
| Invalid PM response/geometry | Distinct dependency-response failure; use a bounded explicit policy, not an invalid-source label |
| Authentication/authorization/configuration fault | Hold until fixed |
| Unhandled defect, unrecognized metadata or ambiguous status | Hold; do not guess |
| Redrive limit reached | Hold for manual decision |

For future failures, separate automatically recoverable work from manual/invalid work by routing or a clearly defined manual queue. This avoids repeatedly scanning the same permanent failures. Existing mixed DLQs need a bounded triage pass. Correlate available Elasticsearch logs with stable message identifiers when the root cause is missing; unreliably matched or absent evidence remains unknown.

Retention is now an explicit requirement: confirmed invalid messages, unknown failures and messages that exhaust automatic replay remain available indefinitely for manual handling. Retain the original body byte-for-byte, including all polygons and heavy fields, plus identifiers, required application headers and structured failure information. Keep these messages in durable manual-retention queues outside the automatic scan, with persistent publications and confirmed transfer before acknowledging the source. No age-based deletion, automatic purge, or deletion on successful logging is included. Manual handling is an explicit operator action, not another automatic loop.

Indefinite retention requires checking effective broker policies as well as declarations: retention queues must have no message TTL, queue expiry, auto-delete or policy that drops retained messages at a size limit. Do not propagate a live per-message expiration into a retained publication; preserve any original expiry value as diagnostic metadata instead. If a configured limit rejects a retention publication, retain the source delivery and report the failure. The existing ten-second TTLs on short-retry queues remain unchanged. Verify these properties on the deployed RabbitMQ 3.12.11 queues; absence of TTL in appsettings alone is insufficient. [RabbitMQ expiration behavior](https://www.rabbitmq.com/docs/3.13/ttl) and [queue overflow behavior](https://www.rabbitmq.com/docs/3.13/maxlength) describe the relevant mechanisms; those archived pages document 3.13 rather than certify the production configuration.

Elasticsearch failure logs must include the original message's non-heavy fields and the failure information: service, source queue, retained queue, MessageId/CorrelationId, failure class/code/message, dependency/status where available, failure timestamps, short-retry count and redrive count. Generate this logging copy separately from the retained body. Remove polygon/geometry data (for example footprint and roiFootprint) and other configured heavy fields recursively using the actual message schemas. Include omitted field paths and the original byte length so the removal is explicit. Apply the same removal to embedded payloads in failure details so heavy data is not reintroduced through exception logging.

Keep the heavy-field exclusion paths configurable per service; derive initial paths from its input DTOs and gateway source schema. For malformed JSON that cannot be reliably filtered, retain the complete original bytes in RabbitMQ and log the parse failure, identifiers, byte length and body hash without dumping the unfiltered body. Validate the existing log serializer/exporter limits so non-heavy fields are not silently truncated; the checked-in observability fragment currently limits attributes, collection entries and string lengths. Logging failures must not delete or alter the retained original. Logs are for investigation; RabbitMQ holds the full message for manual recovery.

The job decides eligibility using the recorded reason for the previous failure and the message's redrive count. A timeout can justify another attempt; it does not prove PM has recovered. There are no PM health requests, test mapping requests, canary-message gates or Elasticsearch outcome checks before replay. The job does not need a PM URL, health endpoint, test payload or PM credentials. Normal application handlers still call their dependencies as they do today.

If PM is still unavailable, replayed messages can fail again, use the existing short retries and return to the DLQ. A subsequent scheduled run can replay them again within the persistent redrive limit. With the illustrative five-minute schedule and three-redrive cap, all automatic attempts can be spent during one prolonged outage. The schedule is not a guaranteed minimum delay from an individual failure; actual timing also depends on the backlog and job execution. Keep the schedule and cap configurable and make exhausted messages visible for a manual decision.

Successful replay publication means RabbitMQ accepted the transfer, not that application processing succeeded. Observe application outcomes in Elasticsearch for tuning and investigation, without making those logs a prerequisite for the job to proceed. Ordinary application failure handling retains failed work; the job must not create another DLQ copy merely because it cannot find a success log. Invalid, unknown and exhausted messages remain retained for manual handling rather than automatic disposal.

Preserve all application headers needed by the target handler. The existing [PublishToInputAsync](../libs/rabbitmq-client/RabbitMqPublisher.cs) filters headers and does not reset retry count, so it cannot be reused unchanged. Gateway requires x-updated-fields as a typed array. Add a dedicated redrive operation with separate source consumer and target publisher connections. It selects the mapping's explicit replay broker/vhost, preserves the handler context, resets short retry-count to zero, and increments the persistent redrive-count. Short retries and the final-failure route within that service must preserve redrive metadata; successful forwarding to another stage need not carry that stage's retry budget.

With three short retries, each processing cycle has up to four handler executions. If the redrive cap is three, the nominal bound is four initial executions plus three groups of four: sixteen executions per normal message lineage. Broker redelivery/confirm ambiguity can add physical executions; duplicates are accepted, but dependency limits and metrics must account for them. Do not claim a strict globally unique execution ceiling without durable deduplication.

No new current-rule lookup, original-source archive or changed-rule generation mechanism is included. Replay resumes the failed service with its existing input. Replaying a gateway source will still execute that application's normal rule matching, because that is its present handler behavior. This is not an additional rule-relevance step in the recovery job.

Publish-before-ACK means a crash can duplicate a replay if the new copy was accepted but the old DLQ ACK was lost. That is preferable to acknowledging before publishing and losing work. The user already accepts duplicates. [RabbitMQ reliability guidance](https://www.rabbitmq.com/docs/reliability/)

The new job improves recovery when a later attempt succeeds. During a dependency outage, it continues bounded replay without knowing the dependency's state; waiting and failing data remains in RabbitMQ. Moving messages from DLQ to input, retry or manual queues does not reduce total retained bytes. With indefinite retention, unresolved invalid/exhausted messages continue accumulating; this design cannot promise a fixed broker storage footprint. Capacity and alerts must account for that retained data, even when automatic recovery works. Monitor combined input/retry/DLQ/manual bytes and oldest age, alongside broker disk/memory headroom. Messages that exhaust their allowance during the outage need manual action even after the dependency recovers.

Classic TTL/DLX transfers already used by the current short retry path have their existing loss window when targets are unavailable. The cron job cannot recover messages that never reached a DLQ. Explicit confirmed redrive improves its own handoff, not every existing broker hop. [RabbitMQ dead-letter safety](https://www.rabbitmq.com/blog/2022/03/29/at-least-once-dead-lettering)

Implement in three small increments:

| Increment | Change | Evidence |
| --- | --- | --- |
| Classify and retain | Typed failures, enriched final-failure messages, explicit failure destinations, indefinite manual retention, failure logs with heavy fields omitted | Transient/invalid/unknown fixtures route correctly; full originals and failure metadata are retained; logs preserve non-heavy fields and omit geometry |
| Scheduled redrive | Cron configuration, separate source/target connection profiles, exclusive ownership, initial bounded cohort, publisher confirms, retry-counter handling, input-queue backlog limit | Correct same-broker and cross-broker replay, no PM calls from the job, no same-run loop, safe crash/restart, no overlap between cooperating runs |
| Integration validation and production rollout | Validate in flare-intg, start flare-prod with a small batch, review Elasticsearch outcomes and adjust rate/batch/limits | Confirmed processing outcomes improve; fresh latency and broker footprint remain acceptable |

Tests should cover replay without any PM access from the job, application PM failures followed by recovery, a prolonged outage exhausting the redrive allowance, invalid/unknown messages, missing legacy metadata, excessive or unreadable input-queue depth, exhausted redrive counters, mixed queue heads, a message returning during the same run, preserved gateway headers, separate input/output brokers, environment isolation, unroutable publication, lost confirm, crash after confirmed publish before DLQ ACK, job shutdown and simultaneous managed runs. Run broker tests explicitly against RabbitMQ 3.12.11 classic queues; do not accept silently skipped safety tests.

Topology verification must include ug.dlq to flare.ug across two brokers, target connection failure leaving the source delivery available, lost source ACK after target confirmation, input-queue depth checks on the target rather than source broker, and same-cluster ug.dlq to ug.from.hoshen.prod in integration. Test a replay that fails again and travels through the deployed failure route back to a managed DLQ with its redrive count intact. Manual-retention transfers must stay on the source broker/vhost. Validate all effective input DLX bindings without redeclaring existing queues with guessed arguments.

Retention/logging verification must additionally cover full byte-for-byte payload preservation including polygons, retained failure metadata, effective TTL/expiry/overflow policies, removal of live message expiration on retention transfer, heavy-field exclusion in logs, completeness of remaining log fields through the actual exporter, malformed JSON fallback and log-delivery failure without message loss. Do not interpret a finite test as proof of unlimited future retention; verify that no configured automatic expiry/deletion path exists and monitor operational capacity.

Report replay publication separately from downstream success. Use bounded failure/dependency labels in metrics, and IDs in logs. Elasticsearch can show actual application outcomes and repeated failures. Weekly DLQ event counts alone are not a unique recovery count.

The design is implemented with unit and RabbitMQ broker tests. No production queues were modified and nothing has been deployed. Deployment connection values, effective retention policies and the production inter-cluster failure route remain environment rollout checks.
