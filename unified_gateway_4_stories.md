# Unified Gateway – 4 Jira Stories

## 1) Catalog & Contracts Foundation

Title: Catalog & Contracts Foundation

Description:
Today, FindAir and Martin each have their own gateway-specific logic, rule shape, and dispatch behavior. This story creates the shared foundation for a unified gateway by introducing a single pipeline catalog and a pipeline contract model.

The catalog will define, per pipeline:
- whether it is enabled
- which rule index it reads from
- which transport it uses
- the pipeline metadata needed for runtime and admin operations

The contract model will define:
- the shape of the pipeline-specific run params
- how the gateway writes the downstream payload
- which headers and attributes are required for dispatch
- how validation happens at both authoring time and runtime

This is the foundation for removing duplicated gateway logic and making pipeline-specific behavior explicit and validated rather than spread across multiple services.

Acceptance criteria:
- Shared catalog model exists and is used by the gateway and rules API
- Each pipeline has a registered contract
- Invalid or unregistered pipeline config fails startup
- Disabled pipelines are allowed in config but not dispatched
- Pipeline config is separated from pipeline runtime behavior

---

## 2) Gateway Rule Engine

Title: Gateway Rule Engine

Description:
FindAir and Martin both evaluate incoming images against rules and decide which downstream pipeline(s) should receive work. Today this logic is duplicated and shaped by each system separately.

This story extracts the shared rule-evaluation behavior into a single reusable engine used by the unified gateway. The engine will:
- load and validate rule documents
- compile rule predicates into runtime-friendly form
- evaluate conditions like sensor, resolution, age, and geometry
- produce match results with no live ES query in the hot path
- keep a refreshable in-memory snapshot per enabled pipeline

The engine must support both the current pipeline behavior and future extension without making every gateway custom.

Acceptance criteria:
- Shared rule engine exists in a reusable library
- Rule matching is done from an in-memory snapshot, not from a live ES query per message
- Matching logic is reusable across pipelines
- Sensor + quality pairing is preserved correctly
- Empty or invalid rule conditions are rejected

---

## 3) Transport & Dispatch

Title: Transport & Dispatch

Description:
FindAir and Martin do not necessarily use the same transport model. Today the gateway path is tightly coupled to a single dispatch model and cannot handle multiple channel types cleanly.

This story introduces per-pipeline dispatch transport support so that the gateway can send work through different destinations depending on the pipeline:
- RabbitMQ for queue-based pipelines
- HTTP forwarding for HTTP-based pipelines
- a common dispatch abstraction for retry, dead-letter, and delivery semantics

This work makes the gateway transport-agnostic while keeping pipeline-specific payload and headers defined in the pipeline contract.

Acceptance criteria:
- Gateway dispatches through a transport abstraction
- RabbitMQ and HTTP patterns are both supported
- Pipeline transport is configured per pipeline
- Retry, DLQ, and idempotency handling are part of the dispatch design
- The gateway hot path remains generic and does not know pipeline-specific transport logic

---

## 4) Rules API v2

Title: Rules API v2

Description:
The current rules API is tied to current pipeline assumptions and does not model a shared multi-pipeline system cleanly. The unified gateway requires a versioned rules API that understands pipeline-scoped rules and shared validation.

This story introduces Rules API v2 with:
- pipeline-scoped routes such as /pipelines/{pipelineId}/rules
- shared contract-driven validation
- support for a per-pipeline rule document structure
- a common rule model used by the gateway and the API
- route resolution by pipeline ID to the correct rule index

This is the API layer that lets product or engineering author rules for each pipeline without coupling the API to a single pipeline’s behavior.

Acceptance criteria:
- Rules API supports pipeline-scoped endpoints
- Rules are validated against the pipeline contract
- Rules API and gateway share the same rule contract model
- Invalid rule writes are rejected before storage
- Rules can be written for a pipeline without affecting unrelated pipelines
