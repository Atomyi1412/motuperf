from __future__ import annotations

import ast
import json
import math
import os
import queue
import re
import subprocess
import threading
import time
from dataclasses import dataclass
from datetime import datetime
from typing import Any

from backend.models import Sample
from backend.runtime import tool_args, tool_status


UTF8_ENV = {"PYTHONUTF8": "1", "PYTHONIOENCODING": "utf-8"}


@dataclass(frozen=True)
class DependencyStatus:
    apple_mobile_device_service: str
    pyidevice: str
    tidevice: str
    pymobiledevice3: str
    idevice_id: str
    ideviceinfo: str

    def to_dict(self) -> dict[str, str]:
        return {
            "apple_mobile_device_service": self.apple_mobile_device_service,
            "pyidevice": self.pyidevice,
            "tidevice": self.tidevice,
            "pymobiledevice3": self.pymobiledevice3,
            "idevice_id": self.idevice_id,
            "ideviceinfo": self.ideviceinfo,
        }


@dataclass(frozen=True)
class IosDevice:
    udid: str
    name: str
    market_name: str
    product_version: str
    conn_type: str
    serial: str
    recommended: bool

    def to_dict(self) -> dict[str, Any]:
        return {
            "udid": self.udid,
            "name": self.name,
            "market_name": self.market_name,
            "product_version": self.product_version,
            "conn_type": self.conn_type,
            "serial": self.serial,
            "recommended": self.recommended,
        }


@dataclass(frozen=True)
class IosApp:
    bundle_id: str
    name: str
    version: str
    recommended: bool
    reason: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "bundle_id": self.bundle_id,
            "name": self.name,
            "version": self.version,
            "recommended": self.recommended,
            "reason": self.reason,
        }


@dataclass(frozen=True)
class IosProcess:
    pid: int
    name: str
    bundle_id: str
    display_name: str
    recommended: bool
    reason: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "pid": self.pid,
            "name": self.name,
            "bundle_id": self.bundle_id,
            "display_name": self.display_name,
            "recommended": self.recommended,
            "reason": self.reason,
        }


def command_status(command: str) -> str:
    return tool_status(command)


def apple_mobile_device_service_status() -> str:
    try:
        result = subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-Command",
                "(Get-Service -Name 'Apple Mobile Device Service' -ErrorAction SilentlyContinue).Status",
            ],
            check=False,
            capture_output=True,
            text=True,
            timeout=5,
        )
    except (OSError, subprocess.SubprocessError):
        return "unknown"

    status = result.stdout.strip()
    return status.lower() if status else "missing"


def probe_dependencies() -> DependencyStatus:
    return DependencyStatus(
        apple_mobile_device_service=apple_mobile_device_service_status(),
        pyidevice=command_status("pyidevice"),
        tidevice=command_status("tidevice"),
        pymobiledevice3=command_status("pymobiledevice3"),
        idevice_id=command_status("idevice_id"),
        ideviceinfo=command_status("ideviceinfo"),
    )


def dependency_warnings(status: DependencyStatus) -> list[str]:
    warnings: list[str] = []
    if status.apple_mobile_device_service != "running":
        warnings.append("Apple Mobile Device Service 未运行，USB iOS 设备可能无法通信。")
    if status.pyidevice == "missing" and status.tidevice == "missing":
        warnings.append("未检测到 pyidevice 或 tidevice，真实 iOS 采集暂不可用。")
    return warnings


def list_ios_devices() -> list[IosDevice]:
    tidevice = command_status("tidevice")
    if tidevice == "missing":
        return []

    result = subprocess.run(
        tool_args("tidevice", ["list", "--json"]),
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=command_env(),
        timeout=12,
    )
    if result.returncode != 0:
        return []

    try:
        raw_devices = json.loads(result.stdout)
    except json.JSONDecodeError:
        return []

    devices: list[IosDevice] = []
    for index, item in enumerate(raw_devices):
        if not isinstance(item, dict):
            continue
        udid = str(item.get("udid") or "")
        if not udid:
            continue
        devices.append(
            IosDevice(
                udid=udid,
                name=clean_display_text(item.get("name") or ""),
                market_name=clean_display_text(item.get("market_name") or ""),
                product_version=str(item.get("product_version") or ""),
                conn_type=str(item.get("conn_type") or ""),
                serial=str(item.get("serial") or ""),
                recommended=index == 0,
            )
        )
    return devices


def list_ios_apps(udid: str = "") -> list[IosApp]:
    tidevice = command_status("tidevice")
    if tidevice == "missing":
        return ensure_known_host_apps([])

    result = subprocess.run(
        tidevice_args(udid, ["applist"]),
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=command_env(),
        timeout=20,
    )
    apps = ensure_known_host_apps(parse_applist_output(result.stdout))
    return sorted(apps, key=app_sort_key)


