# Scheduled DLQ recovery

This executable runs once and exits. OpenShift schedules it as a CronJob. It
replays known transient failures from the three configured DLQs at a bounded
rate. It makes no Projection Mapper calls and has no database dependency.

Source and replay connections are independent: production gateway can read
`ug.dlq` on one broker and publish to `flare.ug` on another. Integration gateway
uses `ug.from.hoshen.prod` as its replay queue. Deployment examples and the exact
environment mappings are under [deploy/dlq-recovery](../../deploy/dlq-recovery).

## Processing and retention

For each enabled mapping, the worker acquires an exclusive transient lock queue
on the source broker, snapshots the source ready count, then attaches an
exclusive consumer with prefetch one. The lock name is
`dlq-recovery.lock.<SHA256-of-source-queue-name>`; it disappears when its owning
connection closes. This coordinates scheduled and managed one-off runs before
the snapshot. It does not prevent an operator using management Get/purge: do not
perform those operations while the worker owns the queue.

At most `MaxMessagesPerQueue` from the initial snapshot are processed. Known
transient failures with remaining allowance are published persistently and with
mandatory routing to the exact target input queue. Publisher confirmation on
the target comes before the original delivery's ACK on its source channel. An
uncertain publication keeps the source unacknowledged and stops that mapping.
A crash between confirmed publication and source ACK can duplicate work.

Invalid, unknown and exhausted messages transfer to the source-side manual queue
with the same confirm-before-ACK sequence. The full body remains unchanged,
including polygons. The job never purges or ages out retained messages. Their
failure headers remain available for investigation. A live per-message expiry
is removed from the retained/replayed copy and recorded as `original-expiration`.

`redrive-count` persists across the existing three short retries; each scheduled
replay increments it and resets only the configured short-retry header. The
default three redrives can all be consumed during a prolonged dependency outage.
After that, work remains in the manual queue for an operator's decision.

Original IDs, correlation, trace context and typed headers such as gateway
`x-updated-fields` are preserved. Existing basic properties are copied; live
expiry and persistence are intentionally changed as described above. The original
AMQP `user_id` is moved to `original-user-id`, since the recovery publication is
made under a different authenticated broker connection.

The job reads input backlog on the **target** broker. It stops the mapping when
the threshold is reached or the depth cannot be read. The limit is ready queue
depth, not downstream service health, in-flight count or a precise PM request
rate. A replay may fan out or trigger short retries.

## Configuration

`Recovery:Connections` is a dictionary of named broker profiles with `Host`,
`Port`, `VirtualHost`, `Username`, `Password`, `TlsEnabled`, and optional
`TlsServerName`. Use environment variables/Secrets for credentials. TLS uses
normal certificate and hostname validation; install an internal CA in the
container trust store when required. No insecure certificate bypass is included.

`Recovery:Queues` is a dictionary with `Enabled`, `SourceConnection`,
`SourceQueue`, `ReplayConnection`, `ReplayQueue`, `ManualQueue`, and
`RetryCountHeader` (default `retry-count`). Connections refer to profiles, not
OpenShift namespaces. The exact same queue names may exist in both environments.

| Option | Default | Meaning |
| --- | --- | --- |
| Enabled | true | Disable the executable's work entirely |
| DryRun | true | Read source queue counts only; no consumption, classification or publication |
| MaxMessagesPerQueue | 50 | Scan and transfer limit for each mapping per run |
| MessagesPerSecond | 1 | Publication pace across all mappings, including manual transfers |
| MaxRedrives | 3 | Automatic replay allowance per service/message lineage |
| MaxRunSeconds | 210 | Job time budget including shutdown reserve |
| ShutdownReserveSeconds | 15 | Reserve for closing connections and flushing logs |
| OperationTimeoutSeconds | 10 | Bound each connection/AMQP operation |
| InputQueueBacklogLimit | 1000 | Stop when target ready depth reaches this value |
| ReplayDependencyResponseFailures | false | Explicit opt-in for malformed dependency responses; default manual retention |

Values are initial tuning settings, not measured production capacity. The
five-minute schedule and 240-second hard deadline are in the CronJob manifest.

Failure logs use the existing observability/Logstash route. Each mapping can
configure `FailureLogging:ExcludedPaths` with property names matched recursively
or explicit dot paths with `*` array segments. The default excludes common
geometry and binary fields. Retained bytes are never filtered. Large remaining
diagnostics are emitted as indexed JSON chunks with an event ID, count and hash
to avoid the general log serializer's silent field/string truncation. Log delivery
is still best effort; missing chunks are detectable. Unparseable JSON logs IDs,
length, hash and parse failure instead of dumping potentially heavy raw bytes.

## Provisioning and rollout

1. Deploy the handler/shared-client changes that add typed failure headers and
   enable `RabbitMq:EnrichedDeadLetterEnabled` for the three services.
2. Verify the exact failure destination. By default it is the configured DLQ on
   the application's input connection; use `RabbitMq:FailureDestination` for
   direct confirmed retention on another broker. Existing inter-cluster routes
   must preserve body, failure headers and redrive counters.
3. Provision `<DLQ>.manual` as durable classic queues on each source broker/vhost.
   Existing input, DLQ and manual queues are inspected passively, not redeclared
   with guessed arguments. Check effective policies: no message TTL, queue expiry,
   auto-delete or drop-head retention policy. A rejected retention publish leaves
   its source unacknowledged. Capacity planning must include indefinite retention.
4. Grant source DLQ read, manual-queue publish, passive-inspection permissions,
   and configure permission for the lock-queue prefix. Grant target publish and
   input inspection permissions. Permit network access to both clusters.
5. Fill deployment connection/image/Secret placeholders. Keep the CronJob suspended
   and dry-run enabled until integration validation is complete. A dry run checks
   source counts only; test actual target permissions with a small real batch.
6. Start with a small integration batch, verify application outcomes in
   Elasticsearch, then use the same procedure for production. Publication counts
   are not successful application-processing counts.

Legacy DLQ messages without trustworthy typed failure information are held for
manual handling. The job does not query Elasticsearch to guess their eligibility.
Operators may correlate logs and deliberately prepare a selected recovered input
for replay; do not mark every legacy message as transient or reset its budget in
bulk. Retained queues are not automatically consumed again.

## Operation and tests

Suspend the CronJob to pause scheduling; disable an individual mapping or lower
rate/batch settings through its configuration. A one-off Job created from the
CronJob uses the same ownership protocol. Do not overlap unmanaged UI/BasicGet
operations with it. Unknown broker errors cause nonzero exit; the next scheduled
run is the next automatic attempt (`backoffLimit: 0`).

```powershell
dotnet run --project apps/dlq-recovery -- --Recovery:Enabled=false
dotnet test tests/dlq-recovery-tests --filter 'Category!=Broker'
```

Broker tests **do not skip** when infrastructure is absent. Run two local
RabbitMQ 3.12.11 brokers with test credentials `admin/admin` on AMQP ports 5672
and 5673, then run:

```powershell
dotnet test tests/dlq-recovery-tests
npx nx affected -t build test --base=dev
```

The tests create only uniquely named `karisuke.recovery.*` queues and remove
those queues afterwards. They exercise cross-broker and same-broker replay,
unroutable destinations, source ACK loss, locking, retained originals and replay
budget exhaustion. The production inter-cluster failure route still needs
environment validation because its hosts and credentials are deployment inputs.
