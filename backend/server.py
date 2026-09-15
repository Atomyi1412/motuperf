from __future__ import annotations

import json
import argparse
from errno import ECONNABORTED, EPIPE, ECONNRESET
from http import HTTPStatus
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import parse_qs, urlparse

from backend.collectors.ios import dependency_warnings, list_ios_apps, list_ios_devices, list_ios_processes, probe_dependencies
from backend.runtime import data_dir, frontend_dir
from backend.session import SessionManager


FRONTEND = frontend_dir()
CAPTURES = data_dir()
SESSION = SessionManager()


class ApiHandler(SimpleHTTPRequestHandler):
    server_version = "IOSPerfMonitor/0.1"

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, directory=str(FRONTEND), **kwargs)

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)
        if path == "/api/status":
            self._json(self._status_payload())
            return
        if path == "/api/devices":
            self._json({"devices": [device.to_dict() for device in list_ios_devices()]})
            return
        if path == "/api/apps":
            self._json({"apps": [app.to_dict() for app in list_ios_apps(query_value(query, "udid"))]})
            return
        if path == "/api/processes":
            self._json({"processes": [process.to_dict() for process in list_ios_processes(query_value(query, "udid"))]})
            return
        if path == "/api/sessions/current/samples":
            self._json({"samples": SESSION.samples()})
            return
        if path == "/api/sessions/current/export.csv":
            csv_text = SESSION.export_csv()
            self.send_response(HTTPStatus.OK)
            self.send_header("Content-Type", "text/csv; charset=utf-8")
            self.send_header("Content-Disposition", 'attachment; filename="ios-performance-samples.csv"')
            self.send_header("Content-Length", str(len(csv_text.encode("utf-8"))))
            self.end_headers()
            self.wfile.write(csv_text.encode("utf-8"))
            return
        if path == "/api/sessions/current/export.json":
            self._json({"samples": SESSION.samples()})
            return
        if path.startswith("/captures/"):
            self._serve_capture(path)
            return
        if path == "/":
            self.path = "/index.html"
        super().do_GET()

    def do_POST(self) -> None:
        path = urlparse(self.path).path
        try:
            if path == "/api/sessions/start":
                payload = self._read_json()
                state = SESSION.start(
                    bundle_id=str(payload.get("bundle_id", "com.tencent.xin")),
                    mode=str(payload.get("mode", "ios")),
                    target_pid=parse_optional_int(payload.get("target_pid")),
                    target_name=str(payload.get("target_name") or ""),
                    udid=str(payload.get("udid") or ""),
                    capture_screenshots=parse_bool(payload.get("capture_screenshots")),
                    screenshot_interval_sec=parse_float(payload.get("screenshot_interval_sec"), 10.0),
                )
                self._json({"state": state.__dict__})
                return
            if path == "/api/sessions/options":
                payload = self._read_json()
                state = SESSION.update_capture_options(
                    capture_screenshots=parse_bool(payload.get("capture_screenshots")),
                    screenshot_interval_sec=parse_float(payload.get("screenshot_interval_sec"), 10.0),
                )
                self._json({"state": state.__dict__})
                return
            if path == "/api/sessions/stop":
                state = SESSION.stop()
                self._json({"state": state.__dict__})
                return
            if path == "/api/sessions/clear":
                state = SESSION.clear()
                self._json({"state": state.__dict__})
                return
            self._json({"error": "unknown endpoint"}, status=HTTPStatus.NOT_FOUND)
        except ValueError as exc:
            self._json({"error": str(exc)}, status=HTTPStatus.BAD_REQUEST)
        except Exception as exc:  # noqa: BLE001 - keep local tool errors visible to tester.
            self._json({"error": f"服务异常：{exc}"}, status=HTTPStatus.INTERNAL_SERVER_ERROR)

    def log_message(self, format: str, *args: Any) -> None:
        print(f"[server] {self.address_string()} - {format % args}")

    def _status_payload(self) -> dict[str, Any]:
        deps = probe_dependencies()
        state = SESSION.state()
        warnings = dependency_warnings(deps) + state.warnings
        if state.capture_screenshots and state.latest_screenshot_error:
            warnings.append(state.latest_screenshot_error)
        return {
            "running": state.running,
            "mode": state.mode,
            "udid": state.udid,
            "bundle_id": state.bundle_id,
            "target_pid": state.target_pid,
            "target_name": state.target_name,
            "started_at": state.started_at,
            "elapsed_sec": state.elapsed_sec,
            "sample_count": state.sample_count,
            "latest_screenshot_url": state.latest_screenshot_url,
            "latest_screenshot_error": state.latest_screenshot_error,
            "capture_screenshots": state.capture_screenshots,
            "screenshot_interval_sec": state.screenshot_interval_sec,
            "latest": state.latest,
            "dependencies": deps.to_dict(),
            "warnings": warnings,
        }

    def _serve_capture(self, path: str) -> None:
        parts = [part for part in path.removeprefix("/captures/").split("/") if part]
        target = CAPTURES.joinpath(*parts).resolve()
        captures_root = CAPTURES.resolve()
        if captures_root not in target.parents and target != captures_root:
            self._json({"error": "invalid capture path"}, status=HTTPStatus.BAD_REQUEST)
            return
        if not target.exists() or not target.is_file():
            self._json({"error": "capture not found"}, status=HTTPStatus.NOT_FOUND)
            return
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "image/png")
        self.send_header("Content-Length", str(target.stat().st_size))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        with target.open("rb") as image:
            self.wfile.write(image.read())

    def _read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length", "0"))
        if length == 0:
            return {}
        raw = self.rfile.read(length).decode("utf-8")
        return json.loads(raw)

    def _json(self, payload: dict[str, Any], status: HTTPStatus = HTTPStatus.OK) -> None:
        data = json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        try:
            self.wfile.write(data)
        except OSError as exc:
            if exc.errno not in {ECONNABORTED, ECONNRESET, EPIPE}:
                raise


def run(host: str = "127.0.0.1", port: int = 8770) -> None:
    server = ThreadingHTTPServer((host, port), ApiHandler)
    print(f"iOS performance monitor running at http://{host}:{port}")
    server.serve_forever()


def parse_optional_int(value: Any) -> int | None:
    if value in {None, ""}:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def parse_float(value: Any, default: float) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def parse_bool(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return value != 0
    if isinstance(value, str):
        return value.strip().lower() in {"1", "true", "yes", "y", "on"}
    return False


def query_value(query: dict[str, list[str]], key: str) -> str:
    values = query.get(key) or [""]
    return values[0]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Run the local iOS performance monitor.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8770)
    args = parser.parse_args()
    run(host=args.host, port=args.port)