def parse_applist_output(output: str) -> list[IosApp]:
    apps: list[IosApp] = []
    for raw_line in output.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        parts = line.rsplit(" ", 1)
        if len(parts) != 2:
            continue
        left, version = parts
        bundle_and_name = left.split(" ", 1)
        bundle_id = bundle_and_name[0].strip()
        if "." not in bundle_id:
            continue
        name = clean_display_text(bundle_and_name[1] if len(bundle_and_name) > 1 else bundle_id)
        recommended, reason = classify_app(bundle_id, name)
        apps.append(IosApp(bundle_id=bundle_id, name=name, version=version.strip(), recommended=recommended, reason=reason))
    return apps


def classify_app(bundle_id: str, name: str) -> tuple[bool, str]:
    if bundle_id == "com.tencent.xin":
        return True, "微信宿主应用，测试微信小游戏默认选择它。"
    if bundle_id in {"com.tencent.mqq", "com.tencent.xin"} or name in {"QQ", "微信", "WeChat"}:
        return True, "腾讯宿主应用，常用于小游戏或小程序测试。"
    return False, ""


def ensure_known_host_apps(apps: list[IosApp]) -> list[IosApp]:
    known_apps = [
        IosApp(
            bundle_id="com.tencent.xin",
            name="微信",
            version="",
            recommended=True,
            reason="微信宿主应用，测试微信小游戏默认选择它。",
        )
    ]
    merged = list(apps)
    for known in known_apps:
        for index, app in enumerate(merged):
            if app.bundle_id != known.bundle_id:
                continue
            merged[index] = IosApp(
                bundle_id=app.bundle_id,
                name=app.name or known.name,
                version=app.version,
                recommended=True,
                reason=app.reason or known.reason,
            )
            break
        else:
            merged.append(known)
    return merged


def app_sort_key(app: IosApp) -> tuple[int, str, str]:
    rank = 0 if app.bundle_id == "com.tencent.xin" else 1 if app.recommended else 2
    return (rank, app.name.lower(), app.bundle_id.lower())


def list_ios_processes(udid: str = "") -> list[IosProcess]:
    tidevice = command_status("tidevice")
    if tidevice == "missing":
        return []

    result = subprocess.run(
        tidevice_args(udid, ["ps", "--json", "-A"]),
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=command_env(),
        timeout=12,
    )
    if result.returncode != 0:
        return []

    try:
        raw_processes = json.loads(result.stdout)
    except json.JSONDecodeError:
        return []

    processes: list[IosProcess] = []
    for item in raw_processes:
        if not isinstance(item, dict):
            continue
        pid = as_int(item.get("pid"), -1)
        if pid < 0:
            continue
        name = str(item.get("name") or "")
        bundle_id = str(item.get("bundle_id") or "")
        display_name = clean_display_text(item.get("display_name") or "")
        recommended, reason = classify_process(name, bundle_id)
        processes.append(
            IosProcess(
                pid=pid,
                name=name,
                bundle_id=bundle_id,
                display_name=display_name,
                recommended=recommended,
                reason=reason,
            )
        )

    return sorted(processes, key=process_sort_key)


def command_env() -> dict[str, str]:
    env = dict(os.environ)
    env.update(UTF8_ENV)
    return env


def tidevice_args(udid: str, args: list[str]) -> list[str]:
    command = tool_args("tidevice", [])
    if udid:
        command.extend(["-u", udid])
    command.extend(args)
    return command


def clean_display_text(value: Any) -> str:
    text = str(value or "")
    if "\ufffd" in text:
        return ""
    return text


def classify_process(name: str, bundle_id: str) -> tuple[bool, str]:
    if bundle_id == "com.tencent.xin" or name == "WeChat":
        return True, "微信宿主进程，PerfDog 文档建议小游戏先选它。"
    if name == "com.apple.WebKit.WebContent":
        return True, "WebContent 进程，小程序或高性能模式小游戏常需要观察新出现或 pid 较大的项。"
    if name == "com.apple.WebKit.GPU":
        return True, "WebKit GPU 进程，可辅助排查高性能模式渲染压力。"
    if name == "com.apple.WebKit.Networking":
        return True, "WebKit 网络进程，可辅助排查加载和网络行为。"
    return False, ""


def process_sort_key(process: IosProcess) -> tuple[int, int, str]:
    if process.bundle_id == "com.tencent.xin" or process.name == "WeChat":
        rank = 0
    elif process.name == "com.apple.WebKit.WebContent":
        rank = 1
    elif process.name.startswith("com.apple.WebKit"):
        rank = 2
    elif process.bundle_id:
        rank = 3
    else:
        rank = 4
    return (rank, -process.pid, process.name.lower())


