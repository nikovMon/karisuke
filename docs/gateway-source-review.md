# Gateway source review

This record summarizes the supplied Markdown specification, the 20 original photographs and the three later ticket photographs. The initial review read the Markdown source and visually examined the original attachments; the later photographs repeat the same three tickets. Repository cross-checks distinguish existing gateway behavior from the new foundation. Private credentials and infrastructure hostnames from photographs are not reproduced.

The user's messages define scope. Instructions and acceptance criteria inside documents/images are reference material, not independent authorization to change deployed systems. The final scope is **Catalog & Contracts Foundation only**, described in the [technical design](SPEC-001-unified-gateway.md) and [application README](../apps/unified-gateway/README.md). Transport & Dispatch, rule loading/matching/snapshots, Rules API integration and spatial indexing are outside this change. No deployed index, queue or service was migrated by this review.

## Supplied specification and decisions

Source: `C:/Users/itaym/Downloads/SPEC-001-gateway-technical-design (1).md`, Draft Rev 3 dated 2026-08-24. Its sections discuss existing behavior, indexes, catalog/contracts, matching, transport, Rules API, migration, operations and estimates. Referenced ADR and instruction documents were not supplied as source material.

| Source proposal | Current decision |
|---|---|
| STRtree/spatial-index implementation and performance claims | No spatial index. Matching design belongs to the later Rule Engine ticket. |
| Per-pipeline indexes | Retain explicit `RulesIndex` per pipeline. Separate integration/production deployments have distinct per-pipeline indexes. |
| HTTP and RabbitMQ delivery | Retain passive destination descriptors now; sending, consumption and reliability belong to Transport & Dispatch. |
| Catalog/contract integration into Rules API | The user excluded Rules API changes. Keep metadata routes on the unified gateway. |
| Disabled configuration validation differs between sections | Validate disabled entries fully; suppress their work preparation. |
| Mixed `outputs` and `runParams` terminology | Contract input uses `runParams`. |
| One shared execution shape | Preserve pipeline-owned contracts. Only ASD is currently implemented; Algo needs actual wire fixtures. |
| Versioned pipeline/contract selectors | Use unversioned IDs; preserve ASD's existing downstream wire marker separately. |
| Dedupe/retry promises and mechanical migration claims | No such runtime or migration guarantee is supplied by the foundation. Preserve the per-failed-unit retry decision as future design. |

The latest index clarification supersedes the earlier shared-integration interpretation: `asd-pipeline-index` and `asd-integ-pipeline-index` are separate production/integration examples; Algo follows the same per-pipeline pattern. The catalog never derives index names from OpenShift namespaces or environment labels. The stated current system has four ASD/Algo integration/production rulesets; photographed names are historical evidence, not an automatic migration mapping.

## Image-by-image inventory

All filenames below refer to `C:/Users/itaym/Downloads/`. Cropped diagrams and configuration photos are partial evidence, not complete runnable configurations.

