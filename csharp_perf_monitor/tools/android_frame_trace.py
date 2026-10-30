"""Bounded FrameTimeline experiment. Decode protobuf with the official Perfetto CLI."""
from __future__ import annotations

import csv
import io
import math
from pathlib import Path
import subprocess
import uuid

import android_perf_runner as android


def metrics_from_present_points(points: list[int], *, target_verified: bool) -> dict:
    if not target_verified:
        return {"available": False, "reason": "target_owner_unverified"}
    if len(points) < 2:
        return {"available": False, "reason": "new_present_timestamps_insufficient"}
    intervals, _, duplicates, disorder, invalid = android.ordered_present_intervals_ms(points[1:], points[0])
    if duplicates or disorder or invalid:
        return {"available": False, "reason": "present_timestamp_integrity_lost"}
    metrics = android.ordered_frame_metrics(intervals)
    if metrics is None:
        return {"available": False, "reason": "invalid_frame_window"}
    sample = android.sample_from_ordered_metrics(metrics, "frametimeline-probe", exact_display=True, target_verified=True)
    return {
        "available": True, "target_verified": True, "frame_count": metrics.frame_count,
        "observation_ms": metrics.observation_ms, "fps": sample.fps,
        "frame_time_ms": sample.frame_time_ms, "frame_time_mean_ms": sample.frame_time_mean_ms,
        "frame_time_p95_ms": sample.frame_time_p95_ms, "frame_time_max_ms": sample.frame_time_max_ms,
        "jank": sample.jank, "big_jank": sample.big_jank,
    }


def frame_timeline_metrics(rows: list[dict[str, str]], pid: int, layer: str, verified: bool) -> dict:
    # The PID and exact canonical Surface prevent mixing sibling surfaces or display-wide frames.
    target = [r for r in rows if str(r.get("pid")) == str(pid) and r.get("layer_name") == layer]
    if not verified:
        return {"available": False, "reason": "target_owner_unverified"}
    if not target:
        return {"available": False, "reason": "target_surface_frames_unavailable"}
    points = []
    tokens: set[str] = set()
    dropped = 0
    unfinished = 0
    try:
        for row in target:
            if "drop" in row.get("present_type", "").lower():
                dropped += 1
                continue
            if row.get("present_type") not in {"On-time Present", "Late Present"}:
                return {"available": False, "reason": "unknown_presentation_type"}
            duration = int(row["dur"])
            if duration < 0:
                unfinished += 1
                continue
            token = str(int(row["surface_frame_token"]))
            if int(token) <= 0:
                return {"available": False, "reason": "invalid_surface_frame_token"}
            if token in tokens:
                return {"available": False, "reason": "ambiguous_surface_frame_token"}
            tokens.add(token)
            points.append(int(row["ts"]) + duration)
    except (KeyError, ValueError, TypeError):
        return {"available": False, "reason": "invalid_frame_timeline_row"}
    # SQL orders by presentation, because asynchronous app slices can finish out of start order.
    result = metrics_from_present_points(points, target_verified=True)
    result.update({"presented_frames": len(points), "dropped_frames": dropped,
                   "unfinished_frames": unfinished, "layer_name": layer})
    return result


def graphics_frame_metrics(rows: list[dict[str, str]], layer: str, verified: bool) -> dict:
    target = [r for r in rows if r.get("layer_name") == layer and r.get("name") == "PresentFenceSignaled"]
    if not verified:
        return {"available": False, "reason": "target_owner_unverified"}
    if not target:
        return {"available": False, "reason": "target_present_fence_events_unavailable"}
    try:
        numbers = [int(r["frame_number"]) for r in target]
        if len(set(numbers)) != len(numbers) or any(b <= a for a, b in zip(numbers, numbers[1:])):
            return {"available": False, "reason": "present_fence_frame_order_invalid"}
        # A skipped buffer number is not missing presents: apps can drop submitted buffers.
        return metrics_from_present_points([int(r["ts"]) for r in target], target_verified=True)
    except (ValueError, KeyError, TypeError):
        return {"available": False, "reason": "invalid_present_fence_event"}


