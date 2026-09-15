from __future__ import annotations

import math
import random
from datetime import datetime

from backend.models import Sample


class MockCollector:
    """Generates deterministic-looking samples for UI and export verification."""

    source = "mock"

    def __init__(self, seed: int | None = None) -> None:
        self._random = random.Random(seed)

    def sample(self, started_at: datetime, index: int, bundle_id: str) -> Sample:
        now = datetime.now().astimezone()
        elapsed = (now - started_at).total_seconds()
        wave = math.sin(index / 7.0)
        jitter = self._random.uniform(-2.8, 2.8)
        dip = 0.0
        jank = 0
        big_jank = 0

        if index > 0 and index % 17 == 0:
            dip = self._random.uniform(12.0, 22.0)
            jank = self._random.randint(1, 3)
        if index > 0 and index % 53 == 0:
            dip = self._random.uniform(24.0, 34.0)
            jank = self._random.randint(3, 6)
            big_jank = self._random.randint(1, 2)

        fps = max(8.0, min(60.0, 56.0 + wave * 2.2 + jitter - dip))
        memory = 720.0 + index * 1.15 + math.sin(index / 11.0) * 18.0
        memory += self._random.uniform(-6.0, 6.0)

        return Sample(
            timestamp=now.isoformat(timespec="seconds"),
            elapsed_sec=elapsed,
            fps=fps,
            jank=jank,
            big_jank=big_jank,
            memory_mb=max(0.0, memory),
            screenshot_url="",
            source=self.source,
            note=f"模拟数据：{bundle_id}",
        )
