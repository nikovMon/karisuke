# Unified Gateway Kibana dashboard

The dashboard combines unified gateway logs with Elastic APM traces. It covers source handling, FindAir and DeepWisdom dispatch, validation, retries, filtering, rule health, sampled latency, and investigations by image or trace ID.

The generated bundle targets Kibana 8.15, assuming it matches the verified Elasticsearch 8.15.0 deployment. Its saved-object schemas come from compatible Elastic Kibana 8.4.1 fixtures, which Kibana 8.15 migrates on import. The supplied files have not been imported into or rendered in the live Kibana instance. Local validation cannot confirm live data mappings, permissions, or collector transformations.

## Import

1. Open the intended Kibana Space. The supplied trace links use the `default` Space; regenerate for another Space before import.
2. Open **Stack Management > Saved Objects > Import**.
3. Select [unified-gateway-dashboard.ndjson](unified-gateway-dashboard.ndjson). Import the complete file so its dashboard, visualizations, searches, and dedicated data view stay together.
4. If replacing an earlier import of this bundle, allow overwrite for its objects. Their IDs are stable. A differently configured build uses the same IDs and replaces the same dashboard in that Space; it does not create another independent dashboard.
5. Open **Dashboards > Unified Gateway | Operations, Logs & Traces**.
6. Select the environment and start with **Last 1 hour**. Refresh is initially paused. Generate some gateway activity and refresh manually.
7. Check a source event, a trace latency panel, and a trace-ID link. Empty Debug panels require the logging change below; an empty trace panel requires checking raw APM data.

