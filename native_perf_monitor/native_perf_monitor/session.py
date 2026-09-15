from __future__ import annotations

import threading
from datetime import datetime
from pathlib import Path

from PyQt5.QtCore import QObject, pyqtSignal

from backend.collectors.ios import RealIosCollector, RealIosCollectorUnavailable

from native_perf_monitor.models import CaptureConfig, NativeSample, NativeScreenshot
from native_perf_monitor.screenshot import NativeScreenshotRecorder


class NativeSession(QObject):
    sample_ready = pyqtSignal(object)
    screenshot_ready = pyqtSignal(object)
    message = pyqtSignal(str)
    error = pyqtSignal(str, bool)
    running_changed = pyqtSignal(bool)

    def __init__(self, data_root: Path) -> None:
        super().__init__()
        self._data_root = data_root
        self._stop_event = threading.Event()
        self._lock = threading.RLock()
        self._thread: threading.Thread | None = None
        self._collector: RealIosCollector | None = None
        self._config = CaptureConfig("", "com.tencent.xin", None, "", False, 3.0)
        self._screenshots = NativeScreenshotRecorder(
            data_root / "screenshots",
            on_capture=self._emit_screenshot,
            on_error=lambda message: self.error.emit(message, False),
        )

    def is_running(self) -> bool:
        with self._lock:
            return self._thread is not None and self._thread.is_alive()

    def start(self, config: CaptureConfig) -> None:
        if self.is_running():
            self.stop()
        with self._lock:
            self._config = config
            self._stop_event.clear()
            self._screenshots.reset(config.udid, config.capture_screenshots, config.screenshot_interval_sec)
            self._thread = threading.Thread(target=self._run, name="native-ios-session", daemon=True)
            self._thread.start()

    def stop(self) -> None:
        with self._lock:
            self._stop_event.set()
            collector = self._collector
        if collector:
            collector.stop()
        thread = self._thread
        if thread and thread.is_alive():
            thread.join(timeout=2)
        with self._lock:
            self._thread = None
            self._collector = None
        self.running_changed.emit(False)

    def update_capture_options(self, enabled: bool, interval_sec: float) -> None:
        with self._lock:
            current = self._config
            self._config = CaptureConfig(
                udid=current.udid,
                bundle_id=current.bundle_id,
                target_pid=current.target_pid,
                target_name=current.target_name,
                capture_screenshots=enabled,
                screenshot_interval_sec=interval_sec,
            )
            self._screenshots.configure(enabled, interval_sec)

    def _run(self) -> None:
        config = self._config
        started_at = datetime.now().astimezone()
        collector: RealIosCollector | None = None
        try:
            collector = RealIosCollector()
            collector.assert_available()
            collector.start(config.bundle_id, config.udid)
            with self._lock:
                self._collector = collector
            self.running_changed.emit(True)
            self.message.emit("采集已开始。")

            index = 0
            while not self._stop_event.is_set():
                with self._lock:
                    current = self._config
                sample = collector.sample(
                    started_at=started_at,
                    index=index,
                    bundle_id=current.bundle_id,
                    target_pid=current.target_pid,
                    target_name=current.target_name,
                )
                native_sample = NativeSample.from_backend(sample)
                self.sample_ready.emit(native_sample)
                if current.capture_screenshots:
                    self._screenshots.capture_due(native_sample.elapsed_sec, index)
                index += 1
                self._stop_event.wait(1.0)
        except RealIosCollectorUnavailable as exc:
            self.error.emit(str(exc), True)
        except Exception as exc:  # noqa: BLE001 - keep native tool failures visible.
            self.error.emit(f"采集异常：{exc}", True)
        finally:
            if collector:
                collector.stop()
            with self._lock:
                self._collector = None
                self._thread = None
            self.running_changed.emit(False)

    def _emit_screenshot(self, screenshot: NativeScreenshot) -> None:
        self.screenshot_ready.emit(screenshot)