def format_target_note(bundle_id: str, target_pid: int | None, target_name: str) -> str:
    if target_pid and target_name:
        return f"{bundle_id}；参考进程 {target_name} pid={target_pid}"
    if target_pid:
        return f"{bundle_id}；参考 pid={target_pid}"
    return bundle_id


class RealIosCollectorUnavailable(RuntimeError):
    pass


class RealIosCollector:
    source = "ios-usb"
    FPS_FRESH_SECONDS = 3.5
    MEMORY_FRESH_SECONDS = 6.0

    def __init__(self) -> None:
        self.dependencies = probe_dependencies()
        self._processes: list[subprocess.Popen[str]] = []
        self._reader_threads: list[threading.Thread] = []
        self._events: queue.Queue[tuple[str, dict[str, Any]]] = queue.Queue()
        self._errors: queue.Queue[str] = queue.Queue()
        self._latest_fps: float | None = None
        self._latest_fps_at: float | None = None
        self._latest_jank: int | None = None
        self._latest_big_jank: int | None = None
        self._latest_jank_at: float | None = None
        self._native_jank_seen = False
        self._latest_memory_mb: float | None = None
        self._latest_memory_at: float | None = None
        self._backend_name = "unknown"

    def assert_available(self) -> None:
        if self.dependencies.pyidevice == "missing" and self.dependencies.tidevice == "missing":
            raise RealIosCollectorUnavailable(
                "真实 iOS USB 采集依赖未安装。请先安装 py-ios-device 或 tidevice。"
            )

    def start(self, bundle_id: str, udid: str = "") -> None:
        self.assert_available()
        if self.dependencies.tidevice != "missing":
            self._backend_name = "tidevice"
            self._start_tidevice(bundle_id, udid)
        elif self.dependencies.pyidevice != "missing":
            self._backend_name = "py-ios-device"
            self._start_pyidevice(bundle_id, udid)

    def stop(self) -> None:
        for proc in self._processes:
            if proc.poll() is None:
                proc.terminate()
        for proc in self._processes:
            try:
                proc.wait(timeout=2)
            except subprocess.TimeoutExpired:
                proc.kill()
        self._processes = []

    def sample(
        self,
        started_at: datetime,
        index: int,
        bundle_id: str,
        target_pid: int | None = None,
        target_name: str = "",
    ) -> Sample:
        self._drain_events()
        now = datetime.now().astimezone()
        received_at = time.monotonic()
        has_fps = self._latest_fps is not None and is_fresh(
            self._latest_fps_at,
            self.FPS_FRESH_SECONDS,
            received_at,
        )
        has_jank = (
            has_fps
            and self._native_jank_seen
            and self._latest_jank is not None
            and self._latest_big_jank is not None
            and is_fresh(self._latest_jank_at, self.FPS_FRESH_SECONDS, received_at)
        )
        has_memory = self._latest_memory_mb is not None and is_fresh(
            self._latest_memory_at,
            self.MEMORY_FRESH_SECONDS,
            received_at,
        )
        target_note = format_target_note(bundle_id, target_pid, target_name)
        note = self._latest_error() or f"真实 USB 数据：{target_note}"
        if not has_fps and not has_memory:
            note = f"等待 {self._backend_name} 返回数据；请确认设备已信任电脑、目标应用正在前台运行。目标：{target_note}"
        if has_fps and not has_jank:
            note = f"{note}；缺少有序 Display FrameTime，Jank/BigJank 不可用。"
        return Sample(
            timestamp=now.isoformat(timespec="seconds"),
            elapsed_sec=(now - started_at).total_seconds(),
            fps=self._latest_fps if has_fps else None,
            jank=self._latest_jank if has_jank else None,
            big_jank=self._latest_big_jank if has_jank else None,
            memory_mb=self._latest_memory_mb if has_memory else None,
            screenshot_url="",
            source=self._backend_name,
            note=note,
        )

    def _start_pyidevice(self, bundle_id: str, udid: str) -> None:
        base = tool_args("pyidevice", [])
        if udid:
            base.extend(["--udid", udid])
        self._spawn([*base, "instruments", "display"], "pyidevice-display")
        self._spawn([*base, "instruments", "appmonitor", "-b", bundle_id], "pyidevice-appmonitor")

    def _start_tidevice(self, bundle_id: str, udid: str) -> None:
        self._spawn(tidevice_args(udid, ["perf", "-B", bundle_id, "-o", "fps,memory", "--json"]), "tidevice-perf")

    def _spawn(self, command: list[str], label: str) -> None:
        try:
            proc = subprocess.Popen(
                command,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            )
        except OSError as exc:
            self._errors.put(f"{label} 启动失败：{exc}")
            return

        self._processes.append(proc)
        thread = threading.Thread(target=self._read_lines, args=(proc, label), daemon=True)
        thread.start()
        self._reader_threads.append(thread)

    def _read_lines(self, proc: subprocess.Popen[str], label: str) -> None:
        assert proc.stdout is not None
        for line in proc.stdout:
            parsed = parse_perf_line(label, line)
            if parsed:
                self._events.put(parsed)
        code = proc.wait()
        if code != 0:
            self._errors.put(f"{label} 已退出，退出码 {code}")

    def _drain_events(self) -> None:
        while True:
            try:
                kind, payload = self._events.get_nowait()
            except queue.Empty:
                break
            if kind == "fps":
                fps_value = first_present(payload, "fps", "value")
                fps = as_float(fps_value)
                now = time.monotonic()
                if fps is not None and 0 <= fps <= 240:
                    self._latest_fps = fps
                    self._latest_fps_at = now
                has_native_jank = "jank" in payload or "big_jank" in payload or "bigJank" in payload
                if has_native_jank:
                    jank = as_non_negative_int(payload.get("jank"))
                    big_jank = as_non_negative_int(first_present(payload, "big_jank", "bigJank"))
                    if jank is not None or big_jank is not None:
                        self._latest_big_jank = big_jank or 0
                        self._latest_jank = max(jank or 0, self._latest_big_jank)
                        self._latest_jank_at = now
                        self._native_jank_seen = True
                    else:
                        self._clear_jank()
                else:
                    self._clear_jank()
            if kind == "memory":
                memory = parse_memory_mb(first_present(payload, "Memory", "value", "physFootprint"))
                if memory is not None and math.isfinite(memory) and memory > 0:
                    self._latest_memory_mb = memory
                    self._latest_memory_at = time.monotonic()

    def _clear_jank(self) -> None:
        self._native_jank_seen = False
        self._latest_jank = None
        self._latest_big_jank = None
        self._latest_jank_at = None

    def _latest_error(self) -> str | None:
        latest: str | None = None
        while True:
            try:
                latest = self._errors.get_nowait()
            except queue.Empty:
                return latest


