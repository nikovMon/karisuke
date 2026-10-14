#!/usr/bin/env python3
"""Build an importable Kibana 8.15 dashboard from audited gateway telemetry.

Only creates saved-object artifacts. Does not contact a cluster or change services.
Classic visualization schemas come from the pinned Elastic fixture in
schema-reference.json. Run --help for deployment-specific settings.
"""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
from urllib.parse import quote
import uuid

HERE = Path(__file__).resolve().parent
ID_NAMESPACE = uuid.UUID("f05cc998-13e6-42c9-a3cb-96b61a5d291f")
INDEX_REF = "kibanaSavedObjectMeta.searchSourceJSON.index"


def dumps(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def object_id(key):
    return str(uuid.uuid5(ID_NAMESPACE, key))


# Reading _source makes custom fields independent of dynamic keyword/text mappings.
# A data-view runtime field does not alter documents or Elasticsearch templates.
GET_VALUE = """def read(def root, String path) {
  if (!(root instanceof Map)) return null;
  if (root.containsKey(path)) return root[path];
  def current = root;
  for (def part : path.splitOnToken('.')) {
    if (!(current instanceof Map)) return null;
    current = current[part];
    if (current == null) return null;
  }
  return current;
}
"""


def runtime_fields(service="unified-gateway"):
    result = {}

    def add(name, kind, body):
        guard = "if (read(params._source, 'service.name') != " + dumps(service) + ") return;\n"
        result["ug." + name] = {"type": kind, "script": {"source": GET_VALUE + guard + body}}

    def alias(name, candidates, kind="keyword", lower=False):
        emit = "emit(v.toString().toLowerCase());" if lower else "emit(v.toString());"
        if kind == "long":
            emit = "if (v instanceof Number) emit(v.longValue()); else emit(Long.parseLong(v.toString()));"
        add(name, kind, "for (String path : " + dumps(candidates) + ") {\n"
            "  def v = read(params._source, path);\n"
            "  if (v != null && !(v instanceof Map) && !(v instanceof List)) { " + emit + " return; }\n}"
        )

    add("environment", "keyword", """def isLog = read(params._source, 'log.level') != null;
def env = isLog ? read(params._source, 'data_stream.namespace') : read(params._source, 'service.environment');
if (env == null) env = read(params._source, 'service.environment');
if (env == null) env = read(params._source, 'deployment.environment');
if (env == null) env = read(params._source, 'data_stream.namespace');
if (env != null) emit(env.toString().toLowerCase());
""")
    alias("pipeline", ["pipeline.id", "labels.pipeline_id"])
    alias("transport", ["pipeline.transport", "labels.pipeline_transport"])
    alias("instance", ["service.instance.id", "service.node.name"])
    alias("pod", ["kubernetes.pod.name", "labels.kubernetes_pod_name", "labels.k8s_pod_name"])
    alias("event_code", ["event.code"])
    alias("log_level", ["log.level"], lower=True)
    alias("image", ["findair.image.id", "labels.findair_image_id"])
    alias("sensor", ["findair.sensor.name", "labels.findair_sensor_name"])
    alias("area", ["findair.area.name", "labels.findair_area_name"])
    alias("message_id", ["messaging.message.id", "labels.messaging_message_id"])
    alias("error_category", ["findair.error.category", "labels.findair_error_category", "labels.error_type", "error.type"])
    alias("validation_code", ["error.code"])
    alias("filter_reason", ["unified_gateway.filter.reason"])
    alias("rule_rejection_reason", ["unified_gateway.rule.rejection.reason"])
    alias("rule_index", ["labels.db_collection_name", "db.instance"])
    alias("http_status", ["http.response.status_code", "numeric_labels.http_response_status_code",
                          "labels.http_response_status_code"], "long")
    alias("retry_attempt", ["findair.retry.attempt", "numeric_labels.findair_retry_attempt", "labels.findair_retry_attempt"], "long")
    for name, source in {
        "planned_units": "unified_gateway.dispatch.units.count",
        "failed_units": "unified_gateway.dispatch.failed.count",
        "rules_loaded": "unified_gateway.rules.loaded.count",
        "rules_rejected": "unified_gateway.rules.rejected.count",
        "rules_retained": "unified_gateway.rules.retained.count",
        "missed_photo_age": "unified_gateway.rules.missed.photo_age.count",
        "missed_sensor": "unified_gateway.rules.missed.sensor.count",
        "missed_resolution": "unified_gateway.rules.missed.resolution.count",
        "missed_geometry": "unified_gateway.rules.missed.geometry.count",
    }.items():
        alias(name, [source], "long")

    add("signal", "keyword", """def p = read(params._source, 'processor.event');
if (p == 'span' || p == 'transaction') { emit('traces'); return; }
if (read(params._source, 'log.level') != null) emit('logs');
""")
    add("operation", "keyword", """def p = read(params._source, 'processor.event');
def name = p == 'span' ? read(params._source, 'span.name') :
  (p == 'transaction' ? read(params._source, 'transaction.name') : null);
if (name != null) emit(name.toString());
""")
    add("duration_ms", "double", """def p = read(params._source, 'processor.event');
def duration = p == 'span' ? read(params._source, 'span.duration.us') :
  (p == 'transaction' ? read(params._source, 'transaction.duration.us') : null);
if (duration instanceof Number) { emit(duration.doubleValue() / 1000.0); return; }
if (p == 'span' || p == 'transaction') {
  def nanos = read(params._source, 'event.duration');
  if (nanos instanceof Number) emit(nanos.doubleValue() / 1000000.0);
}
""")
    add("trace_failure", "double", """def outcome = read(params._source, 'event.outcome');
if (outcome == 'failure') emit(1.0);
else if (outcome == 'success') emit(0.0);
""")
    add("outcome", "keyword", """def code = read(params._source, 'event.code');
if (code != null && code.toString() == '6001') { emit('success'); return; }
if (code != null && code.toString() == '6020') { emit('rejected'); return; }
for (String path : ['findair.outcome', 'labels.findair_outcome', 'pipeline.outcome', 'labels.pipeline_outcome', 'event.outcome']) {
  def v = read(params._source, path);
  if (v != null) { emit(v.toString()); return; }
}
""")
    add("delivered_units", "long", """def planned = read(params._source, 'unified_gateway.dispatch.units.count');
def failed = read(params._source, 'unified_gateway.dispatch.failed.count');
if (planned instanceof Number && failed instanceof Number) {
  emit(Math.max(0L, planned.longValue() - failed.longValue()));
}
""")
    return result


class Builder:
    def __init__(self, args):
        self.args = args
        self.templates = json.loads((HERE / "schema-reference.json").read_text(encoding="utf-8-sig"))["templates"]
        self.objects = []
        self.panels = []
        self.catalog = []
        self.y = 0
        self.view_id = object_id("data-view")
        self.dashboard_id = object_id("dashboard")
        self.base_query = "service.name: " + dumps(args.service)
        self.add_view()

    def add_view(self):
        runtime = runtime_fields(self.args.service)
        fields = []
        physical = {
            "@timestamp": ("date", "date"), "service.name": ("string", "keyword"),
            "trace.id": ("string", "keyword"), "span.id": ("string", "keyword"),
            "transaction.id": ("string", "keyword"), "processor.event": ("string", "keyword"),
            "event.outcome": ("string", "keyword"), "log.logger": ("string", "keyword"),
            "message": ("string", "match_only_text"), "error.message": ("string", "match_only_text"),
            "error.type": ("string", "keyword"), "findair.rule.id": ("string", "keyword"),
            "messaging.message.conversation_id": ("string", "keyword"),
        }
        for name, (kind, es_type) in physical.items():
            fields.append({"name": name, "type": kind, "esTypes": [es_type], "count": 0,
                           "scripted": False, "searchable": True, "aggregatable": es_type != "match_only_text",
                           "readFromDocValues": es_type != "match_only_text"})
        for name, spec in runtime.items():
            kind = "string" if spec["type"] == "keyword" else "number"
            fields.append({"name": name, "type": kind, "esTypes": [spec["type"]], "count": 0,
                           "scripted": False, "searchable": True, "aggregatable": True,
                           "readFromDocValues": True, "runtimeField": spec})
        base = self.args.base_path.rstrip("/")
        if self.args.space != "default":
            base += "/s/" + quote(self.args.space, safe="")
        formats = {
            "ug.duration_ms": {"id": "number", "params": {"pattern": "0,0.[00]"}},
            "ug.trace_failure": {"id": "percent", "params": {"pattern": "0,0.[0]%"}},
            "trace.id": {"id": "url", "params": {
                "urlTemplate": base + "/app/apm/link-to/trace/{{value}}",
                "labelTemplate": "{{value}}", "openLinkInCurrentTab": False,
            }},
        }
        self.objects.append({
            "id": self.view_id, "type": "index-pattern", "references": [],
            "coreMigrationVersion": "8.0.1", "migrationVersion": {"index-pattern": "7.11.0"},
            "attributes": {"title": self.args.logs + "," + self.args.traces,
                           "name": "Unified Gateway — logs and traces", "timeFieldName": "@timestamp",
                           "allowNoIndex": True, "fields": dumps(fields),
                           "runtimeFieldMap": dumps(runtime), "fieldFormatMap": dumps(formats)},
        })

    def template(self, key, slug, title, description=""):
        result = copy.deepcopy(self.templates[key])
        result["id"] = object_id(slug)
        result["attributes"]["title"] = title
        result["attributes"]["description"] = description
        return result

    def source(self, query):
        scoped_query = self.base_query if not query else f"({self.base_query}) and ({query})"
        return {"indexRefName": INDEX_REF, "query": {"language": "kuery", "query": scoped_query}, "filter": []}

    def add(self, obj, query, x, width, height, description="", debug=False):
        index = str(len(self.panels) + 1)
        ref_name = "panel_" + index
        self.objects.append(obj)
        self.panels.append({"type": obj["type"], "panelIndex": index, "panelRefName": ref_name,
                            "gridData": {"x": x, "y": self.y, "w": width, "h": height, "i": index},
                            "embeddableConfig": {"enhancements": {}}})
        self.catalog.append({"id": obj["id"], "type": obj["type"], "title": obj["attributes"]["title"],
                             "query": query, "description": description, "requiresGatewayDebug": debug,
                             "grid": self.panels[-1]["gridData"]})
        return obj

    def row(self, height):
        self.y += height

    def markdown(self, slug, text, height=4):
        title = text.splitlines()[0].lstrip("# ")
        obj = self.template("markdown", slug, title)
        obj["attributes"]["visState"].update(title=title, params={"fontSize": 14, "openLinksInNewTab": True, "markdown": text})
        obj["references"] = []
        self.add(obj, "", 0, 48, height)
        self.row(height)

    def controls(self):
        obj = self.template("input_control_vis", "controls", "Environment and instance")
        controls = []
        refs = []
        for i, (field, label) in enumerate([("ug.environment", "Environment"), ("ug.instance", "Gateway instance")]):
            ref = f"control_{i}_index_pattern"
            controls.append({"id": f"ug-control-{i}", "fieldName": field, "parent": "", "label": label,
                             "type": "list", "options": {"type": "terms", "multiselect": True, "size": 100,
                                                          "order": "desc", "dynamicOptions": True},
                             "indexPatternRefName": ref})
            refs.append({"type": "index-pattern", "id": self.view_id, "name": ref})
        obj["attributes"]["visState"].update(title="Environment and instance", params={
            "controls": controls, "updateFiltersOnChange": True, "useTimeFilter": True, "pinFilters": False})
        obj["references"] = refs
        self.add(obj, "", 0, 48, 5)
        self.row(5)

    def vis(self, slug, title, kind, query, metrics, buckets=(), *, x=0, w=24, h=10, description="", debug=False, stacked=False):
        template_kind = "area" if kind == "line" else kind
        obj = self.template(template_kind, slug, title, description)
        a = obj["attributes"]
        state = a["visState"]
        state["title"] = title
        state["type"] = kind
        state["aggs"] = []
        for i, (agg_type, params) in enumerate(metrics, 1):
            state["aggs"].append({"id": str(i), "enabled": True, "type": agg_type, "schema": "metric", "params": params})
        for i, (agg_type, schema, params) in enumerate(buckets, len(metrics) + 1):
            state["aggs"].append({"id": str(i), "enabled": True, "type": agg_type, "schema": schema, "params": params})
        if kind in ("line", "area", "horizontal_bar"):
            draw_type = "histogram" if kind == "horizontal_bar" else kind
            state["params"]["type"] = draw_type
            state["params"]["seriesParams"] = [
                {"show": True, "type": draw_type, "mode": "stacked" if stacked else "normal",
                 "data": {"id": str(i), "label": p.get("customLabel", t)}, "valueAxis": "ValueAxis-1",
                 "drawLinesBetweenPoints": True, "showCircles": False, "interpolate": "linear"}
                for i, (t, p) in enumerate(metrics, 1)]
            state["params"]["valueAxes"][0]["title"] = {}
            state["params"]["valueAxes"][0]["labels"]["rotate"] = 0
            if kind == "line":
                state["params"]["fittingFunction"] = "none"
            state["params"]["legendPosition"] = "bottom"
        if kind == "metric":
            state["params"]["metric"]["style"]["fontSize"] = 38
        if kind == "table":
            state["params"]["perPage"] = 10
            state["params"]["showTotal"] = False
        a["uiStateJSON"] = {"vis": {"colors": {
            "success": "#54B399", "retry": "#D6BF57", "rejected": "#E7664C", "failure": "#BD271E",
            "prepared": "#54B399", "invalid": "#E7664C", "failed": "#BD271E",
            "information": "#6092C0", "warning": "#D6BF57", "error": "#BD271E", "debug": "#9170B8",
        }}}
        a["kibanaSavedObjectMeta"]["searchSourceJSON"] = self.source(query)
        obj["references"] = [{"type": "index-pattern", "id": self.view_id, "name": INDEX_REF}]
        return self.add(obj, query, x, w, h, description, debug)

    def search(self, slug, title, query, columns, *, sort="@timestamp", h=12, description=""):
        obj = self.template("search", slug, title, description)
        obj["attributes"]["columns"] = columns
        obj["attributes"]["sort"] = [[sort, "desc"]]
        obj["attributes"]["kibanaSavedObjectMeta"]["searchSourceJSON"] = self.source(query)
        obj["references"] = [{"type": "index-pattern", "id": self.view_id, "name": INDEX_REF}]
        self.add(obj, query, 0, 48, h, description)
        self.row(h)

    def finish(self):
        obj = self.template("dashboard", "dashboard", "Unified Gateway | Operations, Logs & Traces",
                            "FindAir and DeepWisdom operations, source attempts, rule health, sampled trace latency and correlated investigations.")
        a = obj["attributes"]
        a["panelsJSON"] = self.panels
        a["optionsJSON"] = {"useMargins": True, "hidePanelTitles": False, "syncColors": True,
                            "syncCursor": True, "syncTooltips": False}
        a["kibanaSavedObjectMeta"]["searchSourceJSON"] = {"query": {"language": "kuery", "query": self.base_query}, "filter": []}
        a["timeRestore"] = True
        a["timeFrom"] = "now-1h"
        a["timeTo"] = "now"
        a["refreshInterval"] = {"pause": True, "value": 60000}
        obj["references"] = [
            {"type": spec["type"], "id": spec["id"], "name": f"{p['panelIndex']}:{p['panelRefName']}"}
            for p, spec in zip(self.panels, self.catalog)]
        self.objects.append(obj)
        return self.objects


def metric(kind="count", field=None, label=None, **extra):
    p = dict(extra)
    if field:
        p["field"] = field
    if label:
        p["customLabel"] = label
    return kind, p


def terms(field, schema="segment", size=10, order_by="_count", label=None):
    params = {"field": field, "size": size, "order": "desc", "orderBy": order_by,
              "otherBucket": False, "missingBucket": False}
    if label:
        params["customLabel"] = label
    return "terms", schema, params


def timeline():
    return "date_histogram", "segment", {"field": "@timestamp", "interval": "auto", "min_doc_count": 0, "extended_bounds": {}}


def filters(items, schema="group"):
    return "filters", schema, {"filters": [{"label": label, "input": {"language": "kuery", "query": q}} for label, q in items]}


def build(args):
    b = Builder(args)
    logs = 'ug.signal: "logs"'
    traces = 'ug.signal: "traces"'
    event = lambda *codes: logs + " and ug.event_code: (" + " or ".join(dumps(str(c)) for c in codes) + ")"
    operation = lambda name: traces + " and ug.operation: " + dumps(name)
    handler = operation("rabbitmq handler")
    dispatch = operation("unified_gateway.dispatch")
    prepare = operation("unified_gateway.contract.prepare")
    router = logs + ' and log.logger: "ImagingPipeline.RabbitMqClient.RabbitMqOutcomeRouter"'

    b.markdown("start", "# Unified Gateway\n**Operations · FindAir · DeepWisdom**\n\nLogs describe business events. Traces explain sampled processing and transport latency. Select an environment below and use the time picker. Click a trace ID in an investigation table to open APM. **All panels are scoped to this service.**", 6)
    b.controls()
    b.markdown("source-overview", "## Source processing and delivery\nCounts are observed handling/dispatch **attempts**; retries can repeat work. Delivered means transport acceptance, not downstream algorithm completion.", 4)
    for x, slug, title, query, m in [
        (0, "source-count", "Completed source attempts", event(6021), metric()),
        (12, "source-unique", "Distinct processed images ≈", event(6021), metric("cardinality", "ug.image")),
        (24, "source-invalid", "Invalid source inputs", event(6020), metric()),
        (36, "dispatch-failed", "Failed dispatch attempts", event(6002), metric()),
    ]:
        b.vis(slug, title, "metric", query, [m], x=x, w=12, h=6)
    b.row(6)
    b.vis("source-throughput", "Source attempt outcomes over time", "area", event(6021), [metric()], [timeline(), terms("ug.outcome", "group", 5)], stacked=True,
          description="One event6021 per completed source attempt; excludes early filters and parse rejections.")
    b.vis("source-outcomes", "Source attempt outcome mix", "pie", event(6021), [metric()], [terms("ug.outcome", size=5)], x=24)
    b.row(10)
    b.vis("unit-throughput", "Dispatch units reported by source attempts", "line", event(6021),
          [metric("sum", "ug.delivered_units", "Delivered"), metric("sum", "ug.failed_units", "Failed or invalid")], [timeline()],
          description="Derived from source summary; available at Information level. Retries count again.")
    b.vis("source-fanout", "Fan-out per source attempt", "line", event(6021),
          [metric("avg", "ug.planned_units", "Average units"), metric("max", "ug.planned_units", "Maximum units")], [timeline()], x=24)
    b.row(10)
    b.vis("dispatch-breakdown", "Dispatch attempts by pipeline [Debug for success]", "table", event(6001, 6002), [metric()],
          [terms("ug.pipeline", "bucket"), terms("ug.transport", "bucket", 3), terms("ug.outcome", "bucket", 5)], debug=True,
          description="Delivered dispatch event6001 needs gateway Debug export. Failed6002 does not.")
    b.vis("dispatch-errors", "Dispatch failures by pipeline and category", "table", event(6002), [metric()],
          [terms("ug.pipeline", "bucket"), terms("ug.error_category", "bucket"), terms("ug.outcome", "bucket", 5)], x=24)
    b.row(10)
    b.vis("filtered-reasons", "Filter decisions [Debug]", "horizontal_bar", event(6022), [metric()], [terms("ug.filter_reason")], debug=True,
          description="Mixed source/pipeline/run decisions; not a unique dropped-message count.")
    b.vis("validation-reasons", "Invalid source inputs by validation code", "horizontal_bar", event(6020), [metric()], [terms("ug.validation_code")], x=24)
    b.row(10)
    b.vis("retry-routing", "Retry and dead-letter routing events", "area", router + ' and ug.event_code: ("105" or "106")', [metric()],
          [timeline(), filters([("Retry routed", 'ug.event_code: "105"'), ("Dead-letter nack", 'ug.event_code: "106"')])], stacked=True,
          description="Successful routing operations from the outcome router. Not queue depth or confirmation that a DLQ stored the message.")
    b.vis("http-dispatch-failures", "Failed HTTP dispatches by response code", "horizontal_bar", event(6002) + ' and ug.transport: "http"',
          [metric()], [terms("ug.http_status")], x=24, description="Timeouts and connection failures without a response code are absent from this chart.")
    b.row(10)
    b.vis("source-sensors", "Processed attempts by sensor", "horizontal_bar", event(6021), [metric()], [terms("ug.sensor")])
    b.vis("log-volume", "Exported application logs by severity", "area", logs, [metric()], [timeline(), terms("ug.log_level", "group", 6)], x=24, stacked=True)
    b.row(10)
    b.vis("rabbitmq-disruptions", "RabbitMQ connection and consumer events", "area",
          logs + ' and log.logger: ImagingPipeline.RabbitMqClient.* and ug.event_code: ("109" or "110" or "111" or "112" or "113" or "114" or "115")',
          [metric()], [timeline(), filters([
              ("Connection attempt failed", 'ug.event_code: "109"'),
              ("Connection shutdown", 'ug.event_code: "110"'),
              ("Callback exception", 'ug.event_code: "111"'),
              ("Recovery succeeded", 'ug.event_code: "112"'),
              ("Recovery failed", 'ug.event_code: "113"'),
              ("Disposal timed out", 'ug.event_code: "114"'),
              ("Consumer restarting", 'ug.event_code: "115"'),
          ])], w=48, stacked=True,
          description="Exported connection lifecycle events, including shutdown during rollout; not an availability percentage or outage duration.")
    b.row(10)

    b.markdown("rule-health", "## Rule health\nMatching misses are the **first failed condition** per rule. Cache counts below are the latest observed snapshot per pipeline and instance in the selected time range; they are never summed across refreshes.", 4)
    b.vis("rule-misses", "Rule-matching misses by pipeline", "horizontal_bar", event(7002),
          [metric("sum", "ug.missed_photo_age", "Photo age"), metric("sum", "ug.missed_sensor", "Sensor"),
           metric("sum", "ug.missed_resolution", "Resolution"), metric("sum", "ug.missed_geometry", "Geometry")],
          [terms("ug.pipeline")], description="Exact numeric miss counts over exported per-image/per-pipeline evaluation events.")
    b.vis("rule-load-failures", "Rule load failures over time", "area", event(6013), [metric()], [timeline(), terms("ug.pipeline", "group")], x=24, stacked=True)
    b.row(10)
    b.vis("rule-snapshots", "Latest rule cache snapshots [Debug for refresh]", "table", event(6010, 6011),
          [metric("top_hits", "ug.rules_loaded", "Latest loaded", aggregate="average", size=1, sortField="@timestamp", sortOrder="desc"),
           metric("top_hits", "ug.rules_rejected", "Latest rejected", aggregate="average", size=1, sortField="@timestamp", sortOrder="desc"),
           metric("max", "@timestamp", "Last snapshot")],
          [terms("ug.pipeline", "bucket", 20), terms("ug.instance", "bucket", 30)], w=48, h=12, debug=True,
          description="Startup snapshots are Information; periodic snapshots require Debug. A short time range can be empty if only startup snapshots were exported.")
    b.row(12)
    b.vis("rejected-rules", "Rejected-rule detail events by reason", "table", event(6012), [metric()],
          [terms("ug.pipeline", "bucket"), terms("ug.rule_rejection_reason", "bucket", 10)],
          description="Diagnostic details are capped at10 per load; repeated refreshes can repeat a rejection. This is not the total rejected-rule count.")
    b.vis("contract-invalid", "Contract validation and preparation errors", "table", logs + ' and log.logger: "ImagingPipeline.UnifiedGateway.Processing.PipelineWorkPreparer" and ug.log_level: ("warning" or "error" or "critical")',
          [metric()], [terms("ug.pipeline", "bucket"), terms("ug.log_level", "bucket", 5)], x=24)
    b.row(10)

    b.markdown("trace-performance", "## Trace performance\nThese are **sampled attempts**, not total throughput. `rabbitmq handler` measures handling plus ACK/retry/dead-letter completion and excludes broker queue residence. Nested span durations are inclusive; do not sum them.", 4)
    b.vis("trace-handler-count", "Sampled source delivery attempts", "metric", handler, [metric()], w=12, h=6)
    b.vis("trace-handler-failure", "Sampled handler failure share", "metric", handler, [metric("avg", "ug.trace_failure")], x=12, w=12, h=6,
          description="Failures include retries/rejections/cancellation; denominator is known success/failure outcomes, not final lost messages.")
    b.vis("trace-handler-latency-kpi", "Handler latency — milliseconds", "metric", handler,
          [metric("percentiles", "ug.duration_ms", percents=[50, 95, 99])], x=24, w=24, h=6)
    b.row(6)
    b.vis("trace-handler-volume", "Sampled delivery attempts by result", "area", handler, [metric()], [timeline(), terms("ug.outcome", "group", 6)], stacked=True)
    b.vis("trace-handler-latency", "Handler latency percentiles — ms", "line", handler,
          [metric("percentiles", "ug.duration_ms", percents=[50, 95, 99])], [timeline()], x=24)
    b.row(10)
    b.vis("trace-dispatch-latency", "Dispatch p95 latency by pipeline — ms", "line", dispatch,
          [metric("percentiles", "ug.duration_ms", percents=[95])], [timeline(), terms("ug.pipeline", "group")])
    b.vis("trace-dispatch-outcomes", "Sampled dispatch outcomes", "table", dispatch, [metric()],
          [terms("ug.pipeline", "bucket"), terms("ug.transport", "bucket", 3), terms("ug.outcome", "bucket", 5)], x=24)
    b.row(10)
    b.vis("trace-preparation", "Sampled contract preparation outcomes", "table", prepare, [metric()],
          [terms("ug.pipeline", "bucket"), terms("ug.outcome", "bucket", 5)])
    b.vis("trace-preparation-latency", "Preparation p95 latency by pipeline — ms", "line", prepare,
          [metric("percentiles", "ug.duration_ms", percents=[95])], [timeline(), terms("ug.pipeline", "group")], x=24)
    b.row(10)
    b.vis("trace-retry-attempts", "Sampled source deliveries by retry attempt", "horizontal_bar", handler,
          [metric()], [terms("ug.retry_attempt", size=10)])
    b.vis("trace-dispatch-http", "Sampled HTTP dispatch response codes", "horizontal_bar", dispatch + ' and ug.transport: "http"',
          [metric()], [terms("ug.http_status")], x=24)
    b.row(10)
    b.vis("trace-operations", "Operation latency breakdown — ms", "table", traces + ' and ug.duration_ms: *',
          [metric(), metric("percentiles", "ug.duration_ms", percents=[50, 95, 99])],
          [terms("ug.operation", "bucket", 20)], w=48, h=12,
          description="Includes handler, dispatch, preparation and instrumented dependencies. Durations overlap; no sum or end-to-end claim.")
    b.row(12)

    b.markdown("investigations", "## Investigate an image, failure or slow trace\nUse the KQL bar with `trace.id`, `ug.image`, `ug.pipeline` or `ug.message_id`. Clear pipeline filters to include source-wide events. An image filter finds handler traces and logs; child spans do not inherit the image tag. Replace the image filter with `trace.id` to investigate the full trace. **Trace IDs open APM; unsampled or expired traces may not be available.**", 6)
    b.search("recent-errors", "Recent warnings and errors", logs + ' and ug.log_level: ("warning" or "error" or "critical")',
             ["@timestamp", "ug.environment", "ug.pipeline", "ug.event_code", "message", "ug.error_category", "error.message", "ug.image", "trace.id"])
    b.search("slow-handlers", "Slowest sampled source deliveries", handler + ' and ug.duration_ms: *',
             ["@timestamp", "ug.environment", "ug.duration_ms", "ug.outcome", "ug.retry_attempt", "ug.image", "ug.sensor", "trace.id"], sort="ug.duration_ms")
    b.search("slow-dispatches", "Slowest sampled dispatch attempts", dispatch + ' and ug.duration_ms: *',
             ["@timestamp", "ug.environment", "ug.pipeline", "ug.transport", "ug.duration_ms", "ug.outcome", "ug.http_status", "trace.id"], sort="ug.duration_ms")
    b.search("failed-traces", "Recent failed trace operations", traces + ' and event.outcome: "failure"',
             ["@timestamp", "ug.environment", "ug.operation", "ug.pipeline", "ug.duration_ms", "ug.error_category", "trace.id"])
    b.search("recent-source", "Recent source outcomes and dispatch totals", event(6021),
             ["@timestamp", "ug.environment", "ug.outcome", "ug.image", "ug.planned_units", "ug.delivered_units", "ug.failed_units", "trace.id"])
    b.search("recent-rejected-rules", "Recent rejected rules and reasons", event(6012),
             ["@timestamp", "ug.environment", "ug.pipeline", "findair.rule.id", "ug.rule_rejection_reason", "message", "error.message"],
             description="Rule detail events are capped at ten per load and can repeat across refreshes.")
    b.search("image-timeline", "Gateway log timeline — filter by image, message or trace ID", logs,
             ["@timestamp", "ug.environment", "ug.image", "ug.message_id", "messaging.message.conversation_id", "ug.pipeline", "ug.event_code", "message", "trace.id"],
             h=15, description="All exported gateway logs, newest first. Use the KQL bar to correlate an image or trace across processing stages.")
    b.markdown("interpretation", "### Reading this dashboard\n- **Debug panels:** enable `Logging__EcsHttp__LogLevel__ImagingPipeline.UnifiedGateway=Debug` for delivered dispatches, filter decisions and refresh snapshots. An empty panel can mean missing telemetry rather than zero activity.\n- **Sampling and retries:** traces may be sampled; logs may be filtered/dropped; retries repeat attempts. Distinct-image counts are approximate.\n- **Environment:** a runtime field normalizes `Integration` and `integration`. Pipeline and instance names come from actual documents.\n- **Time and cost:** start with one environment and one hour. Refresh is paused by default. Normalization reads runtime fields; broad ranges across many traces cost more.\n- **Signals:** this dashboard uses logs and raw APM traces. Queue depth, CPU and exporter-drop metrics remain in Prometheus.\n- **Trace links:** target the Space selected when generating the import; APM must be enabled there.", 9)
    return b


def serialize_object(obj):
    result = copy.deepcopy(obj)
    a = result["attributes"]
    for key in ("visState", "uiStateJSON", "panelsJSON", "optionsJSON"):
        if key in a and not isinstance(a[key], str):
            a[key] = dumps(a[key])
    meta = a.get("kibanaSavedObjectMeta")
    if meta and not isinstance(meta.get("searchSourceJSON"), str):
        meta["searchSourceJSON"] = dumps(meta["searchSourceJSON"])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service", default="unified-gateway", help="Exact deployed service.name / OTEL_SERVICE_NAME")
    parser.add_argument("--logs", default="logs-unified_gateway-*", help="Elasticsearch log pattern")
    parser.add_argument("--traces", default="traces-apm*,apm-*", help="Current and legacy Elastic APM indices; trace panels select raw spans/transactions")
    parser.add_argument("--space", default="default", help="Kibana Space ID used by APM trace links")
    parser.add_argument("--base-path", default="", help="Kibana reverse-proxy base path, e.g. /kibana (no /s/space)")
    parser.add_argument("--output", type=Path, default=HERE / "unified-gateway-dashboard.ndjson")
    args = parser.parse_args()
    if args.base_path and (not args.base_path.startswith("/") or "://" in args.base_path):
        parser.error("--base-path must be a path beginning with /, not a server URL")
    b = build(args)
    objects = [serialize_object(o) for o in b.finish()]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(dumps(o) for o in objects) + "\n", encoding="utf-8")
    manifest = {"targetKibana": "8.15.x", "savedObjectSchemaSource": "8.4.1",
                "dashboardId": b.dashboard_id, "dataViewId": b.view_id,
                "service": args.service, "logs": args.logs, "traces": args.traces,
                "space": args.space, "basePath": args.base_path, "savedObjectCount": len(objects),
                "panelCount": len(b.catalog), "panels": b.catalog}
    args.output.with_suffix(".manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Created {args.output.name}: {len(objects)} saved objects, {len(b.catalog)} dashboard panels.")


if __name__ == "__main__":
    main()
