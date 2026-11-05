# Scheduled DLQ recovery deployment

These are deployment examples for the [recovery worker](../../apps/dlq-recovery/README.md), not live production configuration. Both overlays start with `suspend: true` and `Recovery.DryRun: true`. Hosts, credentials, TLS choices, ports, virtual hosts and the tested image must be supplied before a run.

| Overlay | Namespace | Gateway source | Gateway replay |
| --- | --- | --- | --- |
| `integration` | `flare-intg` | `gatewaySource`: `ug.dlq` | Same connection: `ug.from.hoshen.prod` |
| `production` | `flare-prod` | `gatewaySource`: `ug.dlq` | Separate `gatewayTarget` connection: `flare.ug` |

Both overlays map `findair.publisher.dlq` to `findair.publisher` and `findair.consumer.dlq` to `findair.consumer` through the `findair` connection. The integration gateway input's `.prod` suffix is part of its actual queue name; it does not select production credentials.

The shared CronJob runs every five minutes, forbids overlapping scheduled Jobs, has no Job retry, and stops after four minutes. Worker limits leave time for cancellation and connection closure. Each run permits up to 50 scanned messages per queue and one replay publication per second across the job. These are initial limits to tune against real payload sizes and workload.

## Configuration and credentials

The image uses `/app` as its working directory. `DOTNET_ENVIRONMENT=Production` makes `Host.CreateApplicationBuilder` load the ConfigMap mounted at `/app/appsettings.Production.json`; environment variables from the Secret override it. Both deployed hosts use this filename. Their ConfigMaps set the actual observability deployment environment and log namespace independently.

1. Replace the image placeholder in the base or add an overlay image override with the tested image digest/tag.
2. Provision the namespace-local `dlq-recovery-connections` Secret using the environment's secret-management process. `connections-secret.example.yaml` shows the exact keys and is deliberately excluded from kustomization. Never commit filled-in credentials. The ConfigMap's `5672` is an example default; explicitly resolve each connection's AMQP port, virtual host and TLS settings. Do not infer the production gateway target from its source connection.
3. Add registry pull-secret references if required by the selected registry. The dedicated `dlq-recovery` ServiceAccount needs no Kubernetes API token or RBAC merely to use injected Secrets and connect to RabbitMQ.
4. Configure existing Logstash settings through environment overrides if required. The default emits structured console logs and does not require Elasticsearch credentials. The worker starts no HTTP metrics listener.

The Pod permits OpenShift to assign its non-root UID, drops all capabilities, uses the runtime-default seccomp profile and has a read-only root filesystem with a bounded writable `/tmp`. Network policy must allow the configured source and target brokers, DNS and any enabled logging endpoint.

## Broker preparation

The worker passively checks source, replay and manual queues. It does not declare or change these existing queues. Before running, pre-provision these durable, non-exclusive, non-auto-delete **classic** manual queues on their corresponding source DLQ broker/vhost:

- `findair.publisher.dlq.manual`
- `findair.consumer.dlq.manual`
- `ug.dlq.manual`

For example, an administrator using an already configured RabbitMQ management client can create one queue with:

```bash
rabbitmqadmin declare queue name=findair.publisher.dlq.manual durable=true auto_delete=false arguments='{"x-queue-type":"classic"}'
```

Repeat with the other names on their source connections. Do not add TTL, expiry, maximum-length eviction or policies that silently remove retained messages. Inspect effective broker policies as well as declaration arguments. Invalid, unknown and exhausted messages retain their full body and failure metadata until explicit manual handling.

Source credentials need source-DLQ inspection/consumption and manual-queue inspection/publication. They also need configure permission for temporary exclusive lock queues named `dlq-recovery.lock.<SHA256-of-source-queue>`; these locks are auto-delete and are unrelated to retention. Replay credentials need target-input inspection and publication. Queue inspection may require configure permission under the broker's permission model. Scope permissions to the configured queues and lock prefix.

Verify that the deployed application failure path returns to the source DLQ with failure/redrive headers intact. Production gateway spans brokers; input DLX arguments alone do not establish that cross-broker route. No inter-broker bridge is created by these manifests.

## Render and validate

These commands render locally without changing a cluster:

```bash
kubectl kustomize deploy/dlq-recovery/integration
kubectl kustomize deploy/dlq-recovery/production
```

Validate the rendered configuration against the target cluster and test with RabbitMQ 3.12.11 classic queues before rollout. Start in integration with a bounded dry run. A dry run reads source counts only: it requires source inspection access but does not acquire locks, consume messages or test target publication. Test transient replay, manual retention, lost publisher confirms, process termination and the production-style two-broker mapping with a small real batch.

Keep the schedule suspended while creating a controlled manual test Job through the deployment process. Enable mutations only after reviewing dry-run results by setting `Recovery.DryRun=false`. Unsuspend the schedule only after the mutation tests and queue mappings are verified. `concurrencyPolicy: Forbid` governs scheduled Jobs from this CronJob; the worker's source lock also coordinates managed manual runs. Do not use management Get/basic.get, purge, or unrelated DLQ consumers during a recovery run.

To stop further scheduled work, set `suspend: true`. Existing running Jobs are separate and must be stopped explicitly when required; unacknowledged deliveries become available after their connections close. A confirmed target publish followed by a lost source ACK can produce duplicates, including across brokers. A replay publication is not proof that downstream processing succeeded.
