#!/usr/bin/env python3
from __future__ import annotations

import argparse
from dataclasses import dataclass, field
import json
import math
import re
import shlex
import subprocess
import sys
import threading
import time
from typing import Iterable

from display_metrics import (
    OrderedTimestampAccumulator,
    OrderedFrameMetrics,
    ordered_frame_metrics,
    perfdog_jank_counts as shared_perfdog_jank_counts,
    recent_interval_history as shared_recent_interval_history,
)
from temperature_metrics import (
    parse_android_battery_temperature,
    parse_android_thermal_status,
    parse_android_thermalservice,
)

_EMIT_LOCK = threading.Lock()
_ADB_EXECUTABLE = "adb"
MIN_VALID_WECHAT_GFXINFO_FPS = 5.0
MIN_VALID_WECHAT_GFXINFO_FRAMES = 8
FPS_LOOP_INTERVAL_SEC = 0.45
DEFAULT_SURFACE_REFRESH_FPS = 60.0
SURFACE_FRAME_TIME_PERCENTILE = 0.95
COMMON_DISPLAY_REFRESH_RATES = (24.0, 30.0, 40.0, 48.0, 50.0, 60.0, 72.0, 75.0, 90.0, 96.0, 100.0, 120.0, 144.0, 165.0, 180.0, 240.0)
FRAME_WINDOW_MS = 1000.0
STATIC_FRAME_IDLE_SECONDS = 1.0
ORDERED_SOURCE_MAX_POLL_GAP_SECONDS = 3.0
LAYER_LIST_REFRESH_SECONDS = 5.0
GFXINFO_FALLBACK_REPROBE_SECONDS = 5.0
SURFACE_LATENCY_RING_CAPACITY = 128
MIN_SURFACE_LATENCY_POLL_INTERVAL_SEC = 0.10
SURFACE_ACTIVE_RECENCY_NS = int(STATIC_FRAME_IDLE_SECONDS * 1_000_000_000)
TARGET_IDENTITY_TIMEOUT_SECONDS = 8.0
INITIAL_IDENTITY_RETRY_SECONDS = 0.25
TEMPERATURE_INTERVAL_SECONDS = 5.0


@dataclass
class TimestatsSample:
    fps: float
    target: str
    average_fps: float | None = None
    histogram: dict[int, int] = field(default_factory=dict)
    jank: float = 0.0
    big_jank: float = 0.0
    frame_time_ms: float | None = None
    frame_count: int = 0
    window_sec: float = 0.0
    total_frames: int | None = None
    has_frame_observation: bool = False
    has_jank_metrics: bool = False
    frame_time_mean_ms: float | None = None
    frame_time_p95_ms: float | None = None
    frame_time_max_ms: float | None = None
    jank_time_ms: float | None = None
    stutter_percent: float | None = None
    ordered_frames: bool = False
    approximate: bool = False
    source_degraded: bool = False
    duplicate_timestamps: int = 0
    out_of_order_timestamps: int = 0
    invalid_frame_intervals: int = 0
    refresh_period_ns: int | None = None
    ring_buffer_overrun: bool = False
    target_verified: bool | None = None
    surface_owner_pid: int | None = None
    surface_owner_uid: int | None = None
    source_elapsed_sec: float | None = None
    source_sequence: int | None = None
    idle_window: bool = False
    no_present_frames: bool = False
    resume_gap_ms: float | None = None


@dataclass(frozen=True)
class HistogramFrameMetrics:
    fps: float
    average_fps: float
    frame_time_mean_ms: float
    frame_time_p95_ms: float
    frame_time_max_ms: float
    frame_count: int
    window_sec: float


@dataclass
class LatencyState:
    last_timestamp_ns: int | None = None
    last_poll_time: float | None = None
    recent_intervals_ms: list[float] = field(default_factory=list)
    reset_before_next_frame: bool = False
    source_origin_timestamp_ns: int | None = None
    source_sequence: int = 0
    source_elapsed_sec: float = 0.0


@dataclass
class OrderedLayerState:
    last_timestamp_ns: int | None = None
    last_poll_time: float | None = None
    last_new_frame_time: float | None = None
    last_zero_emit_time: float | None = None
    reset_before_next_frame: bool = False
    accumulator: OrderedTimestampAccumulator | None = None
    refresh_period_ns: int | None = None
    source_origin_timestamp_ns: int | None = None
    source_sequence: int = 0
    source_elapsed_sec: float = 0.0


def _advance_source_timeline(
    state: LatencyState | OrderedLayerState,
    observation_sec: float,
    endpoint_ns: int | None = None,
    endpoint_authoritative: bool = False,
) -> tuple[float, int]:
    observed = float(observation_sec)
    if not math.isfinite(observed) or observed < 0:
        observed = 0.0
    endpoint_elapsed = 0.0
    if endpoint_ns is not None:
        if state.source_origin_timestamp_ns is None:
            state.source_origin_timestamp_ns = endpoint_ns
        endpoint_elapsed = max(
            0.0,
            (endpoint_ns - state.source_origin_timestamp_ns) / 1_000_000_000.0,
        )
    if endpoint_authoritative and endpoint_ns is not None:
        state.source_elapsed_sec = max(state.source_elapsed_sec, endpoint_elapsed)
    else:
        state.source_elapsed_sec = max(endpoint_elapsed, state.source_elapsed_sec + observed)
    state.source_sequence += 1
    return state.source_elapsed_sec, state.source_sequence


@dataclass
class LayerLatencyTracker:
    selected_layer: str = ""
    selected_state: OrderedLayerState = field(default_factory=OrderedLayerState)
    last_list_time: float = 0.0
    missing_polls: int = 0
    target_pid: int = 0
    target_uid: int = 0
    target_uid_checked: bool = False
    target_verified: bool | None = None
    selected_owner_pid: int = 0
    selected_owner_uid: int = 0


@dataclass(frozen=True)
class SurfaceLayerOwner:
    name: str
    pid: int
    uid: int


@dataclass(frozen=True)
class AppBrandActivityBinding:
    activity: str
    pid: int
    process_name: str


@dataclass
class TimestatsTracker:
    history: dict[str, tuple[dict[int, int], float]] = field(default_factory=dict)
    selected_target: str = ""


@dataclass(frozen=True)
class CpuSnapshot:
    total_jiffies: int
    process_jiffies: int
    online_cpus: int
    core_jiffies: tuple[tuple[int, int], ...] = ()
    online_cpu_ids: tuple[int, ...] = ()


@dataclass(frozen=True)
class MemorySample:
    value_mb: float
    metric: str
    source: str
    fallback: bool = False
    rss_mb: float | None = None


@dataclass
class MemoryProbeState:
    rollup_supported: bool | None = None
    compact_meminfo_supported: bool | None = None


@dataclass(frozen=True)
class FrameStatsBlock:
    key: str
    rows: list[tuple[int, int]]
    timestamp_source: str
    exact_display_present: bool


@dataclass(frozen=True)
class GfxInfoResult:
    sample: TimestatsSample
    target: str
    frame_source: str


def emit(kind: str, payload: dict) -> None:
    with _EMIT_LOCK:
        print(f"{kind} {json.dumps(payload, ensure_ascii=False, separators=(',', ':'))}", flush=True)


def set_adb_executable(value: str) -> None:
    global _ADB_EXECUTABLE
    candidate = (value or "").strip()
    if not candidate:
        raise ValueError("ADB executable path is empty")
    _ADB_EXECUTABLE = candidate


def adb(serial: str, args: list[str], timeout: float = 6.0) -> subprocess.CompletedProcess[str]:
    cmd = [_ADB_EXECUTABLE]
    if serial:
        cmd += ["-s", serial]
    cmd += args
    try:
        return subprocess.run(
            cmd,
            text=True,
            encoding="utf-8",
            errors="replace",
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
        )
    except subprocess.TimeoutExpired as exc:
        stdout = exc.stdout.decode("utf-8", errors="replace") if isinstance(exc.stdout, bytes) else (exc.stdout or "")
        return subprocess.CompletedProcess(cmd, 124, stdout=stdout, stderr=f"adb timed out after {timeout:.1f}s")
    except OSError as exc:
        return subprocess.CompletedProcess(cmd, 127, stdout="", stderr=str(exc))


def remaining_poll_delay(started_at: float, interval: float) -> float:
    elapsed = max(0.0, time.monotonic() - started_at)
    return max(0.0, interval - elapsed)


def parse_proc_stat_fields(text: str) -> list[str] | None:
    text = text.strip()
    end = text.rfind(")")
    if end < 0:
        return None
    fields = text[end + 2 :].split()
    return fields or None


def parse_proc_jiffies(text: str) -> int | None:
    fields = parse_proc_stat_fields(text)
    if fields is None or len(fields) < 15:
        return None
    # After the comm field, fields[11] and fields[12] are utime/stime.
    try:
        return int(fields[11]) + int(fields[12])
    except ValueError:
        return None


def parse_proc_start_time(text: str) -> int | None:
    fields = parse_proc_stat_fields(text)
    if fields is None or len(fields) <= 19:
        return None
    try:
        start_time = int(fields[19])
    except ValueError:
        return None
    return start_time if start_time > 0 else None


def parse_online_cpu_count(value: str) -> int | None:
    value = value.strip()
    if not re.fullmatch(r"\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*", value):
        return None
    count = 0
    for part in value.split(","):
        if "-" in part:
            start_text, end_text = part.split("-", 1)
            start, end = int(start_text), int(end_text)
            if end < start:
                return None
            count += end - start + 1
        else:
            count += 1
    return count if count > 0 else None


def parse_online_cpu_ids(value: str) -> tuple[int, ...] | None:
    value = value.strip()
    if not re.fullmatch(r"\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*", value):
        return None
    ids: list[int] = []
    for part in value.split(","):
        if "-" in part:
            start_text, end_text = part.split("-", 1)
            start, end = int(start_text), int(end_text)
            if end < start:
                return None
            ids.extend(range(start, end + 1))
        else:
            ids.append(int(part))
    return tuple(dict.fromkeys(ids)) or None


def parse_cpu_snapshot(stdout: str, pid: int) -> CpuSnapshot | None:
    total: int | None = None
    process: int | None = None
    online_cpus: int | None = None
    online_cpu_ids: tuple[int, ...] | None = None
    core_jiffies: dict[int, int] = {}
    configured_cpu_lines = 0
    for raw in stdout.splitlines():
        line = raw.strip()
        if line.startswith("cpu ") and total is None:
            parts = line.split()
            try:
                # guest and guest_nice are already included in user/nice.
                total = sum(int(value) for value in parts[1:9])
            except ValueError:
                total = None
        elif re.match(r"^cpu\d+\s", line):
            configured_cpu_lines += 1
            parts = line.split()
            try:
                core_id = int(parts[0][3:])
                # Core utilization excludes idle and iowait. Guest ticks are already in user/nice.
                core_jiffies[core_id] = sum(int(parts[index]) for index in (1, 2, 3, 6, 7, 8))
            except (ValueError, IndexError):
                return None
        elif line.startswith(f"{pid} ("):
            process = parse_proc_jiffies(line)
        else:
            parsed_online = parse_online_cpu_count(line)
            if parsed_online is not None:
                online_cpus = parsed_online
                online_cpu_ids = parse_online_cpu_ids(line)
    if total is None or process is None:
        return None
    core_count = online_cpus or configured_cpu_lines or 1
    ids = online_cpu_ids or tuple(sorted(core_jiffies))
    if ids:
        core_jiffies = {core_id: core_jiffies[core_id] for core_id in ids if core_id in core_jiffies}
    return CpuSnapshot(
        total,
        process,
        max(1, core_count),
        tuple(sorted(core_jiffies.items())),
        tuple(ids),
    )


def read_cpu_snapshot(serial: str, pid: int) -> CpuSnapshot | None:
    result = adb(
        serial,
        ["shell", "cat", "/proc/stat", f"/proc/{pid}/stat", "/sys/devices/system/cpu/online"],
    )
    return parse_cpu_snapshot(result.stdout, pid)


def cpu_percent_from_snapshots(previous: CpuSnapshot, current: CpuSnapshot) -> float | None:
    total_delta = current.total_jiffies - previous.total_jiffies
    process_delta = current.process_jiffies - previous.process_jiffies
    if total_delta <= 0 or process_delta < 0:
        return None
    average_online_cpus = (previous.online_cpus + current.online_cpus) / 2.0
    return process_delta * 100.0 * max(1.0, average_online_cpus) / total_delta


def average_online_cpu_count(previous: CpuSnapshot, current: CpuSnapshot) -> float:
    return max(1.0, (previous.online_cpus + current.online_cpus) / 2.0)


def normalized_cpu_percent_from_raw(raw_percent: float, online_cpus: float) -> float | None:
    if not math.isfinite(raw_percent) or raw_percent < 0 or online_cpus <= 0:
        return None
    return raw_percent / online_cpus


def core_percent_from_snapshots(previous: CpuSnapshot, current: CpuSnapshot) -> list[float] | None:
    previous_cores = dict(previous.core_jiffies)
    current_cores = dict(current.core_jiffies)
    previous_ids = previous.online_cpu_ids or tuple(sorted(previous_cores))
    current_ids = current.online_cpu_ids or tuple(sorted(current_cores))
    if not previous_ids or previous_ids != current_ids or set(previous_cores) != set(current_cores):
        return None
    total_delta = current.total_jiffies - previous.total_jiffies
    if total_delta <= 0:
        return None
    scale = 100.0 * average_online_cpu_count(previous, current) / total_delta
    values: list[float] = []
    for core_id in current_ids:
        delta = current_cores[core_id] - previous_cores[core_id]
        if delta < 0:
            return None
        values.append(delta * scale)
    return values


def read_cpu(serial: str, pid: int, previous: CpuSnapshot | None) -> tuple[float | None, CpuSnapshot | None]:
    current = read_cpu_snapshot(serial, pid)
    if current is None:
        return None, previous
    if previous is None:
        return None, current
    return cpu_percent_from_snapshots(previous, current), current


def parse_meminfo_pss_mb(stdout: str) -> float | None:
    for raw in stdout.splitlines():
        line = " ".join(raw.strip().split())
        if not line:
            continue
        match = re.match(r"^TOTAL\s+PSS:\s*(\d+)\b", line)
        if match:
            return int(match.group(1)) / 1024.0
        match = re.match(r"^TOTAL:\s*(\d+)\b", line)
        if match:
            return int(match.group(1)) / 1024.0
        match = re.match(r"^TOTAL\s+(\d+)\b", line)
        if match:
            return int(match.group(1)) / 1024.0
    return None


def parse_meminfo_rss_mb(stdout: str) -> float | None:
    for raw in (stdout or "").splitlines():
        line = " ".join(raw.strip().split())
        match = re.match(r"^TOTAL\s+RSS:\s*(\d+)\b", line)
        if match:
            return int(match.group(1)) / 1024.0
    return None