def capture_frame_timeline(executable: str, serial: str, pid: int, layer: str, verified: bool,
                           seconds: float, processor: str, work_directory: Path) -> dict:
    if not processor or not Path(processor).is_file():
        return {"status": "unavailable", "metrics": {"available": False, "reason": "official_trace_processor_required"}}
    if pid <= 0 or not math.isfinite(seconds) or not 0 < seconds <= 30:
        raise ValueError("Invalid trace target/duration")
    work_directory.mkdir(parents=True, exist_ok=True)
    token = uuid.uuid4().hex
    remote = f"/data/misc/perfetto-traces/motuperf-{token}.perfetto-trace"
    local = work_directory / f"motuperf-{token}.perfetto-trace"
    config = (
        'buffers { size_kb: 8192 fill_policy: DISCARD }\n'
        'data_sources { config { name: "android.surfaceflinger.frametimeline" } }\n'
        'data_sources { config { name: "android.surfaceflinger.frame" } }\n'
        'data_sources { config { name: "linux.process_stats" process_stats_config { scan_all_processes_on_start: true } } }\n'
        f'duration_ms: {max(1, int(seconds * 1000))}\n'
    )
    base = [executable, "-s", serial]

    def run(args: list[str], **kwargs):
        return subprocess.run(args, text=True, encoding="utf-8", errors="replace",
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=seconds + 15, **kwargs)

    try:
        capture = run(base + ["shell", "perfetto", "--txt", "-c", "-", "-o", remote], input=config)
        if capture.returncode != 0:
            return {"status": "unavailable", "error": capture.stderr[-500:], "metrics": {"available": False, "reason": "trace_capture_failed"}}
        pulled = run(base + ["pull", remote, str(local)])
        if pulled.returncode != 0 or not local.is_file() or local.stat().st_size > 12 * 1024 * 1024:
            return {"status": "unavailable", "metrics": {"available": False, "reason": "trace_transfer_failed_or_oversized"}}
        # Official trace statistics distinguish capture success from an intact stream.
        integrity = run([processor, "-Q",
                         "SELECT coalesce(sum(value),0) AS failures FROM stats WHERE value>0 AND "
                         "(severity IN ('error','data_loss') OR name GLOB '*overrun*' "
                         "OR name GLOB 'traced_buf*overwritten*' OR name GLOB 'traced_buf*discard*' "
                         "OR name GLOB '*packet*loss*')", str(local)])
        counters = list(csv.DictReader(io.StringIO(integrity.stdout))) if integrity.returncode == 0 else []
        if not counters or int(counters[0]["failures"]) > 0:
            return {"status": "captured", "metrics": {"available": False, "reason": "trace_integrity_unconfirmed"}, "trace_file": str(local)}
        sql = (
            "SELECT f.ts,f.dur,p.pid,f.layer_name,f.surface_frame_token,f.present_type "
            "FROM actual_frame_timeline_slice f JOIN process p ON p.upid=f.upid "
            f"WHERE p.pid={pid} AND f.layer_name IS NOT NULL ORDER BY f.ts+f.dur,f.id"
        )
        parsed = run([processor, "-Q", sql, str(local)])
        if parsed.returncode != 0:
            return {"status": "captured", "error": parsed.stderr[-500:], "metrics": {"available": False, "reason": "frame_timeline_query_failed"}, "trace_file": str(local)}
        metrics = frame_timeline_metrics(list(csv.DictReader(io.StringIO(parsed.stdout))), pid, layer, verified)
        source = "perfetto_actual_surface_frame_timeline"
        if metrics.get("reason") == "target_surface_frames_unavailable":
            # Native EGL games may omit app FrameTimeline tokens but expose per-layer presents.
            fences = run([processor, "-Q", "SELECT ts,name,layer_name,frame_number FROM frame_slice "
                          "WHERE name='PresentFenceSignaled' ORDER BY ts,id", str(local)])
            if fences.returncode == 0:
                metrics = graphics_frame_metrics(list(csv.DictReader(io.StringIO(fences.stdout))), layer, verified)
                source = "perfetto_surface_present_fence"
        return {"status": "captured", "metrics": metrics, "trace_file": str(local),
                "trace_bytes": local.stat().st_size, "integrity_checked": True,
                "target_specific_frame_events_parsed": bool(metrics.get("available")),
                "source": source,
                "parser_boundary": "Perfetto v57.2 increments the informational graphics_frame_event_parser_errors "
                    "counter for PRESENT_FENCE in CreateBufferEvent's default branch, while still emitting its "
                    "timestamp/name/layer/frame_number. Only those raw presents are used; derived phase durations are not used."}
    except (OSError, subprocess.SubprocessError, ValueError, KeyError) as exc:
        return {"status": "unavailable", "error": str(exc)[:500], "metrics": {"available": False, "reason": "trace_probe_failed"}}
    finally:
        # Only delete the file created by this invocation; never stop other trace sessions.
        try:
            run(base + ["shell", "rm", "-f", remote])
        except (OSError, subprocess.SubprocessError):
            pass
