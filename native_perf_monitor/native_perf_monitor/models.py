from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from backend.models import Sample


@dataclass(frozen=True)
class CaptureConfig:
    udid: str
    bundle_id: str
    target_pid: int | None
    target_name: str
    capture_screenshots: bool
    screenshot_interval_sec: float


@dataclass(frozen=True)
class NativeSample:
    timestamp: str
    elapsed_sec: float
    fps: float | None
    jank: int | None
    big_jank: int | None
    memory_mb: float | None
    source: str
    note: str

    @classmethod
    def from_backend(cls, sample: Sample) -> "NativeSample":
        return cls(
            timestamp=sample.timestamp,
            elapsed_sec=sample.elapsed_sec,
            fps=sample.fps,
            jank=sample.jank,
            big_jank=sample.big_jank,
            memory_mb=sample.memory_mb,
            source=sample.source,
            note=sample.note,
        )


@dataclass(frozen=True)
class NativeScreenshot:
    elapsed_sec: float
    path: Path
    timestamp: str
    error: str = ""