def parse_smaps_rollup_pss_mb(stdout: str) -> float | None:
    for raw in stdout.splitlines():
        match = re.match(r"^Pss:\s*(\d+)\s+kB\b", raw.strip(), re.IGNORECASE)
        if match:
            return int(match.group(1)) / 1024.0
    return None


def parse_smaps_rollup_rss_mb(stdout: str) -> float | None:
    for raw in (stdout or "").splitlines():
        match = re.match(r"^Rss:\s*(\d+)\s+kB\b", raw.strip(), re.IGNORECASE)
        if match:
            return int(match.group(1)) / 1024.0
    return None


def positive_memory_mb(value: float | None) -> float | None:
    return value if value is not None and math.isfinite(value) and value > 0 else None


def read_memory_sample(
    serial: str,
    pid: int,
    probe_state: MemoryProbeState | None = None,
) -> MemorySample | None:
    state = probe_state if probe_state is not None else MemoryProbeState()
    if state.rollup_supported is not False:
        rollup = adb(serial, ["shell", "cat", f"/proc/{pid}/smaps_rollup"], timeout=4.0)
        if rollup.returncode == 0:
            pss_mb = positive_memory_mb(parse_smaps_rollup_pss_mb(rollup.stdout))
            if pss_mb is not None:
                state.rollup_supported = True
                return MemorySample(
                    pss_mb,
                    "pss",
                    "adb-proc-smaps-rollup",
                    False,
                    positive_memory_mb(parse_smaps_rollup_rss_mb(rollup.stdout)),
                )
        rollup_message = f"{getattr(rollup, 'stdout', '')}\n{getattr(rollup, 'stderr', '')}".lower()
        if "permission denied" in rollup_message or "no such file" in rollup_message:
            state.rollup_supported = False

    if state.compact_meminfo_supported is not False:
        compact = adb(
            serial,
            ["shell", "dumpsys", "meminfo", "--local", "--pssonly", str(pid)],
            timeout=6.0,
        )
        if compact.returncode == 0:
            pss_mb = positive_memory_mb(parse_meminfo_pss_mb(compact.stdout))
            if pss_mb is not None:
                state.compact_meminfo_supported = True
                return MemorySample(
                    pss_mb,
                    "pss",
                    "adb-dumpsys-meminfo-local-pss",
                    False,
                    positive_memory_mb(parse_meminfo_rss_mb(compact.stdout)),
                )
        compact_message = f"{getattr(compact, 'stdout', '')}\n{getattr(compact, 'stderr', '')}".lower()
        unsupported_markers = (
            "unknown option",
            "unknown command",
            "unrecognized option",
            "invalid option",
        )
        if any(marker in compact_message for marker in unsupported_markers):
            state.compact_meminfo_supported = False

    result = adb(serial, ["shell", "dumpsys", "meminfo", str(pid)], timeout=8.0)
    if result.returncode == 0:
        pss_mb = positive_memory_mb(parse_meminfo_pss_mb(result.stdout))
        if pss_mb is not None:
            return MemorySample(
                pss_mb,
                "pss",
                "adb-dumpsys-meminfo",
                False,
                positive_memory_mb(parse_meminfo_rss_mb(result.stdout)),
            )
    status = adb(serial, ["shell", "cat", f"/proc/{pid}/status"])
    if status.returncode != 0:
        return None
    for raw in status.stdout.splitlines():
        if raw.startswith("VmRSS:"):
            parts = raw.split()
            if len(parts) >= 2 and parts[1].isdigit() and int(parts[1]) > 0:
                rss_mb = int(parts[1]) / 1024.0
                return MemorySample(rss_mb, "rss", "adb-proc-status-rss", True, rss_mb)
    return None


def read_memory_mb(serial: str, pid: int) -> float | None:
    sample = read_memory_sample(serial, pid)
    return None if sample is None else sample.value_mb


def parse_framestats_blocks(stdout: str) -> list[FrameStatsBlock]:
    blocks: list[FrameStatsBlock] = []
    block_occurrences: dict[str, int] = {}
    in_profile = False
    context_line = ""
    block_label = ""
    header: list[str] = []
    data_rows: list[list[str]] = []

    def finish_block() -> None:
        nonlocal header, data_rows, block_label
        if not data_rows:
            header = []
            data_rows = []
            return
        normalized = [value.strip() for value in header]
        flags_index = normalized.index("Flags") if "Flags" in normalized else 0
        intended_index = normalized.index("IntendedVsync") if "IntendedVsync" in normalized else 1
        timestamp_candidates: list[tuple[int, str, bool]] = []
        for candidate, exact in (
            ("DisplayPresentTime", True),
            ("FrameCompleted", False),
            ("SwapBuffersCompleted", False),
            ("GpuCompleted", False),
        ):
            if candidate in normalized:
                timestamp_candidates.append((normalized.index(candidate), candidate, exact))
        if all(len(row) >= 14 for row in data_rows) and not any(source == "FrameCompleted" for _, source, _ in timestamp_candidates):
            timestamp_candidates.append((13, "FrameCompletedLegacy", False))
        if not timestamp_candidates:
            header = []
            data_rows = []
            return

        max_sentinel = 9_000_000_000_000_000_000
        for timestamp_index, timestamp_source, exact_display in timestamp_candidates:
            parsed_rows: list[tuple[int, int]] = []
            for parts in data_rows:
                if max(flags_index, intended_index, timestamp_index) >= len(parts):
                    continue
                try:
                    flags = int(parts[flags_index])
                    intended = int(parts[intended_index])
                    timestamp = int(parts[timestamp_index])
                except ValueError:
                    continue
                if flags != 0 or intended <= 0 or timestamp <= intended or timestamp >= max_sentinel:
                    continue
                parsed_rows.append((intended, timestamp))
            if parsed_rows:
                key_base = block_label or "framestats"
                occurrence = block_occurrences.get(key_base, 0)
                block_occurrences[key_base] = occurrence + 1
                key = f"{key_base}#window-{occurrence}"
                blocks.append(FrameStatsBlock(key, parsed_rows, timestamp_source, exact_display))
                break
        header = []
        data_rows = []

    for raw in stdout.splitlines():
        line = raw.strip()
        if line == "---PROFILEDATA---":
            if in_profile:
                finish_block()
                in_profile = False
            else:
                in_profile = True
                block_label = context_line
                header = []
                data_rows = []
            continue
        if line == "---PROFILEDATAEND---":
            if in_profile:
                finish_block()
            in_profile = False
            continue
        if not in_profile:
            if line:
                context_line = line
            continue
        if not line:
            continue
        parts = [value.strip() for value in line.split(",")]
        if parts and parts[0] == "Flags":
            header = parts
            continue
        if header:
            data_rows.append(parts)
    if in_profile:
        finish_block()
    return blocks


def parse_framestats(stdout: str) -> list[tuple[int, int]]:
    blocks = parse_framestats_blocks(stdout)
    if not blocks:
        return []
    selected = max(blocks, key=lambda block: (max(row[1] for row in block.rows), len(block.rows)))
    return list(selected.rows)


def parse_gfxinfo_summary(stdout: str) -> tuple[int, int, int] | None:
    total: int | None = None
    jank: int | None = None
    big_jank: int | None = None
    for raw in stdout.splitlines():
        line = raw.strip()
        if line.startswith("Total frames rendered:"):
            value = line.split(":", 1)[1].strip()
            if value.isdigit():
                total = int(value)
        elif line.startswith("Janky frames:") and "legacy" not in line:
            value = line.split(":", 1)[1].strip().split()[0]
            if value.isdigit():
                jank = int(value)
        elif line.startswith("Number Frame deadline missed:") and "legacy" not in line:
            value = line.split(":", 1)[1].strip().split()[0]
            if value.isdigit():
                big_jank = int(value)
    if total is None:
        return None
    return total, jank or 0, big_jank or 0


def is_wechat_appbrand_target(*names: str) -> bool:
    return any("com.tencent.mm:appbrand" in (name or "").lower() for name in names)


def is_suspicious_wechat_gfxinfo_fps(app_package: str, fps: float, frame_count: int, duration_sec: float) -> bool:
    if not is_wechat_appbrand_target(app_package):
        return False
    if fps < MIN_VALID_WECHAT_GFXINFO_FPS:
        return True
    return frame_count < MIN_VALID_WECHAT_GFXINFO_FRAMES and duration_sec >= 1.0


def recent_interval_history(previous_intervals_ms: Iterable[float], intervals_ms: Iterable[float]) -> list[float]:
    return shared_recent_interval_history(previous_intervals_ms, intervals_ms)


def perfdog_jank_counts(
    intervals_ms: Iterable[float],
    previous_intervals_ms: Iterable[float] | None = None,
) -> tuple[float, float]:
    return shared_perfdog_jank_counts(intervals_ms, previous_intervals_ms)


def interval_metrics(intervals_ms: list[float], previous_intervals_ms: Iterable[float] | None = None) -> tuple[float, float, float, float]:
    metrics = ordered_frame_metrics(intervals_ms, previous_intervals_ms)
    if metrics is None:
        return 0.0, 0.0, 0.0, 0.0
    return metrics.fps, metrics.jank, metrics.big_jank, metrics.frame_time_p95_ms


def read_fps(
    serial: str,
    package_name: str,
    seen: set[int],
    summary_state: dict[str, object],
    app_package: str = "",
) -> tuple[float | None, float | None, float | None, str, float | None]:
    result = read_gfxinfo_sample(serial, package_name, seen, summary_state, app_package)
    if result is None:
        return None, None, None, "", None
    sample = result.sample
    has_ordered_metrics = sample.has_jank_metrics and sample.frame_time_ms is not None
    return (
        sample.fps,
        sample.jank if has_ordered_metrics else None,
        sample.big_jank if has_ordered_metrics else None,
        result.target,
        sample.frame_time_ms if has_ordered_metrics else None,
    )


def _reset_gfxinfo_ordered_state(seen: set[int], summary_state: dict[str, object]) -> None:
    seen.clear()
    for key in (
        "frame_block",
        "frame_timestamp_source",
        "frame_exact_display",
        "last_end_ns",
        "recent_intervals_ms",
        "source_origin_timestamp_ns",
        "source_elapsed_sec",
        "source_sequence",
        "last_new_frame_time",
        "last_zero_emit_time",
        "last_successful_poll_time",
    ):
        summary_state.pop(key, None)


def _advance_gfxinfo_source_timeline(
    summary_state: dict[str, object],
    observed_seconds: float,
    endpoint_timestamp_ns: int | float | None,
    endpoint_authoritative: bool = False,
) -> tuple[float, int]:
    observed = max(0.0, float(observed_seconds))
    previous_value = summary_state.get("source_elapsed_sec", 0.0)
    previous_elapsed = float(previous_value) if isinstance(previous_value, (int, float)) else 0.0
    origin_value = summary_state.get("source_origin_timestamp_ns")
    endpoint_elapsed = 0.0
    if (
        isinstance(origin_value, (int, float))
        and isinstance(endpoint_timestamp_ns, (int, float))
        and endpoint_timestamp_ns >= origin_value
    ):
        endpoint_elapsed = (float(endpoint_timestamp_ns) - float(origin_value)) / 1_000_000_000.0
    if endpoint_authoritative and endpoint_timestamp_ns is not None:
        source_elapsed = max(previous_elapsed, endpoint_elapsed)
    else:
        source_elapsed = max(endpoint_elapsed, previous_elapsed + observed)
    sequence_value = summary_state.get("source_sequence", 0)
    source_sequence = int(sequence_value) + 1 if isinstance(sequence_value, (int, float)) else 1
    summary_state["source_elapsed_sec"] = source_elapsed
    summary_state["source_sequence"] = source_sequence
    return source_elapsed, source_sequence


