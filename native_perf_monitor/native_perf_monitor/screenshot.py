from __future__ import annotations

import subprocess
import threading
from datetime import datetime
from pathlib import Path
from typing import Callable

from backend.runtime import tool_args, tool_status

from native_perf_monitor.models import NativeScreenshot


ScreenshotCallback = Callable[[NativeScreenshot], None]
ErrorCallback = Callable[[str], None]


class NativeScreenshotRecorder:
    def __init__(self, root: Path, on_capture: ScreenshotCallback, on_error: ErrorCallback) -> None:
        self.root = root
        self._on_capture = on_capture
        self._on_error = on_error
        self._interval_sec = 3.0
        self._last_elapsed = -3.0
        self._udid = ""
        self._inflight = False
        self._enabled = False
        self._lock = threading.Lock()

    def reset(self, udid: str, enabled: bool, interval_sec: float) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        with self._lock:
            self._udid = udid.strip()
            self._enabled = enabled
            self._interval_sec = normalize_screenshot_interval(interval_sec)
            self._last_elapsed = -self._interval_sec
            self._inflight = False

    def configure(self, enabled: bool, interval_sec: float) -> None:
        with self._lock:
            was_enabled = self._enabled
            self._enabled = enabled
            self._interval_sec = normalize_screenshot_interval(interval_sec)
            if enabled and not was_enabled:
                self._last_elapsed = -self._interval_sec

    def capture_due(self, elapsed_sec: float, index: int) -> None:
        with self._lock:
            if not self._enabled:
                return
            if self._inflight or elapsed_sec - self._last_elapsed < self._interval_sec:
                return
            self._last_elapsed = elapsed_sec
            self._inflight = True
            udid = self._udid

        thread = threading.Thread(target=self._capture_worker, args=(elapsed_sec, index, udid), daemon=True)
        thread.start()

    def _capture_worker(self, elapsed_sec: float, index: int, udid: str) -> None:
        try:
            capture = capture_screenshot_file(self.root, elapsed_sec, index, udid)
        except ScreenshotError as exc:
            self._on_error(str(exc))
        else:
            self._on_capture(capture)
        finally:
            with self._lock:
                self._inflight = False


class ScreenshotError(RuntimeError):
    pass


def normalize_screenshot_interval(value: float) -> float:
    try:
        interval = float(value)
    except (TypeError, ValueError):
        interval = 3.0
    return max(3.0, min(300.0, interval))


def capture_screenshot_file(root: Path, elapsed_sec: float, index: int, udid: str = "") -> NativeScreenshot:
    tidevice = tool_status("tidevice")
    if tidevice == "missing":
        raise ScreenshotError("未检测到 tidevice，无法截图。")

    root.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().astimezone().strftime("%Y%m%d-%H%M%S")
    path = root / f"{stamp}-{index:05d}.png"
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
    except subprocess.TimeoutExpired as exc:
        path.unlink(missing_ok=True)
        raise ScreenshotError("截图超时，请确认设备未锁屏并保持 USB 连接。") from exc
    except OSError as exc:
        path.unlink(missing_ok=True)
        raise ScreenshotError(f"截图命令启动失败：{exc}") from exc

    if result.returncode != 0:
        path.unlink(missing_ok=True)
        message = clip_message(result.stderr or result.stdout or "无错误输出")
        raise ScreenshotError(f"tidevice screenshot 失败，退出码 {result.returncode}：{message}")
    if not path.exists() or path.stat().st_size == 0:
        path.unlink(missing_ok=True)
        raise ScreenshotError("tidevice screenshot 未生成有效图片。")

    return NativeScreenshot(
        elapsed_sec=elapsed_sec,
        path=path,
        timestamp=datetime.now().astimezone().isoformat(timespec="seconds"),
    )


def clip_message(message: str, limit: int = 180) -> str:
    text = " ".join(str(message or "").split())
    if len(text) <= limit:
        return text
    return f"{text[:limit]}..."