Import requires the appropriate Saved Objects Management privilege. Kibana migrates supported older saved objects during import, but imports from newer versions into older Kibana versions are unsupported. See [Elastic's saved-object import documentation](https://www.elastic.co/guide/en/kibana/8.15/managing-saved-objects.html).

The bundle creates its own data view. It does not change the existing default data view, Elasticsearch mappings, retention, Logstash configuration, or any application service.

## Data and deployment settings

| Setting | Default | Meaning |
| --- | --- | --- |
| Service | `unified-gateway` | Exact deployed `service.name`, including any `OTEL_SERVICE_NAME` override |
| Logs | `logs-unified_gateway-*` | Development, integration, and production gateway streams accessible from this cluster |
| Traces | `traces-apm*,apm-*` | Raw APM spans and transactions in current or legacy APM indices |
| Time | `@timestamp` | Shared dashboard time filter |
| Space | `default` | Space used in generated APM trace links |
| Base path | Empty | Optional Kibana reverse-proxy path such as `/kibana` |

The trace panels select `processor.event: span` or `transaction`. They do not use APM metric rollups or `logs-apm*` error documents as spans. A pre-existing APM data view is not a dependency. Multiple index patterns can share one Kibana data view; see [Elastic's data-view documentation](https://www.elastic.co/guide/en/kibana/8.15/data-views.html).

Log export working does not establish that OTLP trace export is working. The trace panels require raw gateway traces in Elasticsearch, stored in Elastic APM's document format. Collector deployments that store arbitrary OTLP JSON or use other field names need their normalization adapted in the generator.

## Enable detailed gateway log panels

Delivery events (`6001`), periodic rule snapshots (`6011`), and filter decisions (`6022`) are emitted at Debug level. Add this setting to the gateway Deployment to export those categories through the ECS HTTP provider:

```yaml
- name: Logging__EcsHttp__LogLevel__ImagingPipeline.UnifiedGateway
  value: Debug
```

Alternatively, merge this into the application's existing logging configuration:

```json
{
  "Logging": {
    "EcsHttp": {
      "LogLevel": {
        "ImagingPipeline.UnifiedGateway": "Debug"
      }
    }
  }
}
```

Roll out the gateway after changing Deployment configuration. Keep Logstash export enabled. This category override enables gateway details without enabling every framework category. A global console Debug setting alone does not override a provider-specific Information setting.

Historical Debug events that were never exported will not appear after this change. Information-level source summaries still provide delivered/failed dispatch totals before detailed Debug export is enabled.

## Interpret the panels

- Source and dispatch totals count **observed attempts**. A source retry can resend dispatch units that previously succeeded. Delivered means the transport accepted work; it does not mean the downstream algorithm completed.
- A completed source attempt can have zero dispatch units when no rules match. Early missing-gridType filters and invalid source messages are separate events.
- Filter logs cover source, pipeline, and run decisions. Their combined count is not a count of discarded source messages.
- Dead-letter events record successful broker rejection without requeue. They neither measure DLQ depth nor establish that the DLQ stored a message.
- Rule-cache counts are the latest logged snapshot per pipeline and instance within the selected time range. They are not summed across refreshes or pods. A failed refresh keeps previous rules; a short range may miss the last successful snapshot.
- Rule rejection detail logs are capped at ten per load. Rule matching ID lists may also be truncated. Numeric matching-miss counts are the per-evaluation totals and represent each rule's first failed condition.
- Trace counts and latency percentiles describe exported, sampled attempts. Nested span durations overlap and must not be summed. Handler latency excludes time waiting in the broker queue.
- Logs can be filtered or dropped, and traces can be sampled or expired. An empty panel means no matching telemetry, which is not proof of zero activity. Trace IDs can exist in logs even when their traces were not retained.

## Investigate an image or trace

Use the dashboard's KQL bar, for example:

```text
ug.image: "<image-id>"
```

```text
trace.id: "<trace-id>"
```

```text
ug.pipeline: "FindAir"
```

**Clear a pipeline filter when investigating the whole source message.** Source summaries, handler traces, retries, and dead-letter events are source-wide and normally have no pipeline ID. They disappear under a global pipeline filter even while a pipeline's dispatch logs remain visible.

An image filter also hides dispatch and preparation spans because those child spans do not inherit the handler's image tags. Find the image in logs or handler traces first, then **replace** the `ug.image` filter with `trace.id`, or open the APM trace link, to inspect the complete trace.

Click a trace ID in an investigation table to open APM. The target Space must have APM available, the trace must be retained, and the viewer must have access. Application log environment values come from `data_stream.namespace`; trace environments come from the deployed service resource. Normalization lowercases them, so `Integration` and `integration` can be selected together.

## Regenerate

Use Python 3 with its standard library from the repository root:

```text
python deploy/unified-gateway/observability/kibana/build_dashboard.py
```

Example with deployment-specific values:

```text
python deploy/unified-gateway/observability/kibana/build_dashboard.py --service unified-gateway --logs "logs-unified_gateway-*" --traces "traces-apm*,apm-*" --space observability --base-path /kibana
```

`--space` is the Space ID from its URL, not its display name. `--base-path` excludes `/s/<space-id>`; the generator adds that itself. Use `--output <file.ndjson>` to select another output location. The adjacent manifest records the configuration and panel queries used to generate that bundle.

Validate a generated bundle with:

```text
python deploy/unified-gateway/observability/kibana/validate_dashboard.py
```

The optional positional argument selects another NDJSON file; `--manifest` selects its manifest explicitly. `--target-version` defaults to `8.4.1`, the oldest fixture schema compatibility target, even though this deployment targets Kibana 8.15. The validator checks nested JSON, references, migration metadata, panel layout, field definitions, aggregation wiring, service/signal scoping, and manifest agreement. It does not execute KQL or Painless or test live Kibana importing and rendering.

The `ug.*` data-view runtime fields read `_source` to normalize custom fields across log and APM documents and avoid dependence on dynamic `.keyword` mappings. They do not rewrite indexed documents. Runtime aggregations cost more than indexed-field aggregations: start with one environment and a short time range, and avoid aggressive refresh over all historical traces. `_source` and runtime-field queries must be available in the deployment. For sustained high-volume use, validate live mappings and replace expensive normalization with indexed fields.

## Files

| File | Purpose |
| --- | --- |
| `unified-gateway-dashboard.ndjson` | Complete Kibana import bundle |
| `unified-gateway-dashboard.manifest.json` | Generated settings, object IDs, and panel queries |
| `build_dashboard.py` | Reproducible generator with configurable service, indices, Space, and base path |
| `validate_dashboard.py` | Offline saved-object structure and telemetry-query consistency checks |
| `schema-reference.json` | Saved-object examples sourced from Elastic's versioned Kibana fixtures |
| `log-panel-spec.json` | Log event semantics and panel candidates checked against the gateway implementation |
| `trace-panel-spec.json` | Trace semantics, fields, and interpretation constraints |

The panel specifications describe candidates and constraints; the generated manifest is the authority for which panels are included in the import.