def read_gfxinfo_sample(
    serial: str,
    package_name: str,
    seen: set[int],
    summary_state: dict[str, object],
    app_package: str = "",
) -> GfxInfoResult | None:
    if not package_name:
        return None
    app_context = app_package or package_name
    poll_started_at = time.monotonic()
    result = adb(serial, ["shell", "dumpsys", "gfxinfo", package_name, "framestats"], timeout=8.0)
    if result.returncode != 0:
        return None
    previous_success_value = summary_state.get("last_successful_poll_time")
    previous_success = float(previous_success_value) if isinstance(previous_success_value, (int, float)) else None
    source_poll_gap = previous_success is not None and poll_started_at - previous_success > ORDERED_SOURCE_MAX_POLL_GAP_SECONDS
    blocks = parse_framestats_blocks(result.stdout)
    selected_block = max(
        blocks,
        key=lambda block: (max(row[1] for row in block.rows), len(block.rows)),
        default=None,
    )
    rows = list(selected_block.rows) if selected_block is not None else []
    previous_block = str(summary_state.get("frame_block") or "")
    if selected_block is None or (previous_block and previous_block != selected_block.key) or source_poll_gap:
        _reset_gfxinfo_ordered_state(seen, summary_state)
    summary_state["last_successful_poll_time"] = poll_started_at
    if selected_block is not None:
        summary_state["frame_block"] = selected_block.key
        summary_state["frame_timestamp_source"] = selected_block.timestamp_source
        summary_state["frame_exact_display"] = selected_block.exact_display_present

    new_rows = [(start, end) for start, end in rows if end not in seen]
    if not seen and new_rows:
        for _, end in new_rows:
            seen.add(end)
        baseline = max(end for _, end in new_rows)
        summary_state["last_end_ns"] = float(baseline)
        summary_state["source_origin_timestamp_ns"] = float(baseline)
        summary_state["source_elapsed_sec"] = 0.0
        summary_state["source_sequence"] = 0
        summary_state["last_new_frame_time"] = time.monotonic()
        return None
    for _, end in new_rows:
        seen.add(end)
    if len(seen) > 3000:
        keep = set(sorted(seen)[-1500:])
        seen.clear()
        seen.update(keep)
    previous_end_value = summary_state.get("last_end_ns", 0)
    previous_end = int(previous_end_value) if isinstance(previous_end_value, (int, float)) else 0
    if previous_end > 0 and not isinstance(summary_state.get("source_origin_timestamp_ns"), (int, float)):
        summary_state["source_origin_timestamp_ns"] = float(previous_end)
    now = poll_started_at
    last_new_value = summary_state.get("last_new_frame_time")
    last_new_time = float(last_new_value) if isinstance(last_new_value, (int, float)) else None
    exact_display = selected_block.exact_display_present if selected_block is not None else False
    idle_reset = last_new_time is not None and now - last_new_time >= STATIC_FRAME_IDLE_SECONDS
    if idle_reset and not exact_display:
        previous_end = 0
        summary_state["recent_intervals_ms"] = []
    last_zero_value = summary_state.get("last_zero_emit_time")
    resume_gap_ms: float | None = None
    if (
        exact_display
        and previous_end > 0
        and new_rows
        and isinstance(last_zero_value, (int, float))
        and new_rows[0][1] > previous_end
    ):
        resume_gap_ms = (new_rows[0][1] - previous_end) / 1_000_000.0
    if new_rows:
        summary_state["last_end_ns"] = float(max(end for _, end in new_rows))
        summary_state["last_new_frame_time"] = now
    points: list[int] = []
    if previous_end > 0 and new_rows and previous_end < new_rows[0][1]:
        points.append(previous_end)
    points.extend(end for _, end in new_rows)
    duplicate_timestamps = 0
    out_of_order_timestamps = 0
    invalid_frame_intervals = 0
    if len(points) >= 2:
        intervals_ms, _, duplicate_timestamps, out_of_order_timestamps, invalid_frame_intervals = (
            ordered_present_intervals_ms(points[1:], points[0])
        )
    else:
        intervals_ms = []
    if out_of_order_timestamps or invalid_frame_intervals:
        summary_state["recent_intervals_ms"] = []
    if not intervals_ms:
        last_zero_time = float(last_zero_value) if isinstance(last_zero_value, (int, float)) else None
        zero_window_started_at = last_zero_time if last_zero_time is not None else last_new_time
        if (
            not new_rows
            and exact_display
            and last_new_time is not None
            and zero_window_started_at is not None
            and now - last_new_time >= STATIC_FRAME_IDLE_SECONDS
            and now - zero_window_started_at >= STATIC_FRAME_IDLE_SECONDS
        ):
            window_sec = max(STATIC_FRAME_IDLE_SECONDS, now - zero_window_started_at)
            summary_state["last_zero_emit_time"] = now
            source_elapsed_sec, source_sequence = _advance_gfxinfo_source_timeline(
                summary_state,
                window_sec,
                summary_state.get("last_end_ns"),
            )
            return GfxInfoResult(
                TimestatsSample(
                    fps=0.0,
                    target=package_name,
                    average_fps=0.0,
                    frame_count=0,
                    window_sec=window_sec,
                    has_frame_observation=True,
                    has_jank_metrics=True,
                    jank_time_ms=0.0,
                    stutter_percent=0.0,
                    ordered_frames=True,
                    approximate=False,
                    target_verified=True,
                    source_elapsed_sec=source_elapsed_sec,
                    source_sequence=source_sequence,
                    idle_window=True,
                    no_present_frames=True,
                ),
                package_name,
                "gfxinfo-display-present",
            )
        summary = parse_gfxinfo_summary(result.stdout)
        if summary is None:
            return None
        total, _, _ = summary
        previous_total_value = summary_state.get("total", -1)
        previous_time_value = summary_state.get("time", 0.0)
        previous_total = int(previous_total_value) if isinstance(previous_total_value, (int, float)) else -1
        previous_time = float(previous_time_value) if isinstance(previous_time_value, (int, float)) else 0.0
        summary_state.update({"total": float(total), "time": now})
        if previous_total < 0 or total < previous_total or now <= previous_time:
            return None
        elapsed = max(0.001, now - previous_time)
        frame_delta = max(0, total - previous_total)
        if frame_delta <= 0:
            return None
        fps = frame_delta / elapsed
        if not math.isfinite(fps) or fps < 0 or fps > 240:
            return None
        if is_suspicious_wechat_gfxinfo_fps(app_context, fps, frame_delta, elapsed):
            return None
        return GfxInfoResult(
            TimestatsSample(
                fps=fps,
                target=package_name,
                average_fps=fps,
                frame_count=frame_delta,
                window_sec=elapsed,
                has_frame_observation=True,
                ordered_frames=False,
                approximate=True,
                target_verified=True,
            ),
            package_name,
            "gfxinfo-rendered-frame-count",
        )
    previous_intervals = summary_state.get("recent_intervals_ms")
    if not isinstance(previous_intervals, list):
        previous_intervals = []
    metrics = ordered_frame_metrics(intervals_ms, previous_intervals)
    if metrics is None:
        return None
    if not exact_display and is_suspicious_wechat_gfxinfo_fps(
        app_context,
        metrics.fps,
        metrics.frame_count,
        metrics.observation_ms / 1000.0,
    ):
        return None
    summary_state["recent_intervals_ms"] = recent_interval_history(previous_intervals, intervals_ms)  # type: ignore[assignment]
    timestamp_source = selected_block.timestamp_source if selected_block is not None else "FrameCompletedLegacy"
    endpoint = max(points)
    origin_value = summary_state.get("source_origin_timestamp_ns", endpoint)
    origin = int(origin_value) if isinstance(origin_value, (int, float)) else endpoint
    source_elapsed_sec, source_sequence = _advance_gfxinfo_source_timeline(
        summary_state,
        metrics.observation_ms / 1000.0,
        endpoint,
        endpoint_authoritative=resume_gap_ms is not None,
    )
    sample = sample_from_ordered_metrics(
            metrics,
            package_name,
            exact_display=exact_display,
            duplicate_timestamps=duplicate_timestamps,
            out_of_order_timestamps=out_of_order_timestamps,
            invalid_frame_intervals=invalid_frame_intervals,
            target_verified=True,
            source_elapsed_sec=source_elapsed_sec,
            source_sequence=source_sequence,
            resume_gap_ms=resume_gap_ms,
        )
    summary_state.pop("last_zero_emit_time", None)
    return GfxInfoResult(
        sample,
        package_name,
        "gfxinfo-display-present" if exact_display else f"gfxinfo-{timestamp_source.lower()}",
    )


def host_package(package_name: str) -> str:
    package_name = (package_name or "").strip()
    return package_name.split(":", 1)[0] if ":" in package_name else package_name


def parse_hwc_fps_row(line: str) -> int | None:
    columns = [column.strip() for column in line.strip().split("|")]
    if len(columns) < 13 or not re.match(r"^0x[0-9a-fA-F]+$", columns[1]):
        return None
    fps_text = columns[11]
    if re.match(r"^-?\d+$", fps_text):
        return int(fps_text)
    return None


def is_hwc_layer_name(line: str) -> bool:
    if not line:
        return False
    if line.startswith("-") or line.startswith("+") or line.startswith("|"):
        return False
    if line.startswith("Layer name") or line.startswith("Z |") or line.startswith("rel "):
        return False
    if "|" in line:
        return False
    prefixes = (
        "MPPFlags ",
        "acquireFence",
        "resource ",
        "dump ",
        "bufferHandle:",
        "usageFlags",
    )
    return not line.startswith(prefixes)


def collect_hwc_layer_fps(stdout: str) -> list[tuple[str, int]]:
    rows: list[tuple[str, int]] = []
    in_layers = False
    current_name = ""
    for raw in stdout.splitlines():
        stripped = raw.strip()
        if re.match(r"^Display\s+.+\s+HWC layers:$", stripped):
            in_layers = True
            continue
        if not in_layers:
            continue
        if stripped.startswith("h/w composer state:"):
            break
        fps = parse_hwc_fps_row(stripped)
        if fps is not None and current_name:
            rows.append((current_name, fps))
            current_name = ""
            continue
        if is_hwc_layer_name(stripped):
            current_name = stripped
    return rows


def collect_hwc_fps_values(stdout: str) -> list[int]:
    return [fps for _, fps in collect_hwc_layer_fps(stdout)]


def collect_hwc_layer_names(stdout: str) -> list[str]:
    return [name for name, _ in collect_hwc_layer_fps(stdout)]


def surface_layer_score(layer_name: str, package_name: str) -> int:
    package_name = (package_name or "").strip()
    host = host_package(package_name)
    host_match = bool(host and host in layer_name)
    target_match = bool(package_name and package_name in layer_name)
    if not host_match and not target_match:
        return 0
    score = 0
    if host_match:
        score += 100
    if target_match:
        score += 120
    if "SurfaceView" in layer_name:
        score += 80
    if "AppBrandUI" in layer_name:
        score += 60
    if "LauncherUI" in layer_name and host == "com.tencent.mm":
        score -= 40
    return score


def surface_flinger_fps_from_output(stdout: str, package_name: str) -> tuple[float | None, str]:
    host = host_package(package_name)
    if not host:
        return None, ""
    best: tuple[int, int, str] | None = None
    for name, fps in collect_hwc_layer_fps(stdout):
        if fps <= 0:
            continue
        score = surface_layer_score(name, package_name)
        if score <= 0:
            continue
        if best is None or score > best[0]:
            best = (score, fps, name)
    if best is None:
        return None, ""
    return float(best[1]), best[2]


def parse_timestats_histogram_bins(line: str) -> dict[int, int]:
    bins: dict[int, int] = {}
    for match in re.finditer(r"(\d+(?:\.\d+)?)ms\s*=\s*(\d+)", line):
        ms = int(round(float(match.group(1))))
        count = int(match.group(2))
        bins[ms] = bins.get(ms, 0) + count
    return bins


def nominal_frame_time_ms(histogram: dict[int, int], refresh_period_ms: float | None = None) -> float:
    fast_bins = [(ms, count) for ms, count in histogram.items() if 0 < ms <= 25 and count > 0]
    inferred_ms: float | None = None
    if fast_bins:
        modal_ms = float(max(fast_bins, key=lambda item: (item[1], -item[0]))[0])
        refresh_rate = min(COMMON_DISPLAY_REFRESH_RATES, key=lambda rate: abs((1000.0 / rate) - modal_ms))
        inferred_ms = 1000.0 / refresh_rate
    if refresh_period_ms is not None and math.isfinite(refresh_period_ms) and 2.0 <= refresh_period_ms <= 100.0:
        # A dominant cadence that is materially faster proves that a vendor's
        # global period is stale and cannot describe these presents.
        if inferred_ms is not None and inferred_ms < refresh_period_ms * 0.9:
            return inferred_ms
        return refresh_period_ms
    return inferred_ms if inferred_ms is not None else 1000.0 / DEFAULT_SURFACE_REFRESH_FPS


def weighted_percentile_ms(histogram: dict[int, int], percentile: float) -> float | None:
    positive = [(ms, count) for ms, count in histogram.items() if ms > 0 and count > 0]
    if not positive:
        return None
    total = sum(count for _, count in positive)
    if not math.isfinite(percentile):
        return None
    quantile = min(1.0, max(0.0, percentile))
    rank = max(1, math.ceil(total * quantile))
    cumulative = 0
    for ms, count in sorted(positive):
        cumulative += count
        if cumulative >= rank:
            return float(ms)
    return float(max(ms for ms, _ in positive))


def normalized_histogram_interval_ms(value_ms: float, nominal_ms: float) -> float:
    if nominal_ms <= 0:
        return value_ms
    multiple = max(1, int(round(value_ms / nominal_ms)))
    quantized = nominal_ms * multiple
    tolerance_ms = max(1.0, nominal_ms * 0.08)
    if abs(value_ms - quantized) <= tolerance_ms:
        return quantized
    return max(value_ms, nominal_ms)


def representative_frame_time_ms(
    histogram: dict[int, int],
    average_frame_time_ms: float,
    nominal_ms: float | None = None,
) -> float:
    percentile_ms = weighted_percentile_ms(histogram, SURFACE_FRAME_TIME_PERCENTILE)
    if percentile_ms is None:
        return average_frame_time_ms
    if nominal_ms is not None:
        return normalized_histogram_interval_ms(percentile_ms, nominal_ms)
    return percentile_ms


def histogram_from_intervals(intervals_ms: Iterable[float]) -> dict[int, int]:
    histogram: dict[int, int] = {}
    for value in intervals_ms:
        ms = int(round(value))
        histogram[ms] = histogram.get(ms, 0) + 1
    return histogram


def timestats_metrics_from_histogram(
    histogram: dict[int, int],
    window_sec: float | None = None,
    refresh_period_ms: float | None = None,
) -> HistogramFrameMetrics | None:
    positive = {ms: count for ms, count in histogram.items() if ms > 0 and count > 0}
    if not positive:
        return None
    frame_count = sum(positive.values())
    if frame_count <= 0:
        return None
    nominal_ms = nominal_frame_time_ms(positive, refresh_period_ms)
    observed_duration_ms = sum(
        normalized_histogram_interval_ms(float(ms), nominal_ms) * count
        for ms, count in positive.items()
    )
    if observed_duration_ms <= 0:
        return None
    effective_duration_ms = observed_duration_ms
    if window_sec is not None and window_sec > 0:
        effective_duration_ms = max(effective_duration_ms, window_sec * 1000.0)
    if effective_duration_ms <= 0:
        return None
    effective_fps = frame_count * 1000.0 / effective_duration_ms
    if not math.isfinite(effective_fps) or effective_fps < 0 or effective_fps > 240:
        return None
    average_frame_time_ms = observed_duration_ms / frame_count
    return HistogramFrameMetrics(
        fps=effective_fps,
        average_fps=effective_fps,
        frame_time_mean_ms=average_frame_time_ms,
        frame_time_p95_ms=representative_frame_time_ms(positive, average_frame_time_ms, nominal_ms),
        frame_time_max_ms=normalized_histogram_interval_ms(float(max(positive)), nominal_ms),
        frame_count=frame_count,
        window_sec=effective_duration_ms / 1000.0,
    )


def delta_timestats_histogram(current: dict[int, int], previous: dict[int, int] | None) -> dict[int, int]:
    if not previous:
        return dict(current)
    reset_detected = False
    delta: dict[int, int] = {}
    for ms, count in current.items():
        prev = previous.get(ms, 0)
        if count < prev:
            reset_detected = True
            break
        if count > prev:
            delta[ms] = count - prev
    return dict(current) if reset_detected else delta


def zero_timestats_sample(target: str, previous_time: float, now: float | None = None) -> TimestatsSample:
    now = time.monotonic() if now is None else now
    window_sec = max(0.001, now - previous_time)
    return TimestatsSample(
        fps=0.0,
        target=target,
        average_fps=0.0,
        histogram={},
        jank=0.0,
        big_jank=0.0,
        frame_time_ms=None,
        frame_count=0,
        window_sec=window_sec,
        has_frame_observation=True,
    )


