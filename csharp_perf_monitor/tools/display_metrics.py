from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Iterable


JANK_FRAME_TIME_MS = 1000.0 / 24.0 * 2.0
BIG_JANK_FRAME_TIME_MS = 1000.0 / 24.0 * 3.0
MAX_REASONABLE_FPS = 240.0
IOS_DISPLAY_FRAME_EVENT = (0x31, 0x80, 0xD1)
IOS_DISPLAY_FRAME_EVENT_ID = 0x31800344
IOS_DISPLAY_FRAME_EVENT_NAME = "IOMFB_V2_SWAP_ON_GLASS"


@dataclass(frozen=True)
class OrderedFrameMetrics:
    fps: float
    frame_count: int
    observation_ms: float
    frame_time_mean_ms: float
    frame_time_p95_ms: float
    frame_time_max_ms: float
    jank: float
    big_jank: float
    jank_time_ms: float
    stutter_percent: float


@dataclass(frozen=True)
class OrderedFrameWindow:
    metrics: OrderedFrameMetrics
    duplicate_timestamps: int
    out_of_order_timestamps: int
    invalid_intervals: int

    @property
    def source_degraded(self) -> bool:
        return bool(self.duplicate_timestamps or self.out_of_order_timestamps or self.invalid_intervals)

    @property
    def derived_metrics_available(self) -> bool:
        # Exact duplicates can be removed without changing the remaining order.
        # Out-of-order events and invalid gaps make the surrounding intervals ambiguous.
        return self.out_of_order_timestamps == 0 and self.invalid_intervals == 0


@dataclass
class IdleFrameWindowTracker:
    window_seconds: float
    last_frame_at: float | None = None
    last_zero_at: float | None = None

    def __post_init__(self) -> None:
        self.window_seconds = float(self.window_seconds)
        if not math.isfinite(self.window_seconds) or self.window_seconds <= 0:
            raise ValueError("window_seconds must be finite and positive")

    def mark_frame(self, now: float) -> None:
        current = float(now)
        if not math.isfinite(current):
            return
        self.last_frame_at = current
        self.last_zero_at = None

    def take_due_window(self, now: float) -> float | None:
        current = float(now)
        if not math.isfinite(current) or self.last_frame_at is None:
            return None
        window_started_at = self.last_zero_at if self.last_zero_at is not None else self.last_frame_at
        if current - self.last_frame_at < self.window_seconds:
            return None
        if current - window_started_at < self.window_seconds:
            return None
        self.last_zero_at = current
        return max(self.window_seconds, current - window_started_at)


