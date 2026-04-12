import json
import threading
import time
from copy import deepcopy
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib import error, parse, request


BASE_DIR = Path(__file__).resolve().parent
CONFIG_PATH = BASE_DIR / "services.json"
EXAMPLE_CONFIG_PATH = BASE_DIR / "services.example.json"

CONFIG = {}
INCIDENTS = []
SERVICE_STATE = {}
LATEST_SNAPSHOT = {}
LOCK = threading.Lock()


def now_iso():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def read_config():
    source = CONFIG_PATH if CONFIG_PATH.exists() else EXAMPLE_CONFIG_PATH
    return json.loads(source.read_text(encoding="utf-8"))


def normalize_status(status_code, expected_status):
    if status_code == expected_status:
        return "operational"
    if 0 < status_code < 500:
        return "degraded"
    return "outage"


def overall_status(services):
    if any(item["status"] == "outage" for item in services):
        return "outage"
    if any(item["status"] == "degraded" for item in services):
        return "degraded"
    return "operational"


def average_uptime_30d(services):
    if not services:
        return None
    total = 0.0
    for item in services:
        uptime = item.get("uptime30d", 100)
        total += float(uptime if uptime is not None else 100)
    return round(total / len(services), 2)


def build_snapshot(services, status_override=None):
    overall = status_override or overall_status(services)
    return {
        "updatedAt": now_iso(),
        "overall": {
            "title": CONFIG["site"]["title"],
            "message": CONFIG["site"]["message"],
            "status": overall,
            "uptime30d": average_uptime_30d(services),
        },
        "services": services,
        "incidents": deepcopy(INCIDENTS[:10]),
    }


def normalize_audience(value):
    audience = (value or "").strip().lower()
    return audience or None


def service_audience(service):
    if service.get("public") is False:
        return "internal"
    return normalize_audience(service.get("audience") or service.get("visibility")) or "public"


def filter_snapshot(snapshot, audience=None):
    normalized_audience = normalize_audience(audience)
    if normalized_audience is None:
        return deepcopy(snapshot)

    services = [
        deepcopy(service)
        for service in snapshot.get("services", [])
        if service_audience(service) == normalized_audience
    ]
    visible_ids = {service.get("id") for service in services if service.get("id")}
    incidents = [
        deepcopy(item)
        for item in snapshot.get("incidents", [])
        if any(str(item.get("id", "")).startswith(f"{service_id}-") for service_id in visible_ids)
    ]

    filtered = build_snapshot(services)
    filtered["updatedAt"] = snapshot.get("updatedAt") or filtered["updatedAt"]
    filtered["incidents"] = incidents
    return filtered


def record_incident(service, previous_status, next_status):
    INCIDENTS.insert(
        0,
        {
            "id": f"{service['id']}-{int(time.time() * 1000)}",
            "title": f"{service['name']} \\u72b6\\u6001\\u53d8\\u5316\\uff1a{previous_status} -> {next_status}",
            "summary": f"\\u63a2\\u6d4b\\u63a5\\u53e3 {service['probeUrl']} \\u7684\\u8fd4\\u56de\\u7ed3\\u679c\\u53d1\\u751f\\u53d8\\u5316\\u3002",
            "impact": "major" if next_status == "outage" else "minor",
            "status": "resolved" if next_status == "operational" else "investigating",
            "startedAt": now_iso(),
            "resolvedAt": now_iso() if next_status == "operational" else None,
            "updates": [
                {
                    "at": now_iso(),
                    "message": f"\\u81ea\\u52a8\\u63a2\\u6d4b\\u8bb0\\u5f55\\u5230\\u72b6\\u6001\\u53d8\\u5316\\uff0c\\u5f53\\u524d\\u72b6\\u6001\\u4e3a {next_status}\\u3002",
                }
            ],
        },
    )
    del INCIDENTS[20:]


