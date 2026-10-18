#!/usr/bin/env python3
"""Check the gateway dashboard's saved-object structure before a Kibana import.

Uses only the Python standard library. This validates references, nested JSON,
layout, data-view fields, aggregation wiring, and service/signal scoping. It does
not execute Painless/KQL or replace an import/render check in a live Kibana.
The visualization serialization rules are pinned in schema-reference.json;
modern dashboard controls use the Kibana v8.15.0 source references packaged
alongside the dashboard.
"""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys

HERE = Path(__file__).resolve().parent
INDEX_REF = "kibanaSavedObjectMeta.searchSourceJSON.index"
ALLOWED_TYPES = {"index-pattern", "visualization", "search", "dashboard"}
XY_TYPES = {"line", "area", "horizontal_bar"}
METRIC_TYPES = {"count", "cardinality", "sum", "avg", "max", "min", "percentiles", "top_hits"}
BUCKET_TYPES = {"terms", "filters", "date_histogram"}


class InvalidDashboard(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise InvalidDashboard(message)


def nested(value, expected, location):
    require(isinstance(value, str), f"{location} must be serialized JSON, not {type(value).__name__}")
    try:
        result = json.loads(value)
    except json.JSONDecodeError as exc:
        raise InvalidDashboard(f"{location}: {exc}") from exc
    require(isinstance(result, expected), f"{location} must decode to {expected.__name__}")
    return result


def version_tuple(version):
    require(isinstance(version, str) and re.fullmatch(r"\d+\.\d+\.\d+", version),
            f"Invalid saved-object migration version {version!r}")
    return tuple(int(n) for n in version.split("."))


def check_migrations(obj, target):
    name = obj["attributes"].get("title", obj["id"])
    versions = {"coreMigrationVersion": obj.get("coreMigrationVersion")}
    if "typeMigrationVersion" in obj:
        versions["typeMigrationVersion"] = obj["typeMigrationVersion"]
    versions.update(obj.get("migrationVersion", {}))
    require(versions["coreMigrationVersion"] is not None, f"{name}: missing core migration metadata")
    require("typeMigrationVersion" in obj or obj["type"] in obj.get("migrationVersion", {}),
            f"{name}: missing type migration metadata")
    for key, version in versions.items():
        require(version_tuple(version) <= target,
                f"{name}: {key}={version} is newer than the target Kibana; exports are not backward compatible")


def check_source(obj, field_names, view_id, signal_required=True):
    title = obj["attributes"]["title"]
    source = nested(obj["attributes"]["kibanaSavedObjectMeta"]["searchSourceJSON"], dict,
                    f"{title}.searchSourceJSON")
    require(source.get("indexRefName") == INDEX_REF, f"{title}: missing data-view reference in search source")
    require(any(r == {"name": INDEX_REF, "type": "index-pattern", "id": view_id} for r in obj["references"]),
            f"{title}: search source does not reference the shared data view")
    query = source.get("query", {})
    require(query.get("language") == "kuery" and isinstance(query.get("query"), str), f"{title}: expected KQL query")
    query_text = query["query"]
    require(source.get("filter") == [], f"{title}: unexpected hidden source filters")
    if signal_required:
        require(re.search(r'ug\.signal\s*:\s*"(?:logs|traces)"', query_text),
                f"{title}: missing explicit logs/traces scope")
    for field in re.findall(r"\bug\.[A-Za-z0-9_]+(?=\s*:)", query_text):
        require(field in field_names, f"{title}: KQL refers to unknown runtime field {field}")
    return query_text


def check_view(obj):
    a = obj["attributes"]
    fields = nested(a["fields"], list, "data view.fields")
    runtime = nested(a["runtimeFieldMap"], dict, "data view.runtimeFieldMap")
    formats = nested(a["fieldFormatMap"], dict, "data view.fieldFormatMap")
    by_name = {f["name"]: f for f in fields}
    require(len(by_name) == len(fields), "Duplicate data-view field names")
    require(a.get("timeFieldName") == "@timestamp", "Data view must use @timestamp for the time picker")
    require(by_name.get("@timestamp", {}).get("type") == "date", "Timestamp must be a cached date field")
    require(a.get("allowNoIndex") is True, "Data view should allow traces not yet present")
    require("," in a.get("title", ""), "Expected combined log and trace index patterns")
    for name, spec in runtime.items():
        require(name.startswith("ug."), f"Runtime field {name} could shadow an indexed telemetry field")
        require(spec.get("type") in {"keyword", "long", "double"}, f"Unsupported runtime type for {name}")
        require(isinstance(spec.get("script", {}).get("source"), str) and spec["script"]["source"].strip(),
                f"Missing runtime script for {name}")
        field = by_name.get(name)
        require(field is not None, f"Runtime field {name} missing from cached field definitions")
        require(field.get("runtimeField") == spec, f"Runtime script/cache mismatch for {name}")
        require(field.get("type") == ("string" if spec["type"] == "keyword" else "number"),
                f"Wrong Kibana field type for runtime field {name}")
        require(field.get("esTypes") == [spec["type"]], f"Wrong ES type for runtime field {name}")
        require(field.get("searchable") is True and field.get("aggregatable") is True,
                f"Runtime field {name} must be searchable and aggregatable")
        require(not field.get("scripted", False), f"{name}: runtime fields are not legacy scripted fields")
    for name, field in by_name.items():
        if name.startswith("ug."):
            require(name in runtime, f"Cached runtime field {name} has no runtime_mappings definition")
        if "runtimeField" in field:
            require(name in runtime, f"Cached field {name} contains an orphan runtime definition")
    for name in formats:
        require(name in by_name, f"Format configured for unknown field {name}")
    require(formats.get("ug.trace_failure", {}).get("id") == "percent", "Failure share must be formatted as percent")
    require(formats.get("trace.id", {}).get("id") == "url", "Trace IDs must link to APM")
    require("{{value}}" in formats["trace.id"]["params"].get("urlTemplate", ""), "Missing trace-ID URL substitution")
    return by_name, runtime


def check_visualization(obj, field_map, view_id):
    title = obj["attributes"]["title"]
    state = nested(obj["attributes"]["visState"], dict, f"{title}.visState")
    nested(obj["attributes"]["uiStateJSON"], dict, f"{title}.uiStateJSON")
    require(state.get("title") == title, f"{title}: visState title disagrees with saved object")
    kind = state.get("type")
    require(kind != "input_control_vis", f"{title}: deprecated input controls must use the dashboard control group")
    require(kind in XY_TYPES | {"metric", "pie", "table", "markdown"},
            f"{title}: unsupported visualization type {kind}")
    if kind == "markdown":
        require(state.get("aggs") == [] and obj["references"] == [], f"{title}: markdown retained fixture dependencies")
        require(isinstance(state.get("params", {}).get("markdown"), str), f"{title}: missing Markdown")
        return ""
    query = check_source(obj, field_map, view_id)
    aggs = state.get("aggs", [])
    require(aggs, f"{title}: no aggregations")
    by_id = {a["id"]: a for a in aggs}
    require(len(by_id) == len(aggs), f"{title}: duplicate aggregation IDs")
    metrics = [a for a in aggs if a.get("schema") == "metric"]
    require(metrics, f"{title}: no metric aggregation")
    for agg in aggs:
        agg_type, schema, p = agg["type"], agg["schema"], agg["params"]
        require(agg.get("enabled") is True, f"{title}: unexpected disabled aggregation")
        require(agg_type in METRIC_TYPES | BUCKET_TYPES, f"{title}: unknown aggregation {agg_type}")
        require((schema == "metric") == (agg_type in METRIC_TYPES), f"{title}: wrong aggregation schema for {agg_type}")
        if "field" in p:
            field = field_map.get(p["field"])
            require(field is not None, f"{title}: unknown aggregation field {p['field']}")
            require(field.get("aggregatable") is True or agg_type == "top_hits", f"{title}: field {p['field']} cannot aggregate")
            if agg_type in {"sum", "avg", "percentiles"}:
                require(field["type"] == "number", f"{title}: {agg_type} requires numeric field")
        if agg_type == "percentiles":
            values = p.get("percents", [])
            require(values and all(isinstance(v, (int, float)) and 0 <= v <= 100 for v in values),
                    f"{title}: invalid percentile list")
        if agg_type == "terms":
            require(p.get("orderBy") in {"_count", "_key"} | set(by_id), f"{title}: broken terms ordering")
            require(p.get("order") in {"asc", "desc"} and p.get("size", 0) > 0, f"{title}: invalid terms options")
        if agg_type == "date_histogram":
            require(p.get("field") == "@timestamp" and p.get("interval") == "auto", f"{title}: expected adaptive timestamp histogram")
        if agg_type == "top_hits":
            require(p.get("size") == 1 and p.get("sortField") == "@timestamp" and p.get("sortOrder") == "desc",
                    f"{title}: cache snapshot must select the latest value, not sum refreshes")
            require(p.get("aggregate") in {"average", "min", "max", "sum", "concat"}, f"{title}: invalid top_hits reduction")
        if agg_type == "filters":
            for filt in p["filters"]:
                require(filt.get("label") and filt.get("input", {}).get("language") == "kuery",
                        f"{title}: filter buckets need labels and KQL inputs")
                for field in re.findall(r"\bug\.[A-Za-z0-9_]+(?=\s*:)", filt["input"]["query"]):
                    require(field in field_map, f"{title}: unknown filter-bucket field {field}")
    if kind in XY_TYPES:
        p = state["params"]
        series = p["seriesParams"]
        # Kibana 8.4 getSafeId() reduces 1.50 and 1.95 to parent ID 1. A single
        # parent seriesParam is correct for a multi-value percentile aggregation.
        require({s["data"]["id"] for s in series} == {m["id"] for m in metrics},
                f"{title}: seriesParams must reference each parent metric ID exactly once")
        require(len(series) == len(metrics), f"{title}: duplicate seriesParams")
        axis_ids = {axis["id"] for axis in p["valueAxes"]}
        require(all(s.get("valueAxis") in axis_ids for s in series), f"{title}: unknown series axis")
        require(p.get("fittingFunction") in {None, "none", "zero", "linear", "carry", "lookahead"},
                f"{title}: unsupported gap-fitting function")
        require(all(s.get("mode") in {"normal", "stacked"} for s in series), f"{title}: invalid stacking mode")
        if "latency" in title.lower():
            require(p.get("fittingFunction") == "none", f"{title}: missing latency samples must not become zero")
    return query


def check_controls(dashboard, field_map, view_id, manifest):
    group = dashboard["attributes"].get("controlGroupInput")
    require(isinstance(group, dict), "Dashboard must have a modern controlGroupInput object")
    require(group.get("controlStyle") == "oneLine", "Dashboard controls must use the compact one-line style")
    require(group.get("chainingSystem") == "HIERARCHICAL", "Instance options must follow the environment selection")
    require(group.get("showApplySelections") is False, "Dashboard control selections must apply immediately")
    ignore = nested(group.get("ignoreParentSettingsJSON"), dict, "dashboard.controlGroupInput.ignoreParentSettingsJSON")
    require(ignore == {"ignoreFilters": False, "ignoreQuery": False, "ignoreTimerange": False, "ignoreValidations": False},
            "Dashboard controls must respect the dashboard query, filters, time range, and selection validation")
    controls = nested(group.get("panelsJSON"), dict, "dashboard.controlGroupInput.panelsJSON")
    require(len(controls) == 2, "Expected environment and instance controls")
    expected_fields = {0: "ug.environment", 1: "ug.instance"}
    refs = {r["name"]: r for r in dashboard["references"]}
    used_refs = set()
    catalog = []
    orders = set()
    for control_id, control in controls.items():
        require(control.get("type") == "optionsListControl", f"Control {control_id}: expected modern options-list type")
        order = control.get("order")
        require(type(order) is int and order in expected_fields and order not in orders,
                f"Control {control_id}: duplicate or invalid control order")
        orders.add(order)
        require(control.get("width") == "medium" and control.get("grow") is True,
                f"Control {control_id}: unexpected layout settings")
        explicit = control.get("explicitInput", {})
        require(explicit.get("id") == control_id, f"Control {control_id}: input identity differs from control-map key")
        field_name = explicit.get("fieldName")
        require(field_name == expected_fields[order], f"Control {control_id}: wrong field or chaining order")
        field = field_map.get(field_name, {})
        require(field.get("type") == "string" and field.get("aggregatable") is True and field.get("searchable") is True,
                f"Control {control_id}: {field_name} must be a searchable, aggregatable string field")
        require(isinstance(explicit.get("title"), str) and explicit["title"].strip(),
                f"Control {control_id}: missing display title")
        require(explicit.get("selectedOptions") == [], f"Control {control_id}: unexpected preselected options")
        require(explicit.get("searchTechnique") == "prefix", f"Control {control_id}: unexpected search technique")
        require(explicit.get("enhancements") == {}, f"Control {control_id}: unexpected enhancement state")
        require("dataViewId" not in explicit, f"Control {control_id}: data view must be extracted into saved-object references")
        ref_name = f"controlGroup_{control_id}:optionsListDataView"
        require(refs.get(ref_name) == {"name": ref_name, "type": "index-pattern", "id": view_id},
                f"Control {control_id}: missing shared data-view reference {ref_name}")
        used_refs.add(ref_name)
        catalog.append({"id": control_id, "fieldName": field_name, "title": explicit["title"], "order": order})
    catalog.sort(key=lambda item: item["order"])
    require(manifest.get("controlCount") == len(controls), "Manifest control count disagrees with dashboard")
    require(manifest.get("controls") == catalog, "Manifest control catalog disagrees with dashboard")
    return used_refs


def validate(objects, manifest, target=(8, 15, 0)):
    require(objects, "Empty NDJSON import")
    by_key = {(o.get("type"), o.get("id")): o for o in objects}
    require(len(by_key) == len(objects), "Duplicate saved-object type/ID")
    require(len({o["id"] for o in objects}) == len(objects), "Duplicate object IDs across types")
    for obj in objects:
        require(obj.get("type") in ALLOWED_TYPES, f"Unexpected saved-object type {obj.get('type')}")
        require(isinstance(obj.get("attributes"), dict), f"{obj['id']}: missing attributes")
        require(isinstance(obj.get("references"), list), f"{obj['id']}: missing references")
        check_migrations(obj, target)
        names = [r["name"] for r in obj["references"]]
        require(len(names) == len(set(names)), f"{obj['id']}: duplicate reference names")
        for ref in obj["references"]:
            require((ref["type"], ref["id"]) in by_key, f"{obj['id']}: missing referenced object {ref['name']}")
    views = [o for o in objects if o["type"] == "index-pattern"]
    dashboards = [o for o in objects if o["type"] == "dashboard"]
    require(len(views) == len(dashboards) == 1, "Expected one combined data view and one dashboard")
    view, dashboard = views[0], dashboards[0]
    field_map, runtime = check_view(view)
    control_refs = check_controls(dashboard, field_map, view["id"], manifest)
    a = dashboard["attributes"]
    source = nested(a["kibanaSavedObjectMeta"]["searchSourceJSON"], dict, "dashboard.searchSourceJSON")
    expected_query = "service.name: " + json.dumps(manifest["service"], ensure_ascii=False, separators=(",", ":"))
    require(source.get("query") == {"language": "kuery", "query": expected_query},
            "Dashboard must be scoped to the exact configured service.name")
    require(source.get("filter") == [], "Dashboard retained fixture filters")
    nested(a["optionsJSON"], dict, "dashboard.optionsJSON")
    require(a.get("refreshInterval", {}).get("pause") is True, "Automatic refresh should start paused")
    panels = nested(a["panelsJSON"], list, "dashboard.panelsJSON")
    indexes = [p["panelIndex"] for p in panels]
    require(len(indexes) == len(set(indexes)), "Duplicate dashboard panel indexes")
    refs = {r["name"]: r for r in dashboard["references"]}
    used_refs = set(control_refs)
    queries = {}
    rectangles = []
    reached = {(dashboard["type"], dashboard["id"]), (view["type"], view["id"])}
    for panel in panels:
        panel_index = panel["panelIndex"]
        name = f"{panel_index}:{panel['panelRefName']}"
        require(name in refs, f"Panel {panel_index}: missing prefixed dashboard reference {name}")
        ref = refs[name]
        used_refs.add(name)
        require(ref["type"] == panel["type"], f"Panel {panel_index}: wrong referenced type")
        obj = by_key[(ref["type"], ref["id"])]
        reached.add((ref["type"], ref["id"]))
        g = panel["gridData"]
        require(g["i"] == panel_index, f"Panel {panel_index}: grid identity mismatch")
        require(all(isinstance(g[k], int) for k in ("x", "y", "w", "h")), f"Panel {panel_index}: noninteger layout")
        require(g["x"] >= 0 and g["y"] >= 0 and g["w"] > 0 and g["h"] > 0 and g["x"] + g["w"] <= 48,
                f"Panel {panel_index}: rectangle exceeds the 48-column grid")
        for other_index, other in rectangles:
            intersects = (g["x"] < other["x"] + other["w"] and g["x"] + g["w"] > other["x"] and
                          g["y"] < other["y"] + other["h"] and g["y"] + g["h"] > other["y"])
            require(not intersects, f"Panels {panel_index} and {other_index} overlap")
        rectangles.append((panel_index, g))
        require(set(panel.get("embeddableConfig", {})) <= {"enhancements"},
                f"Panel {panel_index}: unexpected per-panel override can defeat common filtering")
        if obj["type"] == "visualization":
            query = check_visualization(obj, field_map, view["id"])
        elif obj["type"] == "search":
            query = check_source(obj, field_map, view["id"])
            for col in obj["attributes"]["columns"]:
                require(col in field_map, f"{obj['attributes']['title']}: unknown saved-search column {col}")
            for sort_field, direction in obj["attributes"]["sort"]:
                require(sort_field in field_map and direction in {"asc", "desc"}, "Invalid saved-search sorting")
        else:
            raise InvalidDashboard(f"Unexpected panel type {obj['type']}")
        queries[obj["id"]] = query
    require(reached == set(by_key), "Import contains orphan saved objects")
    require(set(refs) == used_refs, "Dashboard contains stale fixture references")
    require(len(refs) == len(panels) + len(control_refs), "Panel and control references must be distinct")
    require(manifest["savedObjectCount"] == len(objects) and manifest["panelCount"] == len(panels),
            "Manifest counts disagree with NDJSON")
    require(manifest["dashboardId"] == dashboard["id"] and manifest["dataViewId"] == view["id"],
            "Manifest identifiers disagree with NDJSON")
    require(len(manifest["panels"]) == len(panels), "Manifest panel catalog is incomplete")
    for row in manifest["panels"]:
        scoped_query = f"({expected_query}) and ({row['query']})" if row["query"] else ""
        require(row["id"] in queries and scoped_query == queries[row["id"]],
                f"Manifest query mismatch for {row['title']}")
    return {"objects": len(objects), "panels": len(panels), "controls": len(control_refs), "runtimeFields": len(runtime),
            "visualizationTypes": dict(Counter(json.loads(o["attributes"]["visState"])["type"]
                                               for o in objects if o["type"] == "visualization"))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", nargs="?", type=Path, default=HERE / "unified-gateway-dashboard.ndjson")
    parser.add_argument("--manifest", type=Path, help="Defaults to the matching .manifest.json file")
    parser.add_argument("--target-version", default="8.15.0", help="Oldest supported Kibana for migration checks")
    args = parser.parse_args()
    try:
        objects = [json.loads(line) for line in args.file.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
        manifest = json.loads((args.manifest or args.file.with_suffix(".manifest.json")).read_text(encoding="utf-8-sig"))
        result = validate(objects, manifest, version_tuple(args.target_version))
    except (InvalidDashboard, KeyError, TypeError, ValueError, OSError) as exc:
        print(f"INVALID: {exc}", file=sys.stderr)
        return 1
    print("PASS: " + json.dumps(result, sort_keys=True))
    print("Static validation only; live Kibana import/render and runtime-script compilation are separate checks.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