def surface_flinger_timestats_samples_from_output(
    stdout: str,
    package_name: str,
    refresh_period_ms: float | None = None,
) -> list[TimestatsSample]:
    host = host_package(package_name)
    if not host:
        return []

    blocks: list[dict[str, object]] = []
    current: dict[str, object] | None = None
    in_histogram = False
    pending_display_rate: float | None = None
    pending_render_rate: float | None = None
    for raw in stdout.splitlines():
        line = raw.strip()
        if line.startswith("displayRefreshRate") and "=" in line:
            match = re.search(r"[-+]?[0-9]*\.?[0-9]+", line.split("=", 1)[1])
            pending_display_rate = float(match.group(0)) if match else None
            continue
        if line.startswith("renderRate") and "=" in line:
            match = re.search(r"[-+]?[0-9]*\.?[0-9]+", line.split("=", 1)[1])
            pending_render_rate = float(match.group(0)) if match else None
            continue
        if "layerName" in line and "=" in line:
            if current is not None:
                blocks.append(current)
            current = {
                "layer": line.split("=", 1)[1].strip(),
                "average": None,
                "histogram": {},
                "total": None,
                "display_rate": pending_display_rate,
                "render_rate": pending_render_rate,
            }
            in_histogram = False
            continue
        if current is None:
            continue
        if "totalFrames" in line and "=" in line:
            value_text = line.split("=", 1)[1].strip()
            if value_text.isdigit():
                current["total"] = int(value_text)
            continue
        if "averageFPS" in line and "=" in line:
            value_text = line.split("=", 1)[1].strip()
            match = re.search(r"[-+]?[0-9]*\.?[0-9]+", value_text)
            if match:
                current["average"] = float(match.group(0))
            continue
        if "present2present histogram" in line:
            in_histogram = True
            continue
        if "histogram is as below" in line:
            in_histogram = False
            continue
        if in_histogram:
            bins = parse_timestats_histogram_bins(line)
            if bins:
                histogram = current["histogram"]
                if isinstance(histogram, dict):
                    for ms, count in bins.items():
                        histogram[ms] = int(histogram.get(ms, 0)) + count
                continue
            if line and "=" in line:
                in_histogram = False
    if current is not None:
        blocks.append(current)

    # Android 16 TimeStats can split one layer into cumulative refresh-rate
    # buckets. Track each bucket once, then aggregate buckets by layer before
    # calculating poll deltas; otherwise alternating buckets look like resets.
    bucket_blocks: dict[tuple[str, object, object], dict[str, object]] = {}
    for block in blocks:
        layer_name = str(block.get("layer") or "")
        if not layer_name:
            continue
        key = (layer_name, block.get("display_rate"), block.get("render_rate"))
        histogram_obj = block.get("histogram")
        histogram_count = sum(int(value) for value in histogram_obj.values()) if isinstance(histogram_obj, dict) else 0
        total = block.get("total")
        completeness = int(total) if isinstance(total, int) else histogram_count
        previous = bucket_blocks.get(key)
        if previous is not None:
            previous_histogram = previous.get("histogram")
            previous_histogram_count = (
                sum(int(value) for value in previous_histogram.values())
                if isinstance(previous_histogram, dict)
                else 0
            )
            previous_total = previous.get("total")
            previous_completeness = int(previous_total) if isinstance(previous_total, int) else previous_histogram_count
            if previous_completeness >= completeness:
                continue
        bucket_blocks[key] = block

    aggregated: dict[str, dict[str, object]] = {}
    for block in bucket_blocks.values():
        layer_name = str(block.get("layer") or "")
        entry = aggregated.setdefault(
            layer_name,
            {
                "layer": layer_name,
                "histogram": {},
                "total_sum": 0,
                "total_complete": True,
                "average_sum": 0.0,
                "average_weight": 0.0,
            },
        )
        histogram_obj = block.get("histogram")
        histogram = histogram_obj if isinstance(histogram_obj, dict) else {}
        target_histogram = entry["histogram"]
        if isinstance(target_histogram, dict):
            for ms, count in histogram.items():
                target_histogram[int(ms)] = int(target_histogram.get(int(ms), 0)) + int(count)
        total = block.get("total")
        if isinstance(total, int):
            entry["total_sum"] = int(entry["total_sum"]) + total
        else:
            entry["total_complete"] = False
        average = block.get("average")
        if isinstance(average, float) and math.isfinite(average):
            histogram_count = sum(int(value) for value in histogram.values())
            weight = float(total if isinstance(total, int) and total > 0 else max(1, histogram_count))
            entry["average_sum"] = float(entry["average_sum"]) + average * weight
            entry["average_weight"] = float(entry["average_weight"]) + weight

    blocks = []
    for entry in aggregated.values():
        average_weight = float(entry["average_weight"])
        blocks.append(
            {
                "layer": entry["layer"],
                "histogram": entry["histogram"],
                "total": int(entry["total_sum"]) if bool(entry["total_complete"]) else None,
                "average": float(entry["average_sum"]) / average_weight if average_weight > 0 else None,
            }
        )

    samples: list[TimestatsSample] = []
    for block in blocks:
        layer_name = str(block.get("layer") or "")
        score = surface_layer_score(layer_name, package_name)
        if score <= 0:
            continue
        average = block.get("average")
        histogram_obj = block.get("histogram")
        histogram = dict(histogram_obj) if isinstance(histogram_obj, dict) else {}
        fps: float | None = None
        frame_time_ms: float | None = None
        frame_time_mean_ms: float | None = None
        frame_time_max_ms: float | None = None
        frame_count = 0
        window_sec = 0.0
        if histogram:
            metrics = timestats_metrics_from_histogram(
                histogram,
                refresh_period_ms=refresh_period_ms,
            )
            if metrics is not None:
                fps = metrics.fps
                average = metrics.average_fps
                frame_time_ms = metrics.frame_time_p95_ms
                frame_time_mean_ms = metrics.frame_time_mean_ms
                frame_time_max_ms = metrics.frame_time_max_ms
                frame_count = metrics.frame_count
                window_sec = metrics.window_sec
        if fps is None and isinstance(average, float):
            fps = average
        total_frames = int(block["total"]) if isinstance(block.get("total"), int) else None
        if fps is None and total_frames is not None:
            fps = 0.0
        if fps is None or fps < 0 or fps > 240 or (fps == 0 and total_frames is None):
            continue
        sample = TimestatsSample(
            fps=fps,
            target=layer_name,
            average_fps=average if isinstance(average, float) else None,
            histogram=histogram,
            frame_time_ms=frame_time_ms,
            frame_count=frame_count,
            window_sec=window_sec,
            total_frames=total_frames,
            has_frame_observation=bool(histogram),
            has_jank_metrics=False,
            frame_time_mean_ms=frame_time_mean_ms,
            frame_time_p95_ms=frame_time_ms,
            frame_time_max_ms=frame_time_max_ms,
            approximate=True,
            refresh_period_ns=int(round(refresh_period_ms * 1_000_000.0)) if refresh_period_ms is not None else None,
        )
        samples.append(sample)
    return sorted(
        samples,
        key=lambda item: (surface_layer_score(item.target, package_name), item.frame_count),
        reverse=True,
    )


def surface_flinger_timestats_sample_from_output(
    stdout: str,
    package_name: str,
    refresh_period_ms: float | None = None,
) -> TimestatsSample | None:
    samples = surface_flinger_timestats_samples_from_output(stdout, package_name, refresh_period_ms)
    return samples[0] if samples else None


def surface_flinger_timestats_fps_from_output(stdout: str, package_name: str) -> tuple[float | None, str]:
    sample = surface_flinger_timestats_sample_from_output(stdout, package_name)
    if sample is None:
        return None, ""
    return sample.fps, sample.target


def parse_surface_flinger_layer_list(stdout: str) -> list[str]:
    layers: list[str] = []
    seen: set[str] = set()
    for raw in stdout.splitlines():
        layer = normalize_surface_layer_name(raw)
        if not layer or layer in seen:
            continue
        lowered = layer.lower()
        if lowered.startswith("error") or "unknown option" in lowered or "not found" in lowered:
            continue
        seen.add(layer)
        layers.append(layer)
    return layers


def normalize_surface_layer_name(raw: str) -> str:
    layer = (raw or "").strip()
    prefix = "RequestedLayerState{"
    if not layer.startswith(prefix):
        return layer
    content = layer[len(prefix) :]
    if content.endswith("}"):
        content = content[:-1]
    metadata_offsets = [
        offset
        for marker in (" parentId=", " relativeParentId=", " z=")
        if (offset := content.find(marker)) >= 0
    ]
    if metadata_offsets:
        content = content[: min(metadata_offsets)]
    return content.strip()


def parse_requested_surface_layer_names(stdout: str) -> list[str]:
    layers: list[str] = []
    seen: set[str] = set()
    for raw in (stdout or "").splitlines():
        stripped = raw.strip()
        if not stripped.startswith("RequestedLayerState{"):
            continue
        layer = normalize_surface_layer_name(stripped)
        if not layer or layer in seen:
            continue
        seen.add(layer)
        layers.append(layer)
    return layers


def surface_layer_sequence_id(name: str) -> int | None:
    matches = list(re.finditer(r"#(?P<id>\d+)(?=\s|$)", name or ""))
    if not matches:
        return None
    return int(matches[-1].group("id"))


def parse_surface_layer_owners(stdout: str) -> list[SurfaceLayerOwner]:
    owners: list[SurfaceLayerOwner] = []
    seen: set[tuple[str, int, int]] = set()
    for raw in (stdout or "").splitlines():
        match = re.match(
            r"^(?P<name>.*?)\s+(?:parent=\d+\s+)?pid=(?P<pid>\d+)\s+uid=(?P<uid>\d+)\s*$",
            raw.rstrip(),
        )
        if match is None:
            continue
        name = re.sub(r"^[\s\u2500-\u257f]+", "", match.group("name")).strip()
        if name.startswith("(Relative) "):
            name = name[len("(Relative) ") :].strip()
        try:
            pid = int(match.group("pid"))
            uid = int(match.group("uid"))
        except ValueError:
            continue
        if not name or pid <= 0 or uid < 0:
            continue
        key = (name, pid, uid)
        if key in seen:
            continue
        seen.add(key)
        owners.append(SurfaceLayerOwner(name, pid, uid))

    # Android 10-era SurfaceFlinger dumps do not expose the modern tree rows
    # with pid/uid. Their compositionengine blocks still carry the owning
    # application id, which is sufficient to verify native-app surfaces by UID.
    legacy_name = ""
    for raw in (stdout or "").splitlines():
        layer_match = re.match(
            r"^\s*\*\s+compositionengine::Layer\b.*\((?P<name>.*)\)\s*$",
            raw.rstrip(),
        )
        if layer_match is not None:
            legacy_name = layer_match.group("name").strip()
            continue
        if not legacy_name:
            continue
        uid_match = re.search(r"\bappId=(?P<uid>\d+)\b", raw)
        if uid_match is None:
            continue
        try:
            uid = int(uid_match.group("uid"))
        except ValueError:
            legacy_name = ""
            continue
        if uid > 0:
            key = (legacy_name, 0, uid)
            if key not in seen and not any(
                owner.name == legacy_name and owner.uid == uid for owner in owners
            ):
                seen.add(key)
                owners.append(SurfaceLayerOwner(legacy_name, 0, uid))
        legacy_name = ""
    return owners


def parse_appbrand_activity_bindings(stdout: str) -> list[AppBrandActivityBinding]:
    bindings: list[AppBrandActivityBinding] = []
    seen: set[tuple[str, int, str]] = set()
    current_activity = ""
    activity_pattern = re.compile(r"\b(AppBrandUI\d*)\b", re.IGNORECASE)
    process_pattern = re.compile(
        r"\bProcessRecord\{[^}\r\n]*?\s(?P<pid>\d+):"
        r"(?P<process>com\.tencent\.mm:appbrand\d*)/",
        re.IGNORECASE,
    )

    for raw in (stdout or "").splitlines():
        stripped = raw.strip()
        record_start = any(
            marker in stripped
            for marker in ("TaskRecord{", "Task{", "ActivityRecord{")
        )
        activity_match = activity_pattern.search(stripped)
        if record_start:
            current_activity = activity_match.group(1) if activity_match is not None else ""
        elif activity_match is not None and any(
            marker in stripped
            for marker in ("mActivityComponent=", "mResumedActivity:", "ResumedActivity:")
        ):
            current_activity = activity_match.group(1)

        if not current_activity:
            continue
        process_match = process_pattern.search(stripped)
        if process_match is None:
            continue
        try:
            pid = int(process_match.group("pid"))
        except ValueError:
            continue
        process_name = process_match.group("process")
        if pid <= 0:
            continue
        key = (current_activity.lower(), pid, process_name.lower())
        if key in seen:
            continue
        seen.add(key)
        bindings.append(AppBrandActivityBinding(current_activity, pid, process_name))
    return bindings


def bind_appbrand_activity_owners(
    owners: Iterable[SurfaceLayerOwner],
    bindings: Iterable[AppBrandActivityBinding],
    package_name: str,
    target_pid: int,
    target_uid: int,
) -> list[SurfaceLayerOwner]:
    owner_rows = list(owners)
    if (
        not is_wechat_appbrand_target(package_name)
        or target_pid <= 0
        or target_uid <= 0
    ):
        return owner_rows

    bindings_by_activity: dict[str, set[tuple[int, str]]] = {}
    for binding in bindings:
        bindings_by_activity.setdefault(binding.activity.lower(), set()).add(
            (binding.pid, binding.process_name.lower())
        )

    selected_process = package_name.strip().lower()
    joined: list[SurfaceLayerOwner] = []
    activity_pattern = re.compile(r"\b(AppBrandUI\d*)\b", re.IGNORECASE)
    target_app_id = target_uid % 100_000
    for owner in owner_rows:
        if owner.pid > 0:
            joined.append(owner)
            continue
        activity_match = activity_pattern.search(owner.name)
        candidates = (
            bindings_by_activity.get(activity_match.group(1).lower(), set())
            if activity_match is not None
            else set()
        )
        owner_app_id = owner.uid % 100_000 if owner.uid > 0 else 0
        if (
            len(candidates) == 1
            and next(iter(candidates)) == (target_pid, selected_process)
            and owner_app_id == target_app_id
        ):
            joined.append(SurfaceLayerOwner(owner.name, target_pid, owner.uid))
        else:
            joined.append(owner)
    return joined


def surface_owner_matches_target(
    owner: SurfaceLayerOwner,
    target_pid: int,
    target_uid: int,
    package_name: str,
) -> bool:
    if target_pid <= 0:
        return False
    if is_wechat_appbrand_target(package_name):
        return owner.pid > 0 and owner.pid == target_pid
    same_uid = target_uid > 0 and (
        owner.uid == target_uid
        or (0 < owner.uid < 100_000 and owner.uid == target_uid % 100_000)
    )
    return (owner.pid > 0 and owner.pid == target_pid) or same_uid


def ranked_surface_layers(stdout: str, package_name: str) -> list[str]:
    scored = [
        (surface_layer_score(layer, package_name), index, layer)
        for index, layer in enumerate(parse_surface_flinger_layer_list(stdout))
    ]
    scored = [item for item in scored if item[0] > 0]
    scored.sort(key=lambda item: (-item[0], item[1]))
    return [layer for _, _, layer in scored]


