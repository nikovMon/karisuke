#!/usr/bin/env python3
"""Execute dashboard runtime fields against synthetic data in a LOCAL Elasticsearch.

Writes one uniquely named temporary index, then deletes that exact index in a
finally block. Non-loopback URLs are rejected. Never point this at production.
Uses only the standard library; Elasticsearch 8.15.0 is the intended target.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sys
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen
import uuid

HERE = Path(__file__).resolve().parent


def local_endpoint(value):
    parsed = urlsplit(value)
    if (parsed.scheme not in {"http", "https"}
            or parsed.hostname not in {"127.0.0.1", "localhost", "::1"}
            or parsed.username or parsed.password or parsed.query or parsed.fragment
            or parsed.path not in {"", "/"}):
        raise argparse.ArgumentTypeError("Use a loopback-only Elasticsearch URL without credentials or a path")
    return value.rstrip("/")


def request(endpoint, method, path, body=None):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = Request(endpoint + path, data=data, method=method,
                  headers={"Content-Type": "application/json"})
    try:
        with urlopen(req, timeout=45) as response:
            return json.load(response)
    except HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"{method} {path}: HTTP {exc.code}\n{detail}") from exc


def fixtures():
    log = {
        "@timestamp": "2026-10-18T10:00:00Z",
        "service": {"name": "unified-gateway", "environment": "Integration", "instance": {"id": "gw-int-1"}},
        "data_stream": {"namespace": "integration"},
        "log": {"level": "Debug"}, "event": {"code": "6021", "outcome": "success"},
        "pipeline": {"id": "FindAir", "transport": "RabbitMq"},
        "kubernetes": {"pod": {"name": "gw-pod-1"}},
        "findair": {"image": {"id": "image-1"}, "sensor": {"name": "sensor-1"},
                    "area": {"name": "area-1"}, "error": {"category": "http_failure"}, "retry": {"attempt": 2}},
        "messaging": {"message": {"id": "message-1"}},
        "error": {"code": "invalid-image"}, "http": {"response": {"status_code": 502}},
        "labels": {"db_collection_name": "findair-rules"},
        "unified_gateway": {
            "dispatch": {"units": {"count": 5}, "failed": {"count": 2}},
            "filter": {"reason": "no_matching_rules"}, "rule": {"rejection": {"reason": "invalid_rule"}},
            "rules": {"loaded": {"count": 11}, "rejected": {"count": 3}, "retained": {"count": 7},
                      "missed": {"photo_age": {"count": 1}, "sensor": {"count": 2},
                                 "resolution": {"count": 3}, "geometry": {"count": 4}}},
        },
    }
    log_expected = {
        "environment": "integration", "pipeline": "FindAir", "transport": "RabbitMq", "instance": "gw-int-1",
        "pod": "gw-pod-1", "event_code": "6021", "log_level": "debug", "image": "image-1", "sensor": "sensor-1",
        "area": "area-1", "message_id": "message-1", "error_category": "http_failure", "validation_code": "invalid-image",
        "filter_reason": "no_matching_rules", "rule_rejection_reason": "invalid_rule", "rule_index": "findair-rules",
        "http_status": 502, "retry_attempt": 2, "planned_units": 5, "failed_units": 2, "rules_loaded": 11,
        "rules_rejected": 3, "rules_retained": 7, "missed_photo_age": 1, "missed_sensor": 2,
        "missed_resolution": 3, "missed_geometry": 4, "signal": "logs", "trace_failure": 0.0,
        "outcome": "success", "delivered_units": 3,
    }
    span = {
        "@timestamp": "2026-10-18T10:01:00Z",
        "service": {"name": "unified-gateway", "environment": "Production", "node": {"name": "gw-prod-1"}},
        "data_stream": {"namespace": "default"}, "processor": {"event": "span"},
        "span": {"name": "unified_gateway.dispatch", "duration": {"us": 1500}},
        "transaction": {"name": "parent-name-must-not-replace-span-name"}, "event": {"outcome": "failure"},
        "labels": {"pipeline_id": "DeepWisdom", "pipeline_transport": "Http", "findair_outcome": "retry",
                   "findair_error_category": "http_failure", "findair_image_id": "image-2",
                   "findair_sensor_name": "sensor-2", "findair_area_name": "area-2",
                   "messaging_message_id": "message-2", "k8s_pod_name": "gw-pod-2"},
        "numeric_labels": {"http_response_status_code": 503, "findair_retry_attempt": 3},
    }
    span_expected = {
        "environment": "production", "instance": "gw-prod-1", "signal": "traces",
        "operation": "unified_gateway.dispatch", "duration_ms": 1.5, "trace_failure": 1.0,
        "pipeline": "DeepWisdom", "transport": "Http", "outcome": "retry", "error_category": "http_failure",
        "image": "image-2", "sensor": "sensor-2", "area": "area-2", "message_id": "message-2", "pod": "gw-pod-2",
        "http_status": 503, "retry_attempt": 3,
    }
    transaction = {
        "@timestamp": "2026-10-18T10:02:00Z",
        "service": {"name": "unified-gateway", "environment": "Development"},
        "processor": {"event": "transaction"},
        "transaction": {"name": "rabbitmq handler", "duration": {"us": 9000}},
        "event": {"outcome": "success"},
    }
    transaction_expected = {
        "environment": "development", "signal": "traces", "operation": "rabbitmq handler",
        "duration_ms": 9.0, "trace_failure": 0.0, "outcome": "success",
    }
    flat = {
        "@timestamp": "2026-10-18T10:03:00Z", "service.name": "unified-gateway",
        "service.environment": "Integration", "processor.event": "span", "span.name": "unified_gateway.contract.prepare",
        "event.duration": 1230000, "labels.pipeline_outcome": "prepared",
        "labels.http_response_status_code": "201", "labels.findair_retry_attempt": "4",
    }
    flat_expected = {
        "environment": "integration", "signal": "traces", "operation": "unified_gateway.contract.prepare",
        "duration_ms": 1.23, "outcome": "prepared", "http_status": 201, "retry_attempt": 4,
    }
    missing = {"@timestamp": "2026-10-18T10:04:00Z", "service": {"name": "unified-gateway"}}
    other_service = json.loads(json.dumps(log))
    other_service["service"]["name"] = "another-service"
    no_service = {"@timestamp": "2026-10-18T10:05:00Z", "log": {"level": "Information"}}
    zero_delivered = {"@timestamp": "2026-10-18T10:06:00Z", "service": {"name": "unified-gateway"},
                      "unified_gateway": {"dispatch": {"units": {"count": 2}, "failed": {"count": 2}}}}
    negative_delivered = {"@timestamp": "2026-10-18T10:07:00Z", "service": {"name": "unified-gateway"},
                          "unified_gateway": {"dispatch": {"units": {"count": 2}, "failed": {"count": 3}}}}
    return {"log": (log, log_expected), "span": (span, span_expected), "transaction": (transaction, transaction_expected),
            "flat": (flat, flat_expected), "missing": (missing, {}), "other-service": (other_service, {}),
            "no-service": (no_service, {}),
            "zero-delivered": (zero_delivered, {"planned_units": 2, "failed_units": 2, "delivered_units": 0}),
            "negative-delivered": (negative_delivered, {"planned_units": 2, "failed_units": 3, "delivered_units": 0})}


def check_search(response):
    if response.get("timed_out") or response.get("_shards", {}).get("failed", 0):
        raise AssertionError("Search failed: " + json.dumps(response))
    return response


def run(endpoint, bundle):
    objects = [json.loads(line) for line in bundle.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    view = next(obj for obj in objects if obj["type"] == "index-pattern")
    runtime = json.loads(view["attributes"]["runtimeFieldMap"])
    samples = fixtures()
    covered = {"ug." + key for _, expected in samples.values() for key in expected}
    assert covered == set(runtime), f"Uncovered runtime fields: {set(runtime) - covered}; stale expectations: {covered - set(runtime)}"
    version = request(endpoint, "GET", "/")["version"]["number"]
    index = "ug-dashboard-smoke-" + uuid.uuid4().hex
    created = False
    try:
        request(endpoint, "PUT", "/" + index, {"settings": {"number_of_shards": 1, "number_of_replicas": 0},
                "mappings": {"properties": {"@timestamp": {"type": "date"},
                                            "service": {"properties": {"name": {"type": "keyword"}}}}}})
        created = True
        for doc_id, (document, _) in samples.items():
            request(endpoint, "PUT", f"/{index}/_doc/{doc_id}", document)
        request(endpoint, "POST", f"/{index}/_refresh")

        def search(body):
            return check_search(request(endpoint, "POST", f"/{index}/_search?allow_partial_search_results=false",
                                        {"runtime_mappings": runtime, **body}))

        # Include every runtime mapping even when a query only needs ug.signal:
        # a compile error in an unrelated field can prevent all panels from loading.
        response = search({"size": 0, "query": {"term": {"ug.signal": "logs"}}})
        assert response["hits"]["total"]["value"] == 1, response

        response = search({"size": len(samples), "_source": False, "fields": list(runtime), "query": {"match_all": {}}})
        assert response["hits"]["total"]["value"] == len(samples), response
        for hit in response["hits"]["hits"]:
            expected = {"ug." + key: [value] for key, value in samples[hit["_id"]][1].items()}
            actual = hit.get("fields", {})
            assert actual == expected, f"Document {hit['_id']}\nExpected: {expected}\nActual: {actual}"

        log_aggs = search({"size": 0, "query": {"term": {"ug.signal": "logs"}}, "aggs": {
            "pipelines": {"terms": {"field": "ug.pipeline"}, "aggs": {
                "latest": {"top_hits": {"size": 1, "sort": [{"@timestamp": "desc"}], "_source": False,
                                         "fields": [{"field": "ug.rules_loaded"}, {"field": "ug.rules_rejected"}]}}}},
            "delivered": {"sum": {"field": "ug.delivered_units"}},
            "planned": {"avg": {"field": "ug.planned_units"}},
            "environment": {"terms": {"field": "ug.environment"}},
        }})["aggregations"]
        assert log_aggs["delivered"]["value"] == 3, log_aggs
        assert log_aggs["planned"]["value"] == 5, log_aggs
        assert log_aggs["environment"]["buckets"][0]["key"] == "integration", log_aggs
        assert log_aggs["pipelines"]["buckets"][0]["key"] == "FindAir", log_aggs
        assert log_aggs["pipelines"]["buckets"][0]["latest"]["hits"]["hits"][0]["fields"] == {
            "ug.rules_loaded": [11], "ug.rules_rejected": [3]}, log_aggs

        trace_aggs = search({"size": 0, "query": {"term": {"ug.signal": "traces"}}, "aggs": {
            "duration": {"stats": {"field": "ug.duration_ms"}},
            "latency": {"percentiles": {"field": "ug.duration_ms", "percents": [50, 95, 99]}},
            "failure_share": {"avg": {"field": "ug.trace_failure"}},
            "operations": {"terms": {"field": "ug.operation"}},
        }})["aggregations"]
        assert trace_aggs["duration"]["count"] == 3, trace_aggs
        assert math.isclose(trace_aggs["duration"]["sum"], 11.73), trace_aggs
        assert trace_aggs["failure_share"]["value"] == 0.5, trace_aggs
        assert {bucket["key"] for bucket in trace_aggs["operations"]["buckets"]} == {
            "unified_gateway.dispatch", "rabbitmq handler", "unified_gateway.contract.prepare"}, trace_aggs
        for value in trace_aggs["latency"]["values"].values():
            assert math.isfinite(value) and 1.23 <= value <= 9, trace_aggs
        print(f"PASS: Elasticsearch {version}; {len(runtime)} runtime fields; {len(samples)} synthetic documents; "
              "signal filters, terms, sums, averages, percentiles, and latest-value top_hits.")
    finally:
        if created:
            request(endpoint, "DELETE", "/" + index)
            print(f"Deleted synthetic index {index}.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", type=local_endpoint, default="http://127.0.0.1:19215")
    parser.add_argument("--bundle", type=Path, default=HERE / "unified-gateway-dashboard.ndjson")
    args = parser.parse_args()
    try:
        run(args.url, args.bundle)
    except Exception as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