class OrderedTimestampAccumulator:
    def __init__(
        self,
        tick_to_nanoseconds: float,
        window_ms: float,
        max_interval_ms: float = 60_000.0,
    ) -> None:
        factor = float(tick_to_nanoseconds)
        target_window_ms = float(window_ms)
        if not math.isfinite(factor) or factor <= 0:
            raise ValueError("tick_to_nanoseconds must be finite and positive")
        if not math.isfinite(target_window_ms) or target_window_ms <= 0:
            raise ValueError("window_ms must be finite and positive")

        self._tick_to_nanoseconds = factor
        self._window_ms = target_window_ms
        self._max_interval_ms = float(max_interval_ms)
        self._last_timestamp: float | None = None
        self._intervals_ms: list[float] = []
        self._history_ms: list[float] = []
        self._observation_ms = 0.0
        self._duplicates = 0
        self._out_of_order = 0
        self._invalid_intervals = 0
        self._last_event_accepted = False
        self._last_event_breaks_sequence = False

    @property
    def last_event_accepted(self) -> bool:
        return self._last_event_accepted

    @property
    def last_event_breaks_sequence(self) -> bool:
        return self._last_event_breaks_sequence

    def _discard_ambiguous_sequence(self) -> None:
        self._intervals_ms = []
        self._history_ms = []
        self._observation_ms = 0.0

    def reset_sequence(self) -> None:
        self._last_timestamp = None
        self._discard_ambiguous_sequence()
        self._duplicates = 0
        self._out_of_order = 0
        self._invalid_intervals = 0
        self._last_event_accepted = False
        self._last_event_breaks_sequence = False

    def add_timestamp(self, timestamp: float) -> OrderedFrameWindow | None:
        self._last_event_accepted = False
        self._last_event_breaks_sequence = False
        try:
            current = float(timestamp)
        except (TypeError, ValueError):
            self._invalid_intervals += 1
            self._last_timestamp = None
            self._discard_ambiguous_sequence()
            self._last_event_breaks_sequence = True
            return None
        if not math.isfinite(current):
            self._invalid_intervals += 1
            self._last_timestamp = None
            self._discard_ambiguous_sequence()
            self._last_event_breaks_sequence = True
            return None
        if self._last_timestamp is None:
            self._last_timestamp = current
            self._last_event_accepted = True
            return None
        if current == self._last_timestamp:
            self._duplicates += 1
            return None
        if current < self._last_timestamp:
            self._out_of_order += 1
            self._last_timestamp = None
            self._discard_ambiguous_sequence()
            self._last_event_breaks_sequence = True
            return None

        interval_ms = (current - self._last_timestamp) * self._tick_to_nanoseconds / 1_000_000.0
        self._last_timestamp = current
        if (
            not math.isfinite(interval_ms)
            or interval_ms <= 0
            or interval_ms > self._max_interval_ms
        ):
            self._invalid_intervals += 1
            self._discard_ambiguous_sequence()
            self._last_event_breaks_sequence = True
            return None

        self._last_event_accepted = True
        self._intervals_ms.append(interval_ms)
        self._observation_ms += interval_ms
        window_complete = self._observation_ms >= self._window_ms or math.isclose(
            self._observation_ms,
            self._window_ms,
            rel_tol=1e-12,
            abs_tol=1e-6,
        )
        if not window_complete:
            return None

        metrics = ordered_frame_metrics(self._intervals_ms, self._history_ms)
        if metrics is None:
            self._history_ms = []
            self._intervals_ms = []
            self._observation_ms = 0.0
            self._invalid_intervals += 1
            self._last_event_accepted = False
            self._last_event_breaks_sequence = True
            return None
        self._history_ms = recent_interval_history(self._history_ms, self._intervals_ms)
        window = OrderedFrameWindow(
            metrics=metrics,
            duplicate_timestamps=self._duplicates,
            out_of_order_timestamps=self._out_of_order,
            invalid_intervals=self._invalid_intervals,
        )
        self._intervals_ms = []
        self._observation_ms = 0.0
        self._duplicates = 0
        self._out_of_order = 0
        self._invalid_intervals = 0
        return window


def ordered_frame_payload(window: OrderedFrameWindow) -> dict[str, object]:
    metrics = window.metrics
    payload: dict[str, object] = {
        "fps": metrics.fps,
        "average_fps": metrics.fps,
        "frame_count": metrics.frame_count,
        "window_sec": metrics.observation_ms / 1000.0,
        "ordered_frames": True,
        "approximate": window.source_degraded,
        "source_degraded": window.source_degraded,
        "duplicate_timestamps": window.duplicate_timestamps,
        "out_of_order_timestamps": window.out_of_order_timestamps,
        "invalid_frame_intervals": window.invalid_intervals,
    }
    if window.derived_metrics_available:
        payload.update(
            {
                "jank": metrics.jank,
                "big_jank": metrics.big_jank,
                "jank_time_ms": metrics.jank_time_ms,
                "stutter_percent": metrics.stutter_percent,
                "frame_time_ms": metrics.frame_time_p95_ms,
                "frame_time_mean_ms": metrics.frame_time_mean_ms,
                "frame_time_p95_ms": metrics.frame_time_p95_ms,
                "frame_time_max_ms": metrics.frame_time_max_ms,
                "frame_time_aggregation": "p95",
            }
        )
    return payload