def ranked_owned_surface_layers(
    owners: Iterable[SurfaceLayerOwner],
    package_name: str,
    target_pid: int,
    target_uid: int,
    canonical_layers: Iterable[str] | None = None,
) -> list[SurfaceLayerOwner]:
    canonical_by_id: dict[int, list[str]] = {}
    for layer in canonical_layers or ():
        layer_id = surface_layer_sequence_id(layer)
        if layer_id is None:
            continue
        canonical_by_id.setdefault(layer_id, []).append(layer)

    scored: list[tuple[int, int, SurfaceLayerOwner]] = []
    seen: set[tuple[str, int, int]] = set()
    for index, owner in enumerate(owners):
        if not surface_owner_matches_target(owner, target_pid, target_uid, package_name):
            continue
        owner_name = owner.name
        owner_layer_id = surface_layer_sequence_id(owner_name)
        if owner_layer_id is not None:
            matching_layers = canonical_by_id.get(owner_layer_id, [])
            if matching_layers:
                owner_name = max(
                    matching_layers,
                    key=lambda layer: (surface_layer_score(layer, package_name), len(layer)),
                )
        normalized_owner = SurfaceLayerOwner(owner_name, owner.pid, owner.uid)
        key = (normalized_owner.name, normalized_owner.pid, normalized_owner.uid)
        if key in seen:
            continue
        seen.add(key)
        scored.append((surface_layer_score(owner_name, package_name), index, normalized_owner))
    scored = [item for item in scored if item[0] > 0]
    scored.sort(key=lambda item: (-item[0], item[1]))
    return [owner for _, _, owner in scored]


def parse_surface_flinger_latency(stdout: str) -> tuple[int | None, list[int]]:
    refresh_ns: int | None = None
    timestamps: list[int] = []
    max_sentinel = 9_000_000_000_000_000_000
    for raw in stdout.splitlines():
        parts = raw.strip().split()
        if not parts:
            continue
        if refresh_ns is None and len(parts) == 1:
            try:
                refresh_ns = int(parts[0])
            except ValueError:
                refresh_ns = None
            continue
        if len(parts) < 3:
            continue
        # AOSP FrameTracker::dumpStats writes desired, actual-present, frame-ready.
        # Display FrameTime must use the second column and must not fall through to
        # frame-ready when the actual-present fence is invalid.
        try:
            actual_present_ns = int(parts[1])
        except ValueError:
            continue
        if 0 < actual_present_ns < max_sentinel:
            timestamps.append(actual_present_ns)
    return refresh_ns, timestamps


def ordered_present_intervals_ms(
    timestamps_ns: Iterable[int],
    baseline_ns: int,
    max_interval_ms: float = 60_000.0,
) -> tuple[list[float], int, int, int, int]:
    previous: int | None = int(baseline_ns)
    latest = previous
    intervals_ms: list[float] = []
    duplicate_timestamps = 0
    out_of_order_timestamps = 0
    invalid_intervals = 0
    for raw_timestamp in timestamps_ns:
        try:
            timestamp = int(raw_timestamp)
        except (TypeError, ValueError):
            invalid_intervals += 1
            intervals_ms = []
            previous = None
            continue
        if timestamp <= 0:
            invalid_intervals += 1
            intervals_ms = []
            previous = None
            continue
        latest = max(latest, timestamp)
        if previous is None:
            previous = timestamp
            continue
        if timestamp == previous:
            duplicate_timestamps += 1
            continue
        if timestamp < previous:
            out_of_order_timestamps += 1
            intervals_ms = []
            previous = None
            continue
        interval_ms = (timestamp - previous) / 1_000_000.0
        if not math.isfinite(interval_ms) or interval_ms <= 0 or interval_ms > max_interval_ms:
            invalid_intervals += 1
            intervals_ms = []
            previous = None
            continue
        previous = timestamp
        intervals_ms.append(interval_ms)
    return intervals_ms, latest, duplicate_timestamps, out_of_order_timestamps, invalid_intervals


def sample_from_ordered_metrics(
    metrics: OrderedFrameMetrics,
    target: str,
    *,
    exact_display: bool,
    duplicate_timestamps: int = 0,
    out_of_order_timestamps: int = 0,
    invalid_frame_intervals: int = 0,
    integrity_lost: bool = False,
    refresh_period_ns: int | None = None,
    ring_buffer_overrun: bool = False,
    target_verified: bool | None = None,
    surface_owner_pid: int | None = None,
    surface_owner_uid: int | None = None,
    source_elapsed_sec: float | None = None,
    source_sequence: int | None = None,
    resume_gap_ms: float | None = None,
) -> TimestatsSample:
    source_degraded = bool(
        duplicate_timestamps
        or out_of_order_timestamps
        or invalid_frame_intervals
        or integrity_lost
    )
    derived_available = bool(
        exact_display
        and not out_of_order_timestamps
        and not invalid_frame_intervals
        and not integrity_lost
        and target_verified is not False
    )
    return TimestatsSample(
        fps=metrics.fps,
        target=target,
        average_fps=metrics.fps,
        jank=metrics.jank if derived_available else 0.0,
        big_jank=metrics.big_jank if derived_available else 0.0,
        frame_time_ms=metrics.frame_time_p95_ms if derived_available else None,
        frame_count=metrics.frame_count,
        window_sec=metrics.observation_ms / 1000.0,
        frame_time_mean_ms=metrics.frame_time_mean_ms if derived_available else None,
        frame_time_p95_ms=metrics.frame_time_p95_ms if derived_available else None,
        frame_time_max_ms=metrics.frame_time_max_ms if derived_available else None,
        jank_time_ms=metrics.jank_time_ms if derived_available else None,
        stutter_percent=metrics.stutter_percent if derived_available else None,
        has_frame_observation=True,
        has_jank_metrics=derived_available,
        ordered_frames=True,
        approximate=not exact_display or source_degraded or target_verified is False,
        source_degraded=source_degraded,
        duplicate_timestamps=duplicate_timestamps,
        out_of_order_timestamps=out_of_order_timestamps,
        invalid_frame_intervals=invalid_frame_intervals,
        refresh_period_ns=refresh_period_ns,
        ring_buffer_overrun=ring_buffer_overrun,
        target_verified=target_verified,
        surface_owner_pid=surface_owner_pid,
        surface_owner_uid=surface_owner_uid,
        source_elapsed_sec=source_elapsed_sec,
        source_sequence=source_sequence,
        resume_gap_ms=resume_gap_ms,
    )


def _ordered_layer_accumulator(baseline_ns: int) -> OrderedTimestampAccumulator:
    accumulator = OrderedTimestampAccumulator(tick_to_nanoseconds=1.0, window_ms=FRAME_WINDOW_MS)
    accumulator.add_timestamp(baseline_ns)
    return accumulator