def probe_service(service):
    started_at = time.time()
    req = request.Request(
        service.get("probeUrl"),
        method=service.get("method", "GET"),
        headers={"user-agent": "voicelinkvr-status-probe/1.0.0"},
    )

    history = SERVICE_STATE.get(service["id"], {"checks": 0, "success": 0, "status": "loading"})

    try:
        with request.urlopen(req, timeout=(service.get("timeoutMs", 5000) / 1000.0)) as response:
            response.read(16)
            status_code = response.getcode()

        response_time_ms = int((time.time() - started_at) * 1000)
        status = normalize_status(status_code, service.get("expectedStatus", 200))
        history["checks"] += 1
        if status == "operational":
            history["success"] += 1
        if history["status"] != "loading" and history["status"] != status:
            record_incident(service, history["status"], status)
        history["status"] = status
        SERVICE_STATE[service["id"]] = history

        return {
            "id": service["id"],
            "name": service["name"],
            "description": service.get("description"),
            "status": status,
            "region": service.get("region", "--"),
            "responseTimeMs": response_time_ms,
            "uptime30d": round((history["success"] / history["checks"]) * 100, 2),
            "lastCheckedAt": now_iso(),
            "publicEndpoint": service.get("publicEndpoint") or service.get("probeUrl"),
        }
    except error.HTTPError as exc:
        response_time_ms = int((time.time() - started_at) * 1000)
        status = normalize_status(exc.code, service.get("expectedStatus", 200))
        history["checks"] += 1
        if status == "operational":
            history["success"] += 1
        if history["status"] != "loading" and history["status"] != status:
            record_incident(service, history["status"], status)
        history["status"] = status
        SERVICE_STATE[service["id"]] = history

        return {
            "id": service["id"],
            "name": service["name"],
            "description": service.get("description"),
            "status": status,
            "region": service.get("region", "--"),
            "responseTimeMs": response_time_ms,
            "uptime30d": round((history["success"] / history["checks"]) * 100, 2),
            "lastCheckedAt": now_iso(),
            "publicEndpoint": service.get("publicEndpoint") or service.get("probeUrl"),
        }
    except Exception as exc:
        history["checks"] += 1
        if history["status"] != "loading" and history["status"] != "outage":
            record_incident(service, history["status"], "outage")
        history["status"] = "outage"
        SERVICE_STATE[service["id"]] = history

        return {
            "id": service["id"],
            "name": service["name"],
            "description": f"{service.get('description', '')} ({type(exc).__name__})".strip(),
            "status": "outage",
            "region": service.get("region", "--"),
            "responseTimeMs": None,
            "uptime30d": round((history["success"] / history["checks"]) * 100, 2),
            "lastCheckedAt": now_iso(),
            "publicEndpoint": service.get("publicEndpoint") or service.get("probeUrl"),
        }


def refresh_snapshot():
    global CONFIG, LATEST_SNAPSHOT
    with LOCK:
        CONFIG = read_config()
        services = [probe_service(service) for service in CONFIG.get("services", [])]
        LATEST_SNAPSHOT = build_snapshot(services)
        return deepcopy(LATEST_SNAPSHOT)


def refresh_loop():
    while True:
        try:
            refresh_snapshot()
        except Exception:
            with LOCK:
                LATEST_SNAPSHOT.update(build_snapshot(LATEST_SNAPSHOT.get("services", []), "degraded"))
        time.sleep(CONFIG.get("site", {}).get("refreshIntervalMs", 60000) / 1000.0)


class StatusHandler(BaseHTTPRequestHandler):
    def _write_json(self, payload, status_code=200):
        origin = CONFIG.get("site", {}).get("publicOrigin", "*")
        body = json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Access-Control-Allow-Origin", origin)
        self.send_header("Access-Control-Allow-Methods", "GET, OPTIONS")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):
        origin = CONFIG.get("site", {}).get("publicOrigin", "*")
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", origin)
        self.send_header("Access-Control-Allow-Methods", "GET, OPTIONS")
        self.end_headers()

    def do_GET(self):
        parsed = parse.urlparse(self.path)
        query = parse.parse_qs(parsed.query)
        audience = (query.get("audience") or [None])[0]

        if parsed.path == "/healthz":
            with LOCK:
                payload = {"ok": True, "updatedAt": LATEST_SNAPSHOT.get("updatedAt")}
            self._write_json(payload)
            return

        if parsed.path == "/api/status/public":
            with LOCK:
                payload = filter_snapshot(LATEST_SNAPSHOT, audience)
            self._write_json(payload)
            return

        if parsed.path == "/api/status/refresh":
            payload = filter_snapshot(refresh_snapshot(), audience)
            self._write_json(payload)
            return

        self._write_json({"error": "Not Found"}, 404)

    def log_message(self, format, *args):
        return


if __name__ == "__main__":
    CONFIG = read_config()
    LATEST_SNAPSHOT = build_snapshot([], "loading")

    try:
        refresh_snapshot()
    except Exception as exc:
        LATEST_SNAPSHOT = build_snapshot([], "outage")
        LATEST_SNAPSHOT["overall"]["message"] = f"\\u521d\\u59cb\\u5316\\u63a2\\u6d4b\\u5931\\u8d25\\uff1a{exc}"

    threading.Thread(target=refresh_loop, daemon=True).start()

    port = int(CONFIG.get("site", {}).get("port", 8787))
    server = ThreadingHTTPServer(("0.0.0.0", port), StatusHandler)
    print(f"VoiceLinkVR status probe listening on port {port}")
    server.serve_forever()
