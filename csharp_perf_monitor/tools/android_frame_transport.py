"""Opt-in on-device SF sampling over one bounded ADB shell, not a monitoring agent."""
from __future__ import annotations

from dataclasses import dataclass
import math
import queue
import shlex
import subprocess
import threading
import time

import android_perf_runner as android
from android_frame_trace import metrics_from_present_points


@dataclass(frozen=True)
class LatencySnapshot:
    sequence: int
    uptime: float
    stdout: str


def summarize_snapshots(snapshots: list[LatencySnapshot], verified: bool) -> dict:
    points: list[int] = []
    baseline: int | None = None
    previous: LatencySnapshot | None = None
    gaps = 0
    stale = 0
    for snapshot in snapshots:
        if snapshot.sequence < 0 or not math.isfinite(snapshot.uptime) or snapshot.uptime <= 0:
            gaps += 1
            continue
        refresh, timestamps = android.parse_surface_flinger_latency(snapshot.stdout)
        if previous is not None and (snapshot.sequence != previous.sequence + 1 or snapshot.uptime <= previous.uptime):
            gaps += 1
        if previous is not None and snapshot.uptime - previous.uptime > android.ORDERED_SOURCE_MAX_POLL_GAP_SECONDS:
            gaps += 1
        previous = snapshot
        if not timestamps:
            gaps += 1
            continue
        if baseline is None:
            baseline = max(timestamps)
            points.append(baseline)
            continue
        new = [t for t in timestamps if t > baseline]
        if not new:
            stale += 1
            continue
        # An old endpoint must still be present to prove that the ring has not wrapped.
        if baseline not in timestamps or len(new) >= android.SURFACE_LATENCY_RING_CAPACITY - 1:
            gaps += 1
        points.extend(new)
        baseline = max(timestamps)
    metrics = metrics_from_present_points(points, target_verified=verified) if not gaps else {
        "available": False, "reason": "snapshot_sequence_or_ring_integrity_lost"}
    return {"metrics": metrics, "snapshots": len(snapshots), "discontinuities": gaps,
            "unchanged_snapshots": stale, "new_frame_count": max(0, len(points) - 1)}


def collect_device_batches(executable: str, serial: str, pid: int, layer: str,
                           verified: bool, seconds: float, interval: float = 0.25,
                           batch_size: int = 2) -> dict:
    if pid <= 0 or not math.isfinite(seconds) or not 0 < seconds <= 30:
        raise ValueError("Invalid sampling duration/target")
    if not math.isfinite(interval) or not 0.1 <= interval <= 1 or not 1 <= batch_size <= 4:
        raise ValueError("Invalid sampling interval/batch size")
    count = min(300, max(2, math.ceil(seconds / interval)))
    # All values interpolated below are validated numeric values or POSIX-quoted names.
    # Shell buffers at most four latency snapshots before writing one transport batch.
    command = (
        "read start rest < /proc/uptime; "
        f"limit=$((${{start%.*}} + {math.ceil(seconds) + 5})); "
        f"i=0; data=''; while [ $i -lt {count} ] && [ -d /proc/{pid} ]; do "
        "read up rest < /proc/uptime; "
        "if [ ${up%.*} -gt $limit ]; then break; fi; "
        f"sample=$(dumpsys -t 2 SurfaceFlinger --latency {shlex.quote(layer)}); "
        "data=\"${data}__MOTUPERF_SNAPSHOT__ $i $up\n$sample\n\"; "
        f"i=$((i+1)); if [ $((i % {batch_size})) -eq 0 ]; then "
        "printf '%s' \"$data\"; printf '__MOTUPERF_BATCH_END__\\n'; data=''; fi; "
        f"sleep {interval:g}; done; "
        "if [ -n \"$data\" ]; then printf '%s' \"$data\"; printf '__MOTUPERF_BATCH_END__\\n'; fi"
    )
    events: queue.Queue[str | None] = queue.Queue(maxsize=1024)
    overflow = threading.Event()
    try:
        process = subprocess.Popen([executable, "-s", serial, "shell", "sh", "-c", shlex.quote(command)],
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                   encoding="utf-8", errors="replace")
    except OSError as exc:
        return {"status": "unavailable", "error": str(exc)[:300],
                "metrics": {"available": False, "reason": "device_sampler_start_failed"}}

    def read_output() -> None:
        try:
            for line in process.stdout:
                if len(line) > 4096:
                    overflow.set()
                    break
                try:
                    events.put(line, timeout=0.2)
                except queue.Full:
                    overflow.set()
                    break
        finally:
            try:
                events.put(None, timeout=0.2)
            except queue.Full:
                overflow.set()

    reader = threading.Thread(target=read_output, daemon=True)
    reader.start()
    snapshots = []
    pending: list[LatencySnapshot] = []
    current: tuple[int, float] | None = None
    body: list[str] = []
    batches = 0
    deadline = time.monotonic() + seconds + 10
    error = ""

    def finish_snapshot() -> None:
        nonlocal current, body
        if current is not None:
            if len(body) > 140 or len(pending) >= batch_size:
                raise ValueError("Oversized device snapshot/batch")
            pending.append(LatencySnapshot(*current, "".join(body)))
        current, body = None, []

    try:
        while time.monotonic() < deadline and not overflow.is_set():
            try:
                line = events.get(timeout=min(0.5, max(0.01, deadline - time.monotonic())))
            except queue.Empty:
                continue
            if line is None:
                break
            if line.startswith("__MOTUPERF_SNAPSHOT__ "):
                finish_snapshot()
                fields = line.split()
                current = (int(fields[1]), float(fields[2]))
            elif line.strip() == "__MOTUPERF_BATCH_END__":
                finish_snapshot()
                snapshots.extend(pending)
                pending = []
                batches += 1
                if len(snapshots) > count:
                    raise ValueError("Too many snapshots")
            elif current is not None:
                body.append(line)
                if len(body) > 140:
                    raise ValueError("Oversized latency response")
        else:
            error = "device_batch_timeout_or_backpressure"
        process.wait(timeout=1)
        if process.returncode != 0 or current is not None or pending or len(snapshots) != count:
            error = error or "device_batch_incomplete"
    except (OSError, subprocess.SubprocessError, ValueError, IndexError) as exc:
        error = str(exc)[:300]
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=3)
        reader.join(timeout=1)
        process.stdout.close()
    result = summarize_snapshots(snapshots, verified)
    if error:
        result["metrics"] = {"available": False, "reason": error}
    result.update({"status": "completed" if not error else "inconclusive", "error": error,
                   "host_adb_processes": 1, "device_dumpsys_queries": len(snapshots),
                   "transport_batches": batches, "interval_sec": interval,
                   "batch_size": batch_size, "nominal_added_batch_delay_sec": interval * (batch_size - 1),
                   "boundary": "Reduces host ADB launches, not the number or cost of on-device dumpsys calls."})
    return result