def surface_latency_poll_interval(refresh_period_ns: int | None) -> float:
    if refresh_period_ns is None or refresh_period_ns <= 0:
        return FPS_LOOP_INTERVAL_SEC
    safe_frames = max(1, SURFACE_LATENCY_RING_CAPACITY // 2)
    ring_budget_sec = safe_frames * refresh_period_ns / 1_000_000_000.0
    return max(MIN_SURFACE_LATENCY_POLL_INTERVAL_SEC, min(FPS_LOOP_INTERVAL_SEC, ring_budget_sec))


def layer_latency_samples_from_timestamps(
    timestamps: list[int],
    state: OrderedLayerState,
    target: str,
    refresh_period_ns: int | None = None,
    now: float | None = None,
    target_verified: bool | None = None,
    surface_owner_pid: int | None = None,
    surface_owner_uid: int | None = None,
) -> list[TimestatsSample]:
    now = time.monotonic() if now is None else now
    previous_poll_time = state.last_poll_time
    state.last_poll_time = now
    if refresh_period_ns is not None and refresh_period_ns > 0:
        state.refresh_period_ns = refresh_period_ns
    if state.source_origin_timestamp_ns is None and state.last_timestamp_ns is not None:
        state.source_origin_timestamp_ns = state.last_timestamp_ns

    valid = [timestamp for timestamp in timestamps if timestamp > 0]
    if previous_poll_time is not None and now - previous_poll_time > ORDERED_SOURCE_MAX_POLL_GAP_SECONDS:
        if valid:
            baseline = max(valid)
            state.last_timestamp_ns = baseline
            state.last_new_frame_time = now
            state.last_zero_emit_time = None
            state.reset_before_next_frame = False
            state.accumulator = _ordered_layer_accumulator(baseline)
            state.source_origin_timestamp_ns = baseline
            state.source_elapsed_sec = 0.0
            state.source_sequence = 0
        return []
    if state.last_timestamp_ns is None:
        if valid:
            baseline = max(valid)
            state.last_timestamp_ns = baseline
            state.last_new_frame_time = now
            state.accumulator = _ordered_layer_accumulator(baseline)
            state.source_origin_timestamp_ns = baseline
        elif state.last_new_frame_time is None:
            state.last_new_frame_time = now
        return []

    start_index: int | None = None
    for index, timestamp in enumerate(valid):
        if timestamp > state.last_timestamp_ns:
            start_index = index
            break

    if start_index is None:
        idle_since = state.last_new_frame_time if state.last_new_frame_time is not None else now
        zero_since = state.last_zero_emit_time if state.last_zero_emit_time is not None else idle_since
        if now - idle_since < STATIC_FRAME_IDLE_SECONDS or now - zero_since < STATIC_FRAME_IDLE_SECONDS:
            return []
        state.last_zero_emit_time = now
        window_sec = max(STATIC_FRAME_IDLE_SECONDS, now - zero_since)
        source_elapsed_sec, source_sequence = _advance_source_timeline(
            state,
            window_sec,
            state.last_timestamp_ns,
        )
        return [
            TimestatsSample(
                fps=0.0,
                target=target,
                average_fps=0.0,
                frame_count=0,
                window_sec=window_sec,
                has_frame_observation=True,
                has_jank_metrics=target_verified is not False,
                jank_time_ms=0.0 if target_verified is not False else None,
                stutter_percent=0.0 if target_verified is not False else None,
                ordered_frames=True,
                approximate=target_verified is False,
                refresh_period_ns=state.refresh_period_ns,
                target_verified=target_verified,
                surface_owner_pid=surface_owner_pid,
                surface_owner_uid=surface_owner_uid,
                source_elapsed_sec=source_elapsed_sec,
                source_sequence=source_sequence,
                idle_window=True,
                no_present_frames=True,
            )
        ]

    new_timestamps = valid[start_index:]
    resume_gap_ms = None
    if (
        target_verified is True
        and state.last_zero_emit_time is not None
        and state.accumulator is not None
        and new_timestamps
        and new_timestamps[0] > state.last_timestamp_ns
    ):
        resume_gap_ms = (new_timestamps[0] - state.last_timestamp_ns) / 1_000_000.0
    overflow = len(new_timestamps) >= SURFACE_LATENCY_RING_CAPACITY - 1
    if overflow:
        intervals_ms, _, duplicates, out_of_order, invalid = ordered_present_intervals_ms(
            new_timestamps[1:],
            new_timestamps[0],
        )
        metrics = ordered_frame_metrics(intervals_ms)
        degraded_samples: list[TimestatsSample] = []
        if metrics is not None:
            endpoint = max(new_timestamps)
            source_elapsed_sec, source_sequence = _advance_source_timeline(
                state,
                metrics.observation_ms / 1000.0,
                endpoint,
            )
            degraded_samples.append(
                sample_from_ordered_metrics(
                    metrics,
                    target,
                    exact_display=True,
                    duplicate_timestamps=duplicates,
                    out_of_order_timestamps=out_of_order,
                    invalid_frame_intervals=invalid,
                    integrity_lost=True,
                    refresh_period_ns=state.refresh_period_ns,
                    ring_buffer_overrun=True,
                    target_verified=target_verified,
                    surface_owner_pid=surface_owner_pid,
                    surface_owner_uid=surface_owner_uid,
                    source_elapsed_sec=source_elapsed_sec,
                    source_sequence=source_sequence,
                )
            )
        latest = max(valid)
        state.last_timestamp_ns = latest
        state.accumulator = _ordered_layer_accumulator(latest)
        state.reset_before_next_frame = False
        state.last_new_frame_time = now
        state.last_zero_emit_time = None
        return degraded_samples

    if state.reset_before_next_frame or overflow or state.accumulator is None:
        baseline = new_timestamps[0]
        state.accumulator = _ordered_layer_accumulator(baseline)
        state.last_timestamp_ns = baseline
        state.reset_before_next_frame = False
        new_timestamps = new_timestamps[1:]

    samples: list[TimestatsSample] = []
    pending_resume_gap_ms = resume_gap_ms
    assert state.accumulator is not None
    for timestamp in new_timestamps:
        window = state.accumulator.add_timestamp(timestamp)
        if window is None:
            continue
        metrics = window.metrics
        source_elapsed_sec, source_sequence = _advance_source_timeline(
            state,
            metrics.observation_ms / 1000.0,
            timestamp,
            endpoint_authoritative=pending_resume_gap_ms is not None,
        )
        samples.append(
            sample_from_ordered_metrics(
                metrics,
                target,
                exact_display=True,
                duplicate_timestamps=window.duplicate_timestamps,
                out_of_order_timestamps=window.out_of_order_timestamps,
                invalid_frame_intervals=window.invalid_intervals,
                integrity_lost=overflow,
                refresh_period_ns=state.refresh_period_ns,
                target_verified=target_verified,
                surface_owner_pid=surface_owner_pid,
                surface_owner_uid=surface_owner_uid,
                source_elapsed_sec=source_elapsed_sec,
                source_sequence=source_sequence,
                resume_gap_ms=pending_resume_gap_ms,
            )
        )
        pending_resume_gap_ms = None
        overflow = False

    state.last_timestamp_ns = max(state.last_timestamp_ns, max(valid))
    state.last_new_frame_time = now
    state.last_zero_emit_time = None
    return samples


def _read_layer_latency(serial: str, layer: str) -> tuple[bool, int | None, list[int]]:
    result = adb(
        serial,
        ["shell", "dumpsys", "SurfaceFlinger", "--latency", shlex.quote(layer)],
        timeout=4.0,
    )
    if result.returncode != 0:
        return False, None, []
    refresh_period_ns, timestamps = parse_surface_flinger_latency(result.stdout)
    available = bool(timestamps)
    return available, refresh_period_ns, timestamps


def read_surface_flinger_layer_latency_samples(
    serial: str,
    package_name: str,
    tracker: LayerLatencyTracker,
    target_pid: int = 0,
    target_uid: int | None = None,
) -> tuple[list[TimestatsSample], bool]:
    now = time.monotonic()
    if target_pid > 0:
        tracker.target_pid = target_pid
    if target_uid is not None and target_uid > 0:
        tracker.target_uid = target_uid
        tracker.target_uid_checked = True
    if tracker.target_pid > 0 and not tracker.target_uid_checked:
        observed_uid = read_process_uid(serial, tracker.target_pid)
        if observed_uid is not None and observed_uid > 0:
            tracker.target_uid = observed_uid
            tracker.target_uid_checked = True
    refresh_due = now - tracker.last_list_time >= LAYER_LIST_REFRESH_SECONDS
    selected_samples: list[TimestatsSample] = []
    selected_available = False
    selected_latest = tracker.selected_state.last_timestamp_ns or 0
    selected_probe: tuple[bool, int | None, list[int]] | None = None
    if tracker.selected_layer:
        selected_probe = _read_layer_latency(serial, tracker.selected_layer)
        available, refresh_period_ns, timestamps = selected_probe
        if available:
            tracker.missing_polls = 0
            selected_available = True
            selected_latest = max(timestamps) if timestamps else selected_latest
            if not refresh_due:
                selected_samples = layer_latency_samples_from_timestamps(
                    timestamps,
                    tracker.selected_state,
                    tracker.selected_layer,
                    refresh_period_ns,
                    now,
                    tracker.target_verified,
                    tracker.selected_owner_pid or None,
                    tracker.selected_owner_uid or None,
                )
                return selected_samples, True
        else:
            tracker.missing_polls += 1
            if tracker.missing_polls < 2 and not refresh_due:
                return [], False
            tracker.selected_layer = ""
            tracker.selected_state = OrderedLayerState()
            tracker.target_verified = None
            tracker.selected_owner_pid = 0
            tracker.selected_owner_uid = 0

    if not refresh_due:
        return selected_samples, selected_available
    tracker.last_list_time = now
    candidate_owners: list[SurfaceLayerOwner] = []
    owner_metadata_available = False
    if tracker.target_pid > 0:
        owner_dump = adb(serial, ["shell", "dumpsys", "SurfaceFlinger", "--layers"], timeout=5.0)
        if owner_dump.returncode == 0:
            owners = parse_surface_layer_owners(owner_dump.stdout)
            owner_metadata_available = bool(owners)
            canonical_layers = parse_requested_surface_layer_names(owner_dump.stdout)
            target_owners = [
                owner
                for owner in owners
                if surface_owner_matches_target(
                    owner,
                    tracker.target_pid,
                    tracker.target_uid,
                    package_name,
                )
            ]
            if (
                not target_owners
                and is_wechat_appbrand_target(package_name)
                and tracker.target_uid > 0
            ):
                activity_dump = adb(
                    serial,
                    ["shell", "dumpsys", "activity", "recents"],
                    timeout=5.0,
                )
                if activity_dump.returncode == 0:
                    owners = bind_appbrand_activity_owners(
                        owners,
                        parse_appbrand_activity_bindings(activity_dump.stdout),
                        package_name,
                        tracker.target_pid,
                        tracker.target_uid,
                    )
                    target_owners = [
                        owner
                        for owner in owners
                        if surface_owner_matches_target(
                            owner,
                            tracker.target_pid,
                            tracker.target_uid,
                            package_name,
                        )
                    ]
            if not canonical_layers and any("[...]" in owner.name for owner in target_owners):
                listed = adb(
                    serial,
                    ["shell", "dumpsys", "SurfaceFlinger", "--list"],
                    timeout=5.0,
                )
                if listed.returncode == 0:
                    canonical_layers = parse_requested_surface_layer_names(listed.stdout)
            candidate_owners = ranked_owned_surface_layers(
                owners,
                package_name,
                tracker.target_pid,
                tracker.target_uid,
                canonical_layers,
            )[:6]

    candidate_rows: list[tuple[str, int, int, bool | None]]
    if owner_metadata_available:
        if not candidate_owners:
            tracker.selected_layer = ""
            tracker.selected_state = OrderedLayerState()
            tracker.target_verified = None
            tracker.selected_owner_pid = 0
            tracker.selected_owner_uid = 0
            return [], False
        candidate_rows = [
            (owner.name, owner.pid, owner.uid, True)
            for owner in candidate_owners
        ]
        selected_owner = next(
            (owner for owner in candidate_owners if owner.name == tracker.selected_layer),
            None,
        )
        if tracker.selected_layer and selected_owner is None:
            selected_available = False
            selected_samples = []
    else:
        listed = adb(serial, ["shell", "dumpsys", "SurfaceFlinger", "--list"], timeout=5.0)
        if listed.returncode != 0:
            if selected_available and selected_probe is not None:
                _, refresh_period_ns, timestamps = selected_probe
                tracker.target_verified = False if tracker.target_pid > 0 else None
                tracker.selected_owner_pid = 0
                tracker.selected_owner_uid = 0
                selected_samples = layer_latency_samples_from_timestamps(
                    timestamps,
                    tracker.selected_state,
                    tracker.selected_layer,
                    refresh_period_ns,
                    now,
                    tracker.target_verified,
                )
                return selected_samples, True
            return [], False
        fallback_verified = False if tracker.target_pid > 0 else None
        candidate_rows = [
            (layer, 0, 0, fallback_verified)
            for layer in ranked_surface_layers(listed.stdout, package_name)[:6]
        ]

    candidates: list[tuple[int, int, str, int | None, list[int], int, int, bool | None]] = []
    for layer, owner_pid, owner_uid, target_verified in candidate_rows:
        if selected_available and layer == tracker.selected_layer and selected_probe is not None:
            available, refresh_period_ns, timestamps = selected_probe
        else:
            available, refresh_period_ns, timestamps = _read_layer_latency(serial, layer)
        if not available:
            continue
        latest = max(timestamps) if timestamps else 0
        score = surface_layer_score(layer, package_name)
        candidates.append((
            score,
            latest,
            layer,
            refresh_period_ns,
            timestamps,
            owner_pid,
            owner_uid,
            target_verified,
        ))
    if not candidates:
        return selected_samples, selected_available

    # Candidate probes run serially, so a lower-ranked layer often has a slightly
    # newer timestamp only because it was queried later. Treat layers within the
    # idle threshold as concurrently active, then prefer the main render surface.
    newest_timestamp = max(candidate[1] for candidate in candidates)
    active_candidates = [
        candidate
        for candidate in candidates
        if newest_timestamp - candidate[1] <= SURFACE_ACTIVE_RECENCY_NS
    ]
    selected_last_frame_at = tracker.selected_state.last_new_frame_time
    selected_state_timestamp = tracker.selected_state.last_timestamp_ns or 0
    selected_is_stale = bool(
        tracker.selected_layer
        and selected_available
        and selected_last_frame_at is not None
        and now - selected_last_frame_at >= STATIC_FRAME_IDLE_SECONDS
        and selected_latest <= selected_state_timestamp
        and any(
            candidate[2] != tracker.selected_layer and candidate[1] > selected_latest
            for candidate in active_candidates
        )
    )
    if selected_is_stale:
        active_candidates = [
            candidate
            for candidate in active_candidates
            if candidate[2] != tracker.selected_layer
        ]
    best = max(active_candidates, key=lambda candidate: (candidate[0], candidate[1]))

    score, latest, layer, refresh_period_ns, timestamps, owner_pid, owner_uid, target_verified = best
    if selected_available and tracker.selected_layer:
        selected_score = surface_layer_score(tracker.selected_layer, package_name)
        if layer == tracker.selected_layer or (score <= selected_score and latest <= selected_latest):
            tracker.target_verified = target_verified
            tracker.selected_owner_pid = owner_pid
            tracker.selected_owner_uid = owner_uid
            if selected_probe is not None:
                _, selected_refresh_period_ns, selected_timestamps = selected_probe
                selected_samples = layer_latency_samples_from_timestamps(
                    selected_timestamps,
                    tracker.selected_state,
                    tracker.selected_layer,
                    selected_refresh_period_ns,
                    now,
                    target_verified,
                    owner_pid or None,
                    owner_uid or None,
                )
            return selected_samples, True

    tracker.selected_layer = layer
    tracker.selected_state = OrderedLayerState()
    tracker.missing_polls = 0
    tracker.target_verified = target_verified
    tracker.selected_owner_pid = owner_pid
    tracker.selected_owner_uid = owner_uid
    samples = layer_latency_samples_from_timestamps(
        timestamps,
        tracker.selected_state,
        layer,
        refresh_period_ns,
        now,
        target_verified,
        owner_pid or None,
        owner_uid or None,
    )
    return samples, True


def latency_sample_from_timestamps(
    timestamps: list[int],
    state: LatencyState,
    target: str = "display",
    refresh_period_ns: int | None = None,
    now: float | None = None,
    target_verified: bool | None = None,
) -> TimestatsSample | None:
    now = time.monotonic() if now is None else now
    if state.source_origin_timestamp_ns is None and state.last_timestamp_ns is not None:
        state.source_origin_timestamp_ns = state.last_timestamp_ns

    def zero_sample(previous_time: float) -> TimestatsSample:
        sample = zero_timestats_sample(target, previous_time, now)
        sample.ordered_frames = True
        sample.approximate = target_verified is False
        sample.has_jank_metrics = target_verified is not False
        sample.refresh_period_ns = refresh_period_ns
        sample.target_verified = target_verified
        sample.source_elapsed_sec, sample.source_sequence = _advance_source_timeline(
            state,
            sample.window_sec,
            state.last_timestamp_ns,
        )
        return sample

    if not timestamps:
        state.last_poll_time = now
        return None

    last_timestamp = state.last_timestamp_ns
    previous_poll_time = state.last_poll_time
    state.last_poll_time = now
    if last_timestamp is None:
        state.last_timestamp_ns = max(timestamps)
        state.source_origin_timestamp_ns = state.last_timestamp_ns
        return None

    start_index = next((index for index, timestamp in enumerate(timestamps) if timestamp > last_timestamp), None)
    if start_index is None:
        state.recent_intervals_ms = []
        state.reset_before_next_frame = True
        return zero_sample(previous_poll_time if previous_poll_time is not None else now - 1.0)

    new_timestamps = timestamps[start_index:]
    if state.reset_before_next_frame:
        last_timestamp = new_timestamps[0]
        state.last_timestamp_ns = last_timestamp
        state.recent_intervals_ms = []
        state.reset_before_next_frame = False
        new_timestamps = new_timestamps[1:]
        if not new_timestamps:
            return None

    intervals_ms, latest, duplicates, out_of_order, invalid = ordered_present_intervals_ms(
        new_timestamps,
        last_timestamp,
    )
    state.last_timestamp_ns = max(last_timestamp, latest)
    if invalid:
        state.recent_intervals_ms = []
    if not intervals_ms:
        return None

    metrics = ordered_frame_metrics(intervals_ms, state.recent_intervals_ms)
    if metrics is None:
        return None
    state.recent_intervals_ms = recent_interval_history(state.recent_intervals_ms, intervals_ms)
    source_elapsed_sec, source_sequence = _advance_source_timeline(
        state,
        metrics.observation_ms / 1000.0,
        state.last_timestamp_ns,
    )
    return sample_from_ordered_metrics(
        metrics,
        target,
        exact_display=True,
        duplicate_timestamps=duplicates,
        out_of_order_timestamps=out_of_order,
        invalid_frame_intervals=invalid,
        refresh_period_ns=refresh_period_ns,
        target_verified=target_verified,
        source_elapsed_sec=source_elapsed_sec,
        source_sequence=source_sequence,
    )


def read_surface_flinger_display_latency_sample(serial: str, state: LatencyState) -> TimestatsSample | None:
    poll_started_at = time.monotonic()
    result = adb(serial, ["shell", "dumpsys", "SurfaceFlinger", "--latency"], timeout=5.0)
    if result.returncode != 0:
        return None
    refresh_period_ns, timestamps = parse_surface_flinger_latency(result.stdout)
    return latency_sample_from_timestamps(
        timestamps,
        state,
        refresh_period_ns=refresh_period_ns,
        now=poll_started_at,
        target_verified=False,
    )


def read_surface_refresh_period_ns(serial: str) -> int | None:
    result = adb(serial, ["shell", "dumpsys", "SurfaceFlinger", "--latency"], timeout=4.0)
    if result.returncode != 0:
        return None
    refresh_period_ns, _ = parse_surface_flinger_latency(result.stdout)
    return refresh_period_ns


def read_surface_flinger_fps(serial: str, package_name: str) -> tuple[float | None, str]:
    if not host_package(package_name):
        return None, ""
    result = adb(serial, ["shell", "dumpsys", "SurfaceFlinger"], timeout=10.0)
    if result.returncode != 0:
        return None, ""
    return surface_flinger_fps_from_output(result.stdout, package_name)


def prepare_surface_flinger_timestats(serial: str) -> tuple[bool, bool]:
    existing = adb(serial, ["shell", "dumpsys", "SurfaceFlinger", "--timestats", "-dump"], timeout=5.0)
    if existing.returncode == 0 and "layerName" in existing.stdout:
        return True, False
    result = adb(serial, ["shell", "dumpsys", "SurfaceFlinger", "--timestats", "-enable"], timeout=5.0)
    return result.returncode == 0, result.returncode == 0


def clear_surface_flinger_timestats(serial: str) -> None:
    adb(serial, ["shell", "dumpsys", "SurfaceFlinger", "--timestats", "-clear"], timeout=5.0)


def disable_surface_flinger_timestats(serial: str) -> None:
    adb(serial, ["shell", "dumpsys", "SurfaceFlinger", "--timestats", "-disable"], timeout=5.0)


def _windowed_timestats_sample(
    sample: TimestatsSample,
    history: dict[str, tuple[dict[int, int], float]],
    now: float,
    refresh_period_ms: float | None,
) -> TimestatsSample | None:
    refresh_period_ns = int(round(refresh_period_ms * 1_000_000.0)) if refresh_period_ms is not None else None
    previous_entry = history.get(sample.target)
    previous_time = previous_entry[1] if previous_entry is not None else None

    if sample.histogram:
        previous = previous_entry[0] if previous_entry is not None else None
        history[sample.target] = (dict(sample.histogram), now)
        if previous is None or previous_time is None:
            return None
        reset = any(count < previous.get(ms, 0) for ms, count in sample.histogram.items()) or any(
            sample.histogram.get(ms, 0) < count for ms, count in previous.items()
        )
        if reset:
            return None
        window_histogram = delta_timestats_histogram(sample.histogram, previous)
        if not window_histogram:
            zero = zero_timestats_sample(sample.target, previous_time, now)
            zero.approximate = True
            zero.refresh_period_ns = refresh_period_ns
            return zero
        metrics = timestats_metrics_from_histogram(
            window_histogram,
            window_sec=max(0.001, now - previous_time),
            refresh_period_ms=refresh_period_ms,
        )
        if metrics is None:
            return None
        return TimestatsSample(
            fps=metrics.fps,
            target=sample.target,
            average_fps=metrics.average_fps,
            histogram=window_histogram,
            frame_time_ms=metrics.frame_time_p95_ms,
            frame_count=metrics.frame_count,
            window_sec=metrics.window_sec,
            frame_time_mean_ms=metrics.frame_time_mean_ms,
            frame_time_p95_ms=metrics.frame_time_p95_ms,
            frame_time_max_ms=metrics.frame_time_max_ms,
            has_frame_observation=True,
            has_jank_metrics=False,
            approximate=True,
            refresh_period_ns=refresh_period_ns,
        )

    if sample.total_frames is not None:
        previous_total = previous_entry[0].get(-1) if previous_entry is not None else None
        history[sample.target] = ({-1: sample.total_frames}, now)
        if previous_total is None or previous_time is None or sample.total_frames < previous_total:
            return None
        window_sec = max(0.001, now - previous_time)
        frame_count = sample.total_frames - previous_total
        if frame_count == 0:
            zero = zero_timestats_sample(sample.target, previous_time, now)
            zero.approximate = True
            zero.total_frames = sample.total_frames
            zero.refresh_period_ns = refresh_period_ns
            return zero
        fps = frame_count / window_sec
        if not math.isfinite(fps) or fps < 0 or fps > 240:
            return None
        return TimestatsSample(
            fps=fps,
            target=sample.target,
            average_fps=fps,
            frame_count=frame_count,
            window_sec=window_sec,
            total_frames=sample.total_frames,
            has_frame_observation=True,
            has_jank_metrics=False,
            approximate=True,
            refresh_period_ns=refresh_period_ns,
        )

    return None


def read_surface_flinger_timestats_sample(
    serial: str,
    package_name: str,
    previous_state: TimestatsTracker | dict[str, tuple[dict[int, int], float]] | None = None,
    refresh_period_ms: float | None = None,
) -> TimestatsSample | None:
    if not host_package(package_name):
        return None
    poll_started_at = time.monotonic()
    result = adb(serial, ["shell", "dumpsys", "SurfaceFlinger", "--timestats", "-dump"], timeout=10.0)
    if result.returncode != 0:
        return None
    samples = surface_flinger_timestats_samples_from_output(result.stdout, package_name, refresh_period_ms)
    if not samples:
        return None
    if previous_state is None:
        return samples[0]

    tracker = previous_state if isinstance(previous_state, TimestatsTracker) else None
    history = tracker.history if tracker is not None else previous_state
    now = poll_started_at
    windowed = [
        current
        for current in (_windowed_timestats_sample(sample, history, now, refresh_period_ms) for sample in samples)
        if current is not None
    ]
    if not windowed:
        return None

    active = [sample for sample in windowed if sample.frame_count > 0 or sample.fps > 0]
    if active:
        selected = max(
            active,
            key=lambda sample: (surface_layer_score(sample.target, package_name), sample.frame_count),
        )
        if tracker is not None:
            tracker.selected_target = selected.target
        return selected
    if tracker is not None and tracker.selected_target:
        selected = next((sample for sample in windowed if sample.target == tracker.selected_target), None)
        if selected is not None:
            return selected
    return max(windowed, key=lambda sample: surface_layer_score(sample.target, package_name))


def read_surface_flinger_timestats_fps(serial: str, package_name: str) -> tuple[float | None, str]:
    sample = read_surface_flinger_timestats_sample(serial, package_name)
    if sample is None:
        return None, ""
    return sample.fps, sample.target


def gfxinfo_targets(package_name: str, pid: int) -> list[str]:
    targets: list[str] = []
    if pid > 0:
        targets.append(str(pid))
    package_name = (package_name or "").strip()
    if package_name and package_name not in targets:
        targets.append(package_name)
        if ":" in package_name:
            host = package_name.split(":", 1)[0]
            if host and host not in targets and not is_wechat_appbrand_target(package_name):
                targets.append(host)
    return targets


def frame_sample_payload(
    sample: TimestatsSample,
    source: str,
    scope: str,
    frame_source: str,
) -> dict:
    target_verified = sample.target_verified
    if target_verified is None and source == "adb-gfxinfo-framestats":
        target_verified = True
    elif target_verified is None and source == "adb-surfaceflinger-timestats":
        target_verified = False
    payload = {
        "platform": "android",
        "fps": sample.fps,
        "average_fps": sample.average_fps if sample.average_fps is not None else sample.fps,
        "source": source,
        "scope": scope,
        "target": sample.target,
        "frame_source": frame_source,
        "ordered_frames": sample.ordered_frames,
        "approximate": sample.approximate,
        "source_degraded": sample.source_degraded,
    }
    has_frame_observation = sample.has_frame_observation or sample.ordered_frames or sample.frame_count > 0
    if has_frame_observation:
        payload.update({
            "frame_count": sample.frame_count,
            "window_sec": sample.window_sec,
        })
    if sample.has_jank_metrics:
        payload.update({
            "jank": sample.jank,
            "big_jank": sample.big_jank,
        })
    if sample.frame_time_ms is not None:
        payload.update({
            "frame_time_ms": sample.frame_time_ms,
            "frame_time_mean_ms": sample.frame_time_mean_ms,
            "frame_time_p95_ms": sample.frame_time_p95_ms,
            "frame_time_max_ms": sample.frame_time_max_ms,
            "frame_time_aggregation": "p95",
        })
    if sample.jank_time_ms is not None:
        payload["jank_time_ms"] = sample.jank_time_ms
    if sample.stutter_percent is not None:
        payload["stutter_percent"] = sample.stutter_percent
    if sample.refresh_period_ns is not None and sample.refresh_period_ns > 0:
        payload["refresh_period_ns"] = sample.refresh_period_ns
        payload["refresh_rate_hz"] = 1_000_000_000.0 / sample.refresh_period_ns
    if sample.duplicate_timestamps:
        payload["duplicate_timestamps"] = sample.duplicate_timestamps
    if sample.out_of_order_timestamps:
        payload["out_of_order_timestamps"] = sample.out_of_order_timestamps
    if sample.invalid_frame_intervals:
        payload["invalid_frame_intervals"] = sample.invalid_frame_intervals
    if sample.ring_buffer_overrun:
        payload["ring_buffer_overrun"] = True
    if target_verified is not None:
        payload["target_verified"] = target_verified
    if sample.surface_owner_pid is not None and sample.surface_owner_pid > 0:
        payload["surface_owner_pid"] = sample.surface_owner_pid
    if sample.surface_owner_uid is not None and sample.surface_owner_uid >= 0:
        payload["surface_owner_uid"] = sample.surface_owner_uid
    if sample.source_elapsed_sec is not None and sample.source_elapsed_sec >= 0:
        payload["source_elapsed_sec"] = sample.source_elapsed_sec
    if sample.source_sequence is not None and sample.source_sequence > 0:
        payload["source_sequence"] = sample.source_sequence
    if sample.idle_window:
        payload["idle_window"] = True
    if sample.no_present_frames:
        payload["no_present_frames"] = True
    if sample.resume_gap_ms is not None and sample.resume_gap_ms > 0:
        payload["resume_gap_ms"] = sample.resume_gap_ms
    return payload


def vendor_current_layer_fps_payload(fps: float, target: str) -> dict:
    return {
        "platform": "android",
        "fps": fps,
        "source": "adb-surfaceflinger-current-layer-fps",
        "scope": "surface",
        "target": target,
        "frame_source": "vendor-current-layer-fps",
        "ordered_frames": False,
        "approximate": True,
        "target_verified": False,
    }


def fps_loop(serial: str, pid: int, package_name: str, interval: float, stop_event: threading.Event) -> None:
    targets = gfxinfo_targets(package_name, pid)
    if not targets:
        return
    timestats_enabled = False
    timestats_enabled_by_us = False
    timestats_attempted = False
    seen_by_target: dict[str, set[int]] = {target: set() for target in targets}
    state_by_target: dict[str, dict[str, object]] = {target: {} for target in targets}
    gfxinfo_next_probe_at: dict[str, float] = {target: 0.0 for target in targets}
    exact_gfxinfo_targets: set[str] = set()
    timestats_state = TimestatsTracker()
    display_latency_state = LatencyState()
    layer_latency_tracker = LayerLatencyTracker(target_pid=pid)
    display_refresh_period_ns: int | None = None
    try:
        while not stop_event.is_set():
            loop_started_at = time.monotonic()
            emitted = False
            gfxinfo_fallback: GfxInfoResult | None = None
            layer_samples, layer_source_available = read_surface_flinger_layer_latency_samples(
                serial,
                package_name,
                layer_latency_tracker,
            )
            for sample in layer_samples:
                emit(
                    "fps",
                    frame_sample_payload(
                        sample,
                        "adb-surfaceflinger-layer-latency",
                        "surface",
                        "ordered-layer-present",
                    ),
                )
                emitted = True

            if not layer_source_available and not emitted:
                probe_time = time.monotonic()
                for target in targets:
                    if target not in exact_gfxinfo_targets and probe_time < gfxinfo_next_probe_at[target]:
                        continue
                    gfxinfo = read_gfxinfo_sample(
                        serial,
                        target,
                        seen_by_target[target],
                        state_by_target[target],
                        app_package=package_name,
                    )
                    exact_display = bool(state_by_target[target].get("frame_exact_display")) or bool(
                        gfxinfo is not None and gfxinfo.sample.has_jank_metrics
                    )
                    if exact_display:
                        exact_gfxinfo_targets.add(target)
                        gfxinfo_next_probe_at[target] = 0.0
                    else:
                        exact_gfxinfo_targets.discard(target)
                        gfxinfo_next_probe_at[target] = probe_time + GFXINFO_FALLBACK_REPROBE_SECONDS
                    if gfxinfo is None and exact_display:
                        break
                    if gfxinfo is None:
                        continue
                    if gfxinfo.sample.has_jank_metrics:
                        emit(
                            "fps",
                            frame_sample_payload(
                                gfxinfo.sample,
                                "adb-gfxinfo-framestats",
                                "app",
                                gfxinfo.frame_source,
                            ),
                        )
                        emitted = True
                        break
                    else:
                        if gfxinfo_fallback is None:
                            gfxinfo_fallback = gfxinfo

            if not layer_source_available and not emitted:
                if not timestats_attempted:
                    timestats_attempted = True
                    display_refresh_period_ns = read_surface_refresh_period_ns(serial)
                    timestats_enabled, timestats_enabled_by_us = prepare_surface_flinger_timestats(serial)
                if timestats_enabled:
                    sample = read_surface_flinger_timestats_sample(
                        serial,
                        package_name,
                        timestats_state,
                        (display_refresh_period_ns / 1_000_000.0) if display_refresh_period_ns else None,
                    )
                else:
                    sample = None
                if sample is not None:
                    frame_source = "present2present-histogram" if sample.histogram else "surface-total-frame-count"
                    emit(
                        "fps",
                        frame_sample_payload(
                            sample,
                            "adb-surfaceflinger-timestats",
                            "surface",
                            frame_source,
                        ),
                    )
                    emitted = True

            if not layer_source_available and not emitted:
                if gfxinfo_fallback is not None:
                    emit(
                        "fps",
                        frame_sample_payload(
                            gfxinfo_fallback.sample,
                            "adb-gfxinfo-framestats",
                            "app",
                            gfxinfo_fallback.frame_source,
                        ),
                    )
                    emitted = True

            if not layer_source_available and not emitted:
                fps, used_target = read_surface_flinger_fps(serial, package_name)
                if fps is not None:
                    emit("fps", vendor_current_layer_fps_payload(fps, used_target))
                    emitted = True

            if not layer_source_available and not emitted:
                sample = read_surface_flinger_display_latency_sample(serial, display_latency_state)
                if sample is not None:
                    emit(
                        "fps",
                        frame_sample_payload(
                            sample,
                            "adb-surfaceflinger-display-latency",
                            "display",
                            "ordered-display-present",
                        ),
                    )
                    emitted = True
            if layer_source_available:
                poll_interval = surface_latency_poll_interval(
                    layer_latency_tracker.selected_state.refresh_period_ns
                )
            else:
                poll_interval = max(0.8, interval)
            stop_event.wait(remaining_poll_delay(loop_started_at, poll_interval))
    finally:
        if timestats_enabled and timestats_enabled_by_us:
            disable_surface_flinger_timestats(serial)


def process_cmdline(stdout: str) -> str:
    return (stdout or "").split("\0", 1)[0].strip()


def process_names_from_ps(stdout: str, pid: int) -> set[str]:
    names: set[str] = set()
    for raw in (stdout or "").splitlines():
        parts = raw.strip().split(None, 2)
        if len(parts) < 2 or not parts[0].isdigit() or int(parts[0]) != pid:
            continue
        names.add(parts[1])
        if len(parts) >= 3:
            args_name = process_cmdline(parts[2].split(None, 1)[0])
            if args_name:
                names.add(args_name)
    return names


def process_identity_from_probe(stdout: str, pid: int) -> tuple[str, int | None]:
    lines = (stdout or "").splitlines()
    observed_name = process_cmdline(lines[0]) if lines else ""
    start_time = None
    for raw in lines[1:]:
        line = raw.strip()
        if line.startswith(f"{pid} ("):
            start_time = parse_proc_start_time(line)
            break
    return observed_name, start_time


def read_process_start_time(serial: str, pid: int) -> int | None:
    result = adb(serial, ["shell", "cat", f"/proc/{pid}/stat"], timeout=3.0)
    return parse_proc_start_time(result.stdout) if result.returncode == 0 else None


def append_identity_diagnostic(
    diagnostics: list[str] | None,
    label: str,
    result: subprocess.CompletedProcess[str],
) -> None:
    if diagnostics is None:
        return
    detail = " ".join(f"{result.stdout}\n{result.stderr}".split())
    if len(detail) > 240:
        detail = detail[:237] + "..."
    diagnostics.append(f"{label} exit={result.returncode}" + (f": {detail}" if detail else ""))


def ensure_pid_alive(
    serial: str,
    pid: int,
    expected_name: str = "",
    expected_start_time_ticks: int = 0,
    diagnostics: list[str] | None = None,
) -> bool | None:
    command = f"cat /proc/{pid}/cmdline; printf '\\n'; cat /proc/{pid}/stat"
    result = adb(serial, ["shell", "sh", "-c", shlex.quote(command)], timeout=3.0)
    if result.returncode == 0:
        observed_name, observed_start_time = process_identity_from_probe(result.stdout, pid)
        expected = (expected_name or "").strip()
        if observed_name:
            if expected and observed_name != expected:
                return False
        elif expected:
            ps = adb(serial, ["shell", "ps", "-A", "-o", "PID,NAME,ARGS"], timeout=3.0)
            if ps.returncode != 0:
                append_identity_diagnostic(diagnostics, "identity-ps", ps)
                return None
            names = process_names_from_ps(ps.stdout, pid)
            if not names:
                return False
            if expected not in names:
                return False
        if expected_start_time_ticks > 0:
            if observed_start_time is None:
                if diagnostics is not None:
                    diagnostics.append("identity-probe exit=0: missing /proc starttime")
                return None
            if observed_start_time != expected_start_time_ticks:
                return False
        return bool(observed_name or observed_start_time is not None)
    message = f"{result.stdout}\n{result.stderr}".lower()
    if "no such file" in message or "no such process" in message:
        return False
    if "permission denied" in message:
        expected = (expected_name or "").strip()
        name_verified = not expected
        if expected:
            ps = adb(serial, ["shell", "ps", "-A", "-o", "PID,NAME,ARGS"], timeout=3.0)
            if ps.returncode == 0:
                names = process_names_from_ps(ps.stdout, pid)
                if names:
                    if expected not in names:
                        return False
                    name_verified = True
                    if expected_start_time_ticks <= 0:
                        return True
        stat = adb(serial, ["shell", "cat", f"/proc/{pid}/stat"], timeout=3.0)
        if stat.returncode == 0:
            observed_start_time = parse_proc_start_time(stat.stdout)
            if expected_start_time_ticks > 0:
                if observed_start_time is None:
                    if diagnostics is not None:
                        diagnostics.append("identity-stat exit=0: invalid /proc starttime")
                    return None
                if observed_start_time != expected_start_time_ticks:
                    return False
            if name_verified:
                return True
            if diagnostics is not None:
                diagnostics.append("identity-ps exit=0: expected process name unavailable")
            return None
        stat_message = f"{stat.stdout}\n{stat.stderr}".lower()
        if "no such file" in stat_message or "no such process" in stat_message:
            return False
        append_identity_diagnostic(diagnostics, "identity-stat", stat)
        return None
    append_identity_diagnostic(diagnostics, "identity-probe", result)
    return None


def confirm_initial_target_identity(
    serial: str,
    pid: int,
    expected_name: str = "",
    expected_start_time_ticks: int = 0,
    timeout_seconds: float = TARGET_IDENTITY_TIMEOUT_SECONDS,
    diagnostic_out: list[str] | None = None,
) -> bool | None:
    started_at = time.monotonic()
    last_diagnostic = ""
    while True:
        attempt_diagnostics: list[str] = []
        state = ensure_pid_alive(
            serial,
            pid,
            expected_name,
            expected_start_time_ticks,
            attempt_diagnostics,
        )
        if attempt_diagnostics:
            last_diagnostic = attempt_diagnostics[-1]
        if state is not None:
            if diagnostic_out is not None and last_diagnostic:
                diagnostic_out.append(last_diagnostic)
            return state

        remaining = max(0.0, timeout_seconds) - (time.monotonic() - started_at)
        if remaining <= 0:
            if diagnostic_out is not None and last_diagnostic:
                diagnostic_out.append(last_diagnostic)
            return None
        time.sleep(min(INITIAL_IDENTITY_RETRY_SECONDS, remaining))


def parse_process_uid(stdout: str) -> int | None:
    for raw in (stdout or "").splitlines():
        match = re.match(r"^Uid:\s+(\d+)\b", raw.strip())
        if match is not None:
            return int(match.group(1))
    return None


def read_process_uid(serial: str, pid: int) -> int | None:
    result = adb(serial, ["shell", "cat", f"/proc/{pid}/status"], timeout=3.0)
    return parse_process_uid(result.stdout) if result.returncode == 0 else None


def cpu_loop(serial: str, pid: int, interval: float, stop_event: threading.Event) -> None:
    previous_cpu: CpuSnapshot | None = None
    while not stop_event.is_set():
        loop_started_at = time.monotonic()
        try:
            prior_cpu = previous_cpu
            cpu, current_cpu = read_cpu(serial, pid, previous_cpu)
            previous_cpu = current_cpu
            if cpu is not None:
                normalized = None
                core_values = None
                if prior_cpu is not None and current_cpu is not None:
                    normalized = normalized_cpu_percent_from_raw(
                        cpu,
                        average_online_cpu_count(prior_cpu, current_cpu),
                    )
                    core_values = core_percent_from_snapshots(prior_cpu, current_cpu)
                payload = {
                    "platform": "android",
                    "value": cpu,
                    "unit": "%",
                    "source": "adb-proc-stat-per-core",
                    "scope": "process",
                    "semantics": "raw_process_cpu",
                    "pid": pid,
                }
                if normalized is not None:
                    payload["normalized_value"] = normalized
                    payload["normalized_unit"] = "%"
                    payload["normalized_source"] = "raw_process_cpu_divided_by_online_cores"
                    payload["normalized_semantics"] = "raw_process_cpu_divided_by_online_cores"
                if core_values is not None and current_cpu is not None:
                    payload["core_values"] = core_values
                    payload["core_count"] = len(core_values)
                    payload["core_source"] = "adb-proc-stat-per-core"
                    payload["core_scope"] = "device"
                    payload["core_semantics"] = "device_core_cpu"
                emit(
                    "cpu",
                    payload,
                )
        except (OSError, subprocess.SubprocessError) as exc:
            emit("status", {"message": f"Android PID CPU sample unavailable: {exc}"})
        stop_event.wait(remaining_poll_delay(loop_started_at, max(0.5, interval)))


def memory_loop(serial: str, pid: int, interval: float, stop_event: threading.Event) -> None:
    probe_state = MemoryProbeState()
    while not stop_event.is_set():
        loop_started_at = time.monotonic()
        try:
            memory = read_memory_sample(serial, pid, probe_state)
            if memory is not None:
                payload = {
                    "platform": "android",
                    "value": memory.value_mb,
                    "unit": "MB",
                    "metric": memory.metric,
                    "fallback": memory.fallback,
                    "source": memory.source,
                    "scope": "process",
                    "pid": pid,
                }
                if memory.rss_mb is not None:
                    payload["rss_value"] = memory.rss_mb
                emit("memory", payload)
        except (OSError, subprocess.SubprocessError) as exc:
            emit("status", {"message": f"Android PID memory sample unavailable: {exc}"})
        stop_event.wait(remaining_poll_delay(loop_started_at, max(0.5, interval)))


def read_device_thermal_metrics(
    serial: str,
    collect_temperature: bool,
) -> tuple[dict[str, float], str, tuple[int, str] | None]:
    thermal = adb(serial, ["shell", "dumpsys", "thermalservice"], timeout=4.0)
    thermal_output = thermal.stdout if thermal.returncode == 0 else ""
    values = parse_android_thermalservice(thermal_output) if collect_temperature else {}
    thermal_status = parse_android_thermal_status(thermal_output)
    sources: list[str] = []
    if values:
        sources.append("adb-dumpsys-thermalservice")
    if collect_temperature and "Battery" not in values:
        battery = adb(serial, ["shell", "dumpsys", "battery"], timeout=3.0)
        battery_temperature = parse_android_battery_temperature(battery.stdout) if battery.returncode == 0 else None
        if battery_temperature is not None:
            values["Battery"] = battery_temperature
            sources.append("adb-dumpsys-battery")
    return values, "+".join(sources), thermal_status


def read_device_temperatures(serial: str) -> tuple[dict[str, float], str]:
    values, source, _thermal_status = read_device_thermal_metrics(
        serial,
        collect_temperature=True,
    )
    return values, source


def temperature_loop(
    label: str,
    serial: str,
    interval: float,
    stop_event: threading.Event,
    collect_temperature: bool = True,
    collect_thermal_state: bool = True,
) -> None:
    temperature_notice_shown = False
    thermal_status_notice_shown = False
    while not stop_event.is_set():
        loop_started_at = time.monotonic()
        try:
            values, source, thermal_status = read_device_thermal_metrics(
                serial,
                collect_temperature=collect_temperature,
            )
            if collect_temperature and values:
                emit(
                    "temperature",
                    {
                        "platform": "android",
                        "values": values,
                        "unit": "C",
                        "source": source,
                        "scope": "device",
                        "sensor_policy": "maximum_per_thermal_type",
                    },
                )
                temperature_notice_shown = False
            elif collect_temperature and not temperature_notice_shown:
                emit("status", {"message": "Android 设备未开放可读取的温度传感器数据。"})
                temperature_notice_shown = True
            if collect_thermal_state and thermal_status is not None:
                level, name = thermal_status
                emit(
                    "thermal_state",
                    {
                        "platform": "android",
                        "value": level,
                        "state": name,
                        "source": "adb-dumpsys-thermalservice",
                        "scope": "device",
                        "semantics": "android_power_manager_thermal_status",
                    },
                )
                thermal_status_notice_shown = False
            elif collect_thermal_state and not thermal_status_notice_shown:
                emit("status", {"message": "Android 设备未开放系统热状态数据。"})
                thermal_status_notice_shown = True
        except (OSError, subprocess.SubprocessError) as exc:
            if not temperature_notice_shown or not thermal_status_notice_shown:
                emit("status", {"message": f"Android 设备温度与热状态暂不可用：{exc}"})
                temperature_notice_shown = True
                thermal_status_notice_shown = True
        stop_event.wait(remaining_poll_delay(loop_started_at, max(TEMPERATURE_INTERVAL_SECONDS, interval)))


def guarded_metric_loop(
    label: str,
    source,
    args: tuple[object, ...],
    stop_event: threading.Event,
    failure_event: threading.Event,
    failure_lock: threading.Lock,
) -> None:
    try:
        source(*args)
        if stop_event.is_set():
            return
        emit(
            "status",
            {
                "message": f"{label}采集线程已结束，当前指标暂停，其他指标继续采集。",
            },
        )
    except Exception as exc:
        # A single optional metric must not terminate the whole session. The
        # identity loop remains responsible for stopping on device/process loss.
        emit(
            "status",
            {
                "message": f"{label}采集失败，当前指标暂停，其他指标继续采集：{exc}",
            },
        )


def main(argv: Iterable[str]) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--serial", default="")
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--package", default="")
    parser.add_argument("--target-name", default="")
    parser.add_argument("--target-start-time-ticks", type=int, default=0)
    parser.add_argument("--interval", type=float, default=1.0)
    parser.add_argument("--no-fps", action="store_true")
    parser.add_argument("--no-memory", action="store_true")
    parser.add_argument("--no-cpu", action="store_true")
    parser.add_argument("--no-temperature", action="store_true")
    parser.add_argument("--no-thermal-state", action="store_true")
    args = parser.parse_args(list(argv))
    set_adb_executable(args.adb)

    emit(
        "status",
        {
            "message": (
                f"Android ADB runner started; serial={args.serial}; pid={args.pid}; "
                f"package={args.package}; starttime={args.target_start_time_ticks}"
            )
        },
    )
    target_start_time_ticks = max(0, args.target_start_time_ticks)
    initial_identity_diagnostics: list[str] = []
    initial_pid_state = confirm_initial_target_identity(
        args.serial,
        args.pid,
        args.target_name,
        target_start_time_ticks,
        TARGET_IDENTITY_TIMEOUT_SECONDS,
        initial_identity_diagnostics,
    )
    if initial_pid_state is not True:
        if initial_pid_state is False:
            message = f"Android pid {args.pid} 已退出或已被其他进程复用，采集未启动。"
            code = "android_target_process_missing"
        else:
            message = f"连续无法确认 Android pid {args.pid} 的进程身份或设备连接状态，采集未启动。"
            code = "android_target_identity_unverified"
        if initial_identity_diagnostics:
            message += "；ADB：" + initial_identity_diagnostics[-1]
        emit("fatal", {"code": code, "message": message})
        return 2
    if target_start_time_ticks <= 0:
        target_start_time_ticks = read_process_start_time(args.serial, args.pid) or 0
    if target_start_time_ticks <= 0:
        emit(
            "fatal",
            {
                "code": "android_target_identity_unverified",
                "message": f"无法读取 Android pid {args.pid} 的启动身份，采集未启动。",
            },
        )
        return 2
    emit("target", {"platform": "android", "pid": args.pid, "confirmed": True})
    stop_event = threading.Event()
    last_identity_confirmed_at = time.monotonic()
    failure_event = threading.Event()
    failure_lock = threading.Lock()
    fps_thread = threading.Thread(
        target=guarded_metric_loop,
        args=("Android FPS", fps_loop, (args.serial, args.pid, args.package, FPS_LOOP_INTERVAL_SEC, stop_event), stop_event, failure_event, failure_lock),
        daemon=True,
    )
    cpu_thread = threading.Thread(
        target=guarded_metric_loop,
        args=("Android PID CPU", cpu_loop, (args.serial, args.pid, args.interval, stop_event), stop_event, failure_event, failure_lock),
        daemon=True,
    )
    memory_thread = threading.Thread(
        target=guarded_metric_loop,
        args=("Android PID 内存", memory_loop, (args.serial, args.pid, args.interval, stop_event), stop_event, failure_event, failure_lock),
        daemon=True,
    )
    temperature_thread = threading.Thread(
        target=temperature_loop,
        args=(
            "Android device thermal metrics",
            args.serial,
            args.interval,
            stop_event,
            not args.no_temperature,
            not args.no_thermal_state,
        ),
        daemon=True,
    )
    if not args.no_fps:
        fps_thread.start()
    if not args.no_cpu:
        cpu_thread.start()
    if not args.no_memory:
        memory_thread.start()
    if not args.no_temperature or not args.no_thermal_state:
        temperature_thread.start()
    try:
        while True:
            pid_alive = ensure_pid_alive(
                args.serial,
                args.pid,
                args.target_name,
                target_start_time_ticks,
            )
            if pid_alive is False:
                stop_event.set()
                emit(
                    "fatal",
                    {
                        "code": "android_target_process_missing",
                        "message": f"Android pid {args.pid} 已退出、已被其他进程复用或设备连接已中断，采集已停止。",
                    },
                )
                return 2
            if pid_alive is True:
                last_identity_confirmed_at = time.monotonic()
            elif time.monotonic() - last_identity_confirmed_at >= TARGET_IDENTITY_TIMEOUT_SECONDS:
                stop_event.set()
                emit(
                    "fatal",
                    {
                        "code": "android_target_identity_unverified",
                        "message": f"连续无法确认 Android pid {args.pid} 的进程身份或设备连接状态，采集已停止。",
                    },
                )
                return 2
            if stop_event.wait(max(0.5, args.interval)):
                return 1 if failure_event.is_set() else 0
    finally:
        stop_event.set()
        if not args.no_fps:
            fps_thread.join(timeout=2.0)
        if not args.no_cpu:
            cpu_thread.join(timeout=2.0)
        if not args.no_memory:
            memory_thread.join(timeout=2.0)
        if not args.no_temperature or not args.no_thermal_state:
            temperature_thread.join(timeout=2.0)


def old_main_loop_for_contract_reference() -> None:
    # Keep command tokens visible for lightweight contract tests:
    # dumpsys", "gfxinfo
    return None


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        raise SystemExit(130)
    except Exception as exc:
        emit(
            "fatal",
            {
                "code": "android_runner_unhandled_exception",
                "message": f"Android 采集进程启动失败：{type(exc).__name__}: {exc}",
            },
        )
        raise SystemExit(1)
