from __future__ import annotations

import subprocess
import threading
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

from backend.runtime import tool_args, tool_status


@dataclass(frozen=True)
class ScreenshotCapture:
    path: Path
    url: str
    timestamp: str


@dataclass(frozen=True)
class ScreenshotAttempt:
    capture: ScreenshotCapture | None
    error: str = ""


class ScreenshotRecorder:
    def __init__(self, root: Path, interval_sec: float = 5.0) -> None:
        self.root = root
        self.interval_sec = interval_sec
        self._latest: ScreenshotCapture | None = None
        self._last_elapsed = -interval_sec
        self._inflight = False
        self._udid = ""
        self._last_error = ""
        self._lock = threading.Lock()

    def set_udid(self, udid: str) -> None:
        with self._lock:
            self._udid = udid.strip()

    def configure(self, interval_sec: float) -> None:
        with self._lock:
            self.interval_sec = max(5.0, min(300.0, interval_sec))

    def request_next_capture(self) -> None:
        with self._lock:
            self._last_elapsed = -self.interval_sec
            self._last_error = ""

    def reset(self) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        with self._lock:
            self._latest = None
            self._last_elapsed = -self.interval_sec
            self._inflight = False
            self._last_error = ""

    def capture_due(self, elapsed_sec: float, index: int) -> ScreenshotCapture | None:
        with self._lock:
            if self._inflight or elapsed_sec - self._last_elapsed < self.interval_sec:
                return self._latest
            self._last_elapsed = elapsed_sec
            self._inflight = True
        thread = threading.Thread(target=self._capture_worker, args=(index,), daemon=True)
        thread.start()
        return self.latest()

    def latest(self) -> ScreenshotCapture | None:
        with self._lock:
            return self._latest

    def _capture_worker(self, index: int) -> None:
        with self._lock:
            udid = self._udid
        attempt = capture_screenshot(self.root, index, udid)
        with self._lock:
            if attempt.capture:
                self._latest = attempt.capture
                self._last_error = ""
            elif attempt.error:
                self._last_error = attempt.error
            self._inflight = False

    def latest_url(self) -> str:
        capture = self.latest()
        return capture.url if capture else ""

    def last_error(self) -> str:
        with self._lock:
            return self._last_error


def capture_screenshot(root: Path, index: int, udid: str = "") -> ScreenshotAttempt:
    tidevice = tool_status("tidevice")
    if tidevice == "missing":
        return ScreenshotAttempt(None, "未检测到 tidevice，无法截图。")

    root.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().astimezone().strftime("%Y%m%d-%H%M%S")
    filename = f"{stamp}-{index:05d}.png"
    path = root / filename
    command = tool_args("tidevice", [])
    if udid:
        command.extend(["-u", udid])
    command.extend(["screenshot", str(path)])
    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=15,
        )
    except subprocess.TimeoutExpired:
        path.unlink(missing_ok=True)
        return ScreenshotAttempt(None, "截图超时，请确认设备未锁屏并保持 USB 连接。")
    except OSError as exc:
        path.unlink(missing_ok=True)
        return ScreenshotAttempt(None, f"截图命令启动失败：{exc}")

    if result.returncode != 0:
        message = clip_message(result.stderr or result.stdout or "无错误输出")
        path.unlink(missing_ok=True)
        return ScreenshotAttempt(None, f"tidevice screenshot 失败，退出码 {result.returncode}：{message}")
    if not path.exists() or path.stat().st_size == 0:
        path.unlink(missing_ok=True)
        return ScreenshotAttempt(None, "tidevice screenshot 未生成有效图片。")
    return ScreenshotAttempt(
        ScreenshotCapture(
            path=path,
            url=f"/captures/screenshots/{filename}",
            timestamp=datetime.now().astimezone().isoformat(timespec="seconds"),
        )
    )


def clip_message(message: str, limit: int = 180) -> str:
    text = " ".join(str(message or "").split())
    if len(text) <= limit:
        return text
    return f"{text[:limit]}..."
