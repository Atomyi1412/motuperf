from __future__ import annotations

import csv
import io
import threading
import time
from datetime import datetime
from dataclasses import replace
from pathlib import Path
from typing import Any

from backend.collectors.ios import RealIosCollector, RealIosCollectorUnavailable
from backend.models import Sample, SessionState
from backend.runtime import data_dir as default_data_dir
from backend.screenshots import ScreenshotRecorder


class SessionManager:
    def __init__(self, data_dir: Path | None = None) -> None:
        self._lock = threading.RLock()
        self._data_dir = data_dir or default_data_dir()
        self._running = False
        self._mode = "ios"
        self._udid = ""
        self._bundle_id = "com.tencent.xin"
        self._target_pid: int | None = None
        self._target_name = "WeChat"
        self._capture_screenshots = False
        self._screenshot_interval_sec = 10.0
        self._started_at: datetime | None = None
        self._samples: list[Sample] = []
        self._thread: threading.Thread | None = None
        self._stop_event = threading.Event()
        self._warnings: list[str] = []
        self._real: RealIosCollector | None = None
        self._screenshots = ScreenshotRecorder(self._data_dir / "screenshots")

    def state(self) -> SessionState:
        with self._lock:
            latest = self._samples[-1].to_dict() if self._samples else None
            elapsed = 0.0
            if self._started_at:
                elapsed = (datetime.now().astimezone() - self._started_at).total_seconds()
            return SessionState(
                running=self._running,
                mode=self._mode,
                udid=self._udid,
                bundle_id=self._bundle_id,
                target_pid=self._target_pid,
                target_name=self._target_name,
                started_at=self._started_at.isoformat(timespec="seconds") if self._started_at else None,
                elapsed_sec=round(elapsed, 2),
                sample_count=len(self._samples),
                latest_screenshot_url=self._screenshots.latest_url(),
                latest_screenshot_error=self._screenshots.last_error(),
                capture_screenshots=self._capture_screenshots,
                screenshot_interval_sec=self._screenshot_interval_sec,
                latest=latest,
                warnings=list(self._warnings),
            )

    def samples(self) -> list[dict[str, Any]]:
        with self._lock:
            return [sample.to_dict() for sample in self._samples]

    def start(
        self,
        bundle_id: str,
        mode: str,
        target_pid: int | None = None,
        target_name: str = "",
        udid: str = "",
        capture_screenshots: bool = False,
        screenshot_interval_sec: float = 10.0,
    ) -> SessionState:
        bundle = bundle_id.strip() or "com.tencent.xin"
        selected_udid = udid.strip()
        selected_mode = mode.strip() or "ios"
        if selected_mode != "ios":
            raise ValueError("只支持 iOS USB 真实采集。")

        with self._lock:
            if self._running:
                self.stop()

            self._warnings = []
            real = RealIosCollector()
            try:
                real.assert_available()
            except RealIosCollectorUnavailable as exc:
                raise ValueError(str(exc)) from exc
            real.start(bundle, selected_udid)
            self._real = real

            self._udid = selected_udid
            self._bundle_id = bundle
            self._target_pid = target_pid
            self._target_name = target_name.strip()
            self._capture_screenshots = capture_screenshots
            self._screenshot_interval_sec = normalize_screenshot_interval(screenshot_interval_sec)
            self._mode = selected_mode
            self._samples = []
            self._screenshots.configure(self._screenshot_interval_sec)
            self._screenshots.reset()
            self._screenshots.set_udid(selected_udid)
            self._started_at = datetime.now().astimezone()
            self._running = True
            self._stop_event.clear()
            self._thread = threading.Thread(target=self._run, name="perf-session", daemon=True)
            self._thread.start()
            return self.state()

    def update_capture_options(self, capture_screenshots: bool, screenshot_interval_sec: float) -> SessionState:
        interval = normalize_screenshot_interval(screenshot_interval_sec)
        with self._lock:
            was_enabled = self._capture_screenshots
            self._capture_screenshots = capture_screenshots
            self._screenshot_interval_sec = interval
            self._screenshots.configure(interval)
            if capture_screenshots and not was_enabled:
                self._screenshots.request_next_capture()
        return self.state()

    def stop(self) -> SessionState:
        thread: threading.Thread | None
        with self._lock:
            self._running = False
            self._stop_event.set()
            thread = self._thread
            self._thread = None
            real = self._real
            self._real = None
        if thread and thread.is_alive():
            thread.join(timeout=2)
        if real:
            real.stop()
        return self.state()

    def clear(self) -> SessionState:
        self.stop()
        with self._lock:
            self._samples = []
            self._started_at = None
            self._warnings = []
            self._screenshots.reset()
        return self.state()

    def export_csv(self) -> str:
        with self._lock:
            samples = [sample.to_dict() for sample in self._samples]
        output = io.StringIO()
        writer = csv.DictWriter(
            output,
            fieldnames=[
                "timestamp",
                "elapsed_sec",
                "fps",
                "jank",
                "big_jank",
                "memory_mb",
                "screenshot_url",
                "source",
                "note",
            ],
            lineterminator="\n",
        )
        writer.writeheader()
        writer.writerows(samples)
        return output.getvalue()

    def _run(self) -> None:
        index = 0
        while not self._stop_event.is_set():
            with self._lock:
                started_at = self._started_at
                bundle_id = self._bundle_id
                target_pid = self._target_pid
                target_name = self._target_name
                mode = self._mode
                capture_screenshots = self._capture_screenshots
            if started_at is None:
                break
            sample = self._collect_sample(started_at, index, bundle_id, mode, target_pid, target_name)
            sample = self._attach_screenshot(sample, index, mode, capture_screenshots)
            with self._lock:
                if not self._running:
                    break
                self._samples.append(sample)
            index += 1
            time.sleep(1)

    def _collect_sample(
        self,
        started_at: datetime,
        index: int,
        bundle_id: str,
        mode: str,
        target_pid: int | None,
        target_name: str,
    ) -> Sample:
        with self._lock:
            real = self._real
        if real is None:
            raise RuntimeError("真实 iOS 采集器未启动。")
        return real.sample(started_at, index, bundle_id, target_pid, target_name)

    def _attach_screenshot(self, sample: Sample, index: int, mode: str, capture_screenshots: bool) -> Sample:
        if mode != "ios" or not capture_screenshots:
            return sample
        capture = self._screenshots.capture_due(sample.elapsed_sec, index)
        screenshot_url = capture.url if capture else self._screenshots.latest_url()
        if not screenshot_url:
            return sample
        return replace(sample, screenshot_url=screenshot_url)


def normalize_screenshot_interval(value: float) -> float:
    try:
        interval = float(value)
    except (TypeError, ValueError):
        interval = 10.0
    return max(5.0, min(300.0, interval))