def parse_perf_line(label: str, line: str) -> tuple[str, dict[str, Any]] | None:
    stripped = line.strip()
    if not stripped:
        return None

    if label == "tidevice-perf":
        prefix, _, raw = stripped.partition(" ")
        if prefix in {"fps", "memory"} and raw:
            try:
                return prefix, json.loads(raw)
            except json.JSONDecodeError:
                return None

    payload = parse_python_dict(stripped)
    if payload is None:
        return None
    if "fps" in payload:
        return "fps", payload
    if "Memory" in payload or "physFootprint" in payload:
        return "memory", payload
    return None


def parse_python_dict(text: str) -> dict[str, Any] | None:
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end <= start:
        return None
    try:
        value = ast.literal_eval(text[start : end + 1])
    except (ValueError, SyntaxError):
        return None
    if isinstance(value, dict):
        return value
    return None


def as_float(value: Any) -> float | None:
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    return number if math.isfinite(number) else None


def as_non_negative_int(value: Any) -> int | None:
    number = as_float(value)
    if number is None or number < 0 or not number.is_integer():
        return None
    return int(number)


def first_present(payload: dict[str, Any], *keys: str) -> Any:
    for key in keys:
        if key in payload and payload[key] is not None:
            return payload[key]
    return None


def is_fresh(timestamp: float | None, max_age_seconds: float, now: float | None = None) -> bool:
    if timestamp is None:
        return False
    current = time.monotonic() if now is None else now
    return 0 <= current - timestamp <= max_age_seconds


def as_int(value: Any, default: int = 0) -> int:
    try:
        return int(float(value))
    except (TypeError, ValueError):
        return default


def parse_memory_mb(value: Any) -> float | None:
    if value is None:
        return None
    if isinstance(value, (int, float)):
        number = float(value)
        if number > 1024 * 1024:
            return number / 1024 / 1024
        return number
    text = str(value)
    match = re.search(r"([-+]?[0-9]*\.?[0-9]+)\s*([kmgt]?i?b|b)?", text, re.IGNORECASE)
    if not match:
        return None
    number = float(match.group(1))
    unit = (match.group(2) or "MiB").lower()
    if unit in {"b"}:
        return number / 1024 / 1024
    if unit in {"kb", "kib"}:
        return number / 1024
    if unit in {"gb", "gib"}:
        return number * 1024
    if unit in {"tb", "tib"}:
        return number * 1024 * 1024
    return number