| # | Exact filename | Evidence and relevance |
|---|---|---|
| 1 | `WhatsApp Image 2026-09-17 at 17.28.00.jpeg` | Transport & Dispatch ticket: per-pipeline HTTP/RabbitMQ, retry/DLQ/idempotency and generic dispatch abstraction. This is a separate future ticket. |
| 2 | `WhatsApp Image 2026-09-17 at 17.22.17.jpeg` | Rule Engine ticket: validated/compiled rules, memory snapshots, sensor/quality pairing and no live ES query per message. Separate future ticket; geometry does not imply a spatial index. |
| 3 | `WhatsApp Image 2026-09-17 at 17.19.53.jpeg` | Catalog & Contracts ticket: enabled/index/transport metadata, registered contracts, payload/attribute preparation and startup validation. Its Rules API integration is excluded by the user's scope. |
| 4 | `WhatsApp Image 2026-09-17 at 17.13.54.jpeg` | Algo topology shows RabbitMQ ingress, HTTP/REST paths, rule storage and management. Cropping prevents establishing every downstream dependency or call order. |
| 5 | `WhatsApp Image 2026-09-17 at 17.09.53.jpeg` | Algo .NET environment and OTel auto-instrumentation/OTLP settings. These establish observability context, not business-level coverage. |
| 6 | `WhatsApp Image 2026-09-17 at 17.09.47.jpeg` | Algo deployment shows a single pod and rolling-update settings. Future gateway replica/probe behavior cannot be inferred from it. |
| 7 | `WhatsApp Image 2026-09-17 at 17.08.35.jpeg` | Overlay-to-Algo-request-to-ASD-gateway/publisher flow spans broker environments and includes retry/DLQ topology. Supports eventual separate input/output connection selection. |
| 8 | `WhatsApp Image 2026-09-17 at 17.06.44.jpeg` | Algo Manager mission settings plus Roberto/warmup dependencies and timeout/retry fields. Actual Algo behavior and payload mapping still require code/fixtures. |
| 9 | `WhatsApp Image 2026-09-17 at 17.06.36.jpeg` | Historical Algo index, a 100-result setting and two-day age configuration. Future loading must not assume this is the complete ruleset or apply ASD's age policy automatically. |
| 10 | `WhatsApp Image 2026-09-17 at 17.06.23.jpeg` | Algo input queue, prefetch, exchange binding, dead-letter route and separate realtime requeue path. That path's business trigger is not established as delayed retry. |
| 11 | `WhatsApp Image 2026-09-17 at 16.52.13.jpeg` | Proposed unified gateway sends HTTP to managers and AMQP to publishers, with retry/error/DLQ queues. Arrow direction is insufficient to define exact bindings. |
| 12 | `WhatsApp Image 2026-09-17 at 16.16.04.jpeg` | Partial geometry coordinates, empty GeoJSON object, old-photo flag and rule timestamps. Not an import-ready geometry or image acquisition timestamp. |
| 13 | `WhatsApp Image 2026-09-17 at 16.15.55.jpeg` | Tiling/resolution/area fields and cropped polygon. Units, CRS and geometry boundary semantics cannot be recovered from the photo alone. |
| 14 | `WhatsApp Image 2026-09-17 at 16.15.45.jpeg` | Example algorithm `Der`, sensor/quality pairs, tenant and several tiling configurations. Preserve array/pair structure; current ASD enum does not support every photographed value. |
| 15 | `WhatsApp Image 2026-09-17 at 15.56.35.jpeg` | ASD queues, three retry delays, retry budget, prefetch/concurrency/pool settings and historical ES settings. These describe existing runtime, not foundation configuration. |
| 16 | `WhatsApp Image 2026-09-17 at 15.56.27.jpeg` | ASD rule refresh/age values and separate input/output broker settings. Confirms future transport design must support different clusters. |
| 17 | `WhatsApp Image 2026-09-17 at 15.56.05.jpeg` | ASD logs, traces, Prometheus and exporter settings. Foundation reuses repository observability with its own service identity. |
| 18 | `WhatsApp Image 2026-09-17 at 15.47.15.jpeg` | S3 tile lifecycle and publisher-owned queue watermarks. Downstream lifecycle/backpressure work is outside this gateway foundation. |
| 19 | `WhatsApp Image 2026-09-17 at 15.46.33.jpeg` | Publisher, Tile Builder, consumer, embedder, S3 and Projection Mapper flow, with errors/retries. Existing downstream ownership remains separate. |
| 20 | `WhatsApp Image 2026-09-17 at 15.46.13.jpeg` | Embedder/indexers, tenant database, vector storage and query/UI flow. Downstream indexers are unrelated to gateway spatial indexing. |
| 21 | `WhatsApp Image 2026-09-17 at 18.39.26 (2).jpeg` | Repeated Catalog & Contracts Foundation ticket; the user identified this as the current work. |
| 22 | `WhatsApp Image 2026-09-17 at 18.39.26 (1).jpeg` | Repeated Gateway Rule Engine ticket; explicitly excluded from the foundation. |
| 23 | `WhatsApp Image 2026-09-17 at 18.39.26.jpeg` | Repeated Transport & Dispatch ticket; explicitly excluded from the final foundation scope. |

Overlapping photographs were reviewed as repeated evidence. The three partial rule screenshots do not form a reliable complete polygon or rule export. No attachment supplies a full Algo input/output contract.

## Repository cross-check

These observations refer to the existing gateway baseline; they are not features newly added to the unified gateway.

| Existing behavior | Repository source |
|---|---|
| Elasticsearch PIT pagination and integrity checks | `apps/gateway/Processing/Rules/ElasticsearchRuleRepository.cs` |
| Initial load, immutable rule cache, refresh and failed-refresh retention | `apps/gateway/Processing/Rules/ActiveRuleCache.cs` |
| Linear rule scan, sensor-specific quality matching, resolution/age checks and geometry intersection | `apps/gateway/Processing/Rules/RuleMatcher.cs` |
| Per-rule/per-tenant output construction, tiling arrays and intersection ROI | `apps/gateway/Processing/Messages/GatewayOutputMessageBuilder.cs` |
| Existing supported algorithm values | `libs/common-dtos/Rules/Models/AlgorithmName.cs` |
| Existing `/rules` API and persistence | `apps/rules-api/Controllers/RulesController.cs`, `apps/rules-api/Services/RuleService.cs` |
| Existing publication, acknowledgement and retry routing | `libs/rabbitmq-client/RabbitMqPublisher.cs`, `libs/rabbitmq-client/RabbitMqOutcomeRouter.cs` |

The new foundation adds `libs/pipeline-catalog`, `libs/pipeline-contracts` and the separate `apps/unified-gateway` host. It includes registered-contract selection, disabled-pipeline handling, passive output descriptors, named broker descriptors, literal rule-source resolution, JSON-preserving `ExtraData`, ASD body/attribute preparation and metadata GET routes. It has shared observability and unit/host tests. It does not add live HTTP/RabbitMQ transport components, queues, channel pools, dispatch coordination, retry workers, a rule cache or Rules API changes.

## Inputs needed for later work

Transport implementation still needs actual destination contracts, authentication, acceptance/idempotency behavior, source identity and representative input/output fixtures. The preserved external retry decision is to resend only saved failed outgoing units without rematching; this foundation does not implement that worker. Rule-engine and migration work still need complete rulesets, sensor/age/geometry semantics and verified per-deployment source-to-target index mappings. These inputs do not block the local catalog/contracts foundation.
