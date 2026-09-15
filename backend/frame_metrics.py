from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class FrameEstimate:
    jank: int | None
    big_jank: int | None
    note: str


def estimate_jank_from_fps(fps: float | None, target_fps: float = 60.0) -> FrameEstimate:
    del fps, target_fps
    return FrameEstimate(
        jank=None,
        big_jank=None,
        note="缺少有序 Display FrameTime，Jank/BigJank 不可用。",
    )
