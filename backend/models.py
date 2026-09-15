from __future__ import annotations

from dataclasses import asdict, dataclass
from datetime import datetime
from typing import Any


@dataclass(frozen=True)
class Sample:
    timestamp: str
    elapsed_sec: float
    fps: float | None
    jank: int | None
    big_jank: int | None
    memory_mb: float | None
    screenshot_url: str
    source: str
    note: str

    def to_dict(self) -> dict[str, Any]:
        data = asdict(self)
        data["elapsed_sec"] = round(self.elapsed_sec, 2)
        data["fps"] = round(self.fps, 2) if self.fps is not None else None
        data["memory_mb"] = round(self.memory_mb, 2) if self.memory_mb is not None else None
        return data

    @property
    def has_fps(self) -> bool:
        return self.fps is not None

    @property
    def has_jank(self) -> bool:
        return self.jank is not None and self.big_jank is not None

    @property
    def has_memory(self) -> bool:
        return self.memory_mb is not None


@dataclass(frozen=True)
class SessionState:
    running: bool
    mode: str
    udid: str
    bundle_id: str
    target_pid: int | None
    target_name: str
    started_at: str | None
    elapsed_sec: float
    sample_count: int
    latest_screenshot_url: str
    latest_screenshot_error: str
    capture_screenshots: bool
    screenshot_interval_sec: float
    latest: dict[str, Any] | None
    warnings: list[str]


def now_iso() -> str:
    return datetime.now().astimezone().isoformat(timespec="seconds")