def ordered_zero_fps_payload(window_seconds: float) -> dict[str, object]:
    duration = float(window_seconds)
    if not math.isfinite(duration) or duration <= 0:
        raise ValueError("window_seconds must be finite and positive")
    return {
        "fps": 0.0,
        "average_fps": 0.0,
        "frame_count": 0,
        "window_sec": duration,
        "ordered_frames": True,
        "approximate": False,
        "source_degraded": False,
        "jank": 0.0,
        "big_jank": 0.0,
        "jank_time_ms": 0.0,
        "stutter_percent": 0.0,
        "idle_window": True,
        "no_present_frames": True,
    }


def valid_intervals(values: Iterable[float]) -> list[float]:
    result: list[float] = []
    for value in values:
        try:
            number = float(value)
        except (TypeError, ValueError):
            continue
        if math.isfinite(number) and number > 0:
            result.append(number)
    return result


def recent_interval_history(previous_intervals_ms: Iterable[float], intervals_ms: Iterable[float]) -> list[float]:
    values = valid_intervals(previous_intervals_ms)
    values.extend(valid_intervals(intervals_ms))
    return values[-3:]


def percentile(values: Iterable[float], quantile: float) -> float | None:
    ordered = sorted(valid_intervals(values))
    if not ordered:
        return None
    rank = max(1, min(len(ordered), math.ceil(len(ordered) * quantile)))
    return ordered[rank - 1]


def perfdog_jank_details(
    intervals_ms: Iterable[float],
    previous_intervals_ms: Iterable[float] | None = None,
) -> tuple[float, float, float]:
    previous = valid_intervals(previous_intervals_ms or [])[-3:]
    current = valid_intervals(intervals_ms)
    timeline = previous + current
    offset = len(previous)
    jank = 0.0
    big_jank = 0.0
    jank_time_ms = 0.0
    for index in range(offset, len(timeline)):
        if index < 3:
            continue
        frame_time = timeline[index]
        previous_three_avg = sum(timeline[index - 3 : index]) / 3.0
        if frame_time <= previous_three_avg * 2.0 or frame_time <= JANK_FRAME_TIME_MS:
            continue
        jank += 1.0
        jank_time_ms += frame_time
        if frame_time > BIG_JANK_FRAME_TIME_MS:
            big_jank += 1.0
    return jank, big_jank, jank_time_ms


def perfdog_jank_counts(
    intervals_ms: Iterable[float],
    previous_intervals_ms: Iterable[float] | None = None,
) -> tuple[float, float]:
    jank, big_jank, _ = perfdog_jank_details(intervals_ms, previous_intervals_ms)
    return jank, big_jank


def ordered_frame_metrics(
    intervals_ms: Iterable[float],
    previous_intervals_ms: Iterable[float] | None = None,
) -> OrderedFrameMetrics | None:
    values = valid_intervals(intervals_ms)
    if not values:
        return None
    observation_ms = sum(values)
    if not math.isfinite(observation_ms) or observation_ms <= 0:
        return None
    frame_count = len(values)
    mean_ms = observation_ms / frame_count
    fps = frame_count * 1000.0 / observation_ms
    if not math.isfinite(fps) or fps < 0 or fps > MAX_REASONABLE_FPS:
        return None
    p95_ms = percentile(values, 0.95)
    jank, big_jank, jank_time_ms = perfdog_jank_details(values, previous_intervals_ms)
    return OrderedFrameMetrics(
        fps=fps,
        frame_count=frame_count,
        observation_ms=observation_ms,
        frame_time_mean_ms=mean_ms,
        frame_time_p95_ms=p95_ms if p95_ms is not None else mean_ms,
        frame_time_max_ms=max(values),
        jank=jank,
        big_jank=big_jank,
        jank_time_ms=jank_time_ms,
        stutter_percent=jank_time_ms * 100.0 / observation_ms,
    )
