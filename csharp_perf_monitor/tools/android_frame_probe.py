#!/usr/bin/env python3
from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
import math
from pathlib import Path
import re
import shlex
import subprocess
import sys
import time
from typing import Iterable

import android_perf_runner as android
from android_frame_trace import capture_frame_timeline, metrics_from_present_points
from android_frame_transport import collect_device_batches


@dataclass(frozen=True)
class DeviceCpuTicks:
    total: int
    idle: int


@dataclass(frozen=True)
class DeviceSelection:
    serial: str
    error_code: str = ""
    error_message: str = ""


def adb(executable: str, serial: str, args: list[str], timeout: float = 8.0) -> subprocess.CompletedProcess[str]:
    command = [executable]
    if serial:
        command += ["-s", serial]
    command += args
    try:
        return subprocess.run(
            command,
            text=True,
            encoding="utf-8",
            errors="replace",
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
        )
    except subprocess.TimeoutExpired as exc:
        stdout = exc.stdout.decode("utf-8", errors="replace") if isinstance(exc.stdout, bytes) else (exc.stdout or "")
        return subprocess.CompletedProcess(command, 124, stdout=stdout, stderr=f"adb timed out after {timeout:.1f}s")
    except OSError as exc:
        return subprocess.CompletedProcess(command, 127, stdout="", stderr=str(exc))


def shell(executable: str, serial: str, command: str, timeout: float = 8.0) -> subprocess.CompletedProcess[str]:
    return adb(executable, serial, ["shell", "sh", "-c", shlex.quote(command)], timeout)


def parse_getprop_value(stdout: str) -> str:
    return next((line.strip() for line in (stdout or "").splitlines() if line.strip()), "")


def online_device_serials(stdout: str) -> list[str]:
    serials: list[str] = []
    for raw in (stdout or "").splitlines()[1:]:
        fields = raw.strip().split()
        if len(fields) >= 2 and fields[1] == "device":
            serials.append(fields[0])
    return serials


def select_online_device(stdout: str, requested_serial: str) -> DeviceSelection:
    serials = online_device_serials(stdout)
    requested_serial = requested_serial.strip()
    if requested_serial:
        if requested_serial in serials:
            return DeviceSelection(requested_serial)
        return DeviceSelection(
            "",
            "android_requested_device_not_connected",
            f"Requested Android device {requested_serial} is not online.",
        )
    if not serials:
        return DeviceSelection("", "android_device_not_connected", "No online Android device was reported by adb devices.")
    if len(serials) > 1:
        return DeviceSelection(
            "",
            "android_device_selection_required",
            f"Multiple Android devices are online ({', '.join(serials)}); pass --serial explicitly.",
        )
    return DeviceSelection(serials[0])


def parse_device_cpu_ticks(stdout: str) -> DeviceCpuTicks | None:
    for raw in (stdout or "").splitlines():
        parts = raw.split()
        if not parts or parts[0] != "cpu":
            continue
        try:
            values = [int(value) for value in parts[1:9]]
        except ValueError:
            return None
        if len(values) < 5:
            return None
        return DeviceCpuTicks(sum(values), values[3] + values[4])
    return None


def cpu_utilization_percent(before: DeviceCpuTicks | None, after: DeviceCpuTicks | None) -> float | None:
    if before is None or after is None:
        return None
    total_delta = after.total - before.total
    idle_delta = after.idle - before.idle
    if total_delta <= 0 or idle_delta < 0 or idle_delta > total_delta:
        return None
    return round((total_delta - idle_delta) * 100.0 / total_delta, 3)


def parse_battery_snapshot(stdout: str) -> dict[str, float | int | str]:
    result: dict[str, float | int | str] = {}
    keys = {
        "AC powered": "ac_powered",
        "USB powered": "usb_powered",
        "status": "status",
        "health": "health",
        "level": "level_percent",
        "temperature": "temperature_c",
        "voltage": "voltage_mv",
        "current now": "current_now_ua",
        "charge counter": "charge_counter_uah",
    }
    for raw in (stdout or "").splitlines():
        match = re.match(r"^\s*([^:]+):\s*(.*?)\s*$", raw)
        if match is None:
            continue
        label = match.group(1).strip().lower()
        value = match.group(2).strip()
        target = next((name for key, name in keys.items() if key.lower() == label), "")
        if not target:
            continue
        if target == "temperature_c":
            try:
                result[target] = int(value) / 10.0
            except ValueError:
                pass
        elif target in {"status", "health"}:
            result[target] = value
        elif target.endswith("_powered"):
            result[target] = value.lower() in {"true", "1", "yes"}
        else:
            try:
                result[target] = int(value)
            except ValueError:
                pass
    return result


def frame_metrics_from_latency(before_stdout: str, after_stdout: str, *, target_verified: bool = False) -> dict[str, object]:
    before_refresh, before_points = android.parse_surface_flinger_latency(before_stdout)
    after_refresh, after_points = android.parse_surface_flinger_latency(after_stdout)
    if not before_points:
        return {
            "available": False,
            "reason": "initial_present_baseline_unavailable",
            "refresh_period_ns": after_refresh or before_refresh,
        }
    baseline = max(before_points)
    new_points = [point for point in after_points if point > baseline]
    if not new_points:
        return {"available": False, "reason": "no_new_present_timestamps"}
    if baseline not in after_points or len(new_points) >= android.SURFACE_LATENCY_RING_CAPACITY - 1:
        return {"available": False, "reason": "latency_ring_overlap_lost"}
    result = metrics_from_present_points([baseline] + new_points, target_verified=target_verified)
    result["refresh_period_ns"] = after_refresh or before_refresh
    return result


def resolve_surface(
    executable: str,
    serial: str,
    pid: int,
    package_name: str,
) -> dict[str, object]:
    status = shell(executable, serial, f"cat /proc/{pid}/status", timeout=4.0)
    target_uid = android.parse_process_uid(status.stdout) if status.returncode == 0 else None
    layers = shell(executable, serial, "dumpsys SurfaceFlinger --layers", timeout=7.0)
    canonical = shell(executable, serial, "dumpsys SurfaceFlinger --list", timeout=7.0)
    owners = android.parse_surface_layer_owners(layers.stdout) if layers.returncode == 0 else []
    canonical_layers = android.parse_requested_surface_layer_names(canonical.stdout) if canonical.returncode == 0 else []
    owned = android.ranked_owned_surface_layers(
        owners,
        package_name,
        pid,
        target_uid or 0,
        canonical_layers,
    )
    approximate = android.ranked_surface_layers(canonical.stdout, package_name) if canonical.returncode == 0 else []
    if owned:
        observed = []
        for candidate in owned[:8]:
            latency = shell(executable, serial, f"dumpsys SurfaceFlinger --latency {shlex.quote(candidate.name)}", timeout=3)
            _, timestamps = android.parse_surface_flinger_latency(latency.stdout)
            if latency.returncode == 0 and timestamps:
                observed.append((candidate, max(timestamps)))
        if observed:
            newest = max(latest for _, latest in observed)
            # Preserve semantic ranking among concurrently active target-owned layers.
            owned = [candidate for candidate, latest in observed if newest - latest <= android.SURFACE_ACTIVE_RECENCY_NS]
        else:
            return {"selected_layer": "", "target_verified": False, "candidate_count": len(owned),
                    "ownership_confidence": "owned_but_no_present_timestamps"}
        return {
            "selected_layer": owned[0].name,
            "target_verified": True,
            "owner_pid": owned[0].pid,
            "owner_uid": owned[0].uid,
            "candidate_count": len(owned),
            "ownership_confidence": "verified",
        }
    if approximate:
        return {
            "selected_layer": approximate[0],
            "target_verified": False,
            "owner_pid": 0,
            "owner_uid": 0,
            "candidate_count": len(approximate),
            "ownership_confidence": "name_only",
        }
    return {
        "selected_layer": "",
        "target_verified": False,
        "owner_pid": 0,
        "owner_uid": 0,
        "candidate_count": 0,
        "ownership_confidence": "unavailable",
    }


def trace_capabilities(executable: str, serial: str) -> dict[str, object]:
    perfetto = shell(executable, serial, "perfetto --version", timeout=5.0)
    atrace = shell(executable, serial, "atrace --list_categories", timeout=5.0)
    tracefs = shell(
        executable,
        serial,
        "for p in /sys/kernel/tracing/trace_marker /sys/kernel/debug/tracing/trace_marker; do "
        "if [ -w \"$p\" ]; then echo \"$p\"; exit 0; fi; done; exit 1",
        timeout=5.0,
    )
    categories = [
        line.strip()
        for line in atrace.stdout.splitlines()
        if line.strip() and not line.strip().lower().startswith("error")
    ]
    return {
        "perfetto_available": perfetto.returncode == 0,
        "perfetto_version": perfetto.stdout.strip() if perfetto.returncode == 0 else "",
        "atrace_available": atrace.returncode == 0,
        "atrace_categories": categories,
        "gfx_category_available": any(category.split()[0].lower() == "gfx" for category in categories),
        "tracefs_writable_path": tracefs.stdout.strip() if tracefs.returncode == 0 else "",
        "trace_permission": "tracefs_writable_only" if tracefs.returncode == 0 else "denied_or_unavailable",
        "trace_permission_error": "" if tracefs.returncode == 0 else (tracefs.stderr or tracefs.stdout).strip(),
    }


def sample_cpu(executable: str, serial: str) -> DeviceCpuTicks | None:
    result = shell(executable, serial, "cat /proc/stat", timeout=4.0)
    return parse_device_cpu_ticks(result.stdout) if result.returncode == 0 else None


def sample_battery(executable: str, serial: str) -> dict[str, float | int | str]:
    result = shell(executable, serial, "dumpsys battery", timeout=5.0)
    return parse_battery_snapshot(result.stdout) if result.returncode == 0 else {}


def delta_number(after: object, before: object) -> float | int | None:
    if isinstance(after, (int, float)) and isinstance(before, (int, float)):
        return after - before
    return None


def target_identity(executable: str, serial: str, pid: int, package: str) -> int | None:
    result = shell(executable, serial, f"cat /proc/{pid}/cmdline; printf '\\n'; cat /proc/{pid}/stat", timeout=4)
    name, start = android.process_identity_from_probe(result.stdout, pid)
    return start if result.returncode == 0 and name == package else None


def collect_report(
    executable: str,
    serial: str,
    pid: int,
    package_name: str,
    trace_seconds: float,
    trace_processor: str = "",
    work_directory: Path | None = None,
    device_batch: bool = False,
) -> dict[str, object]:
    sdk = parse_getprop_value(shell(executable, serial, "getprop ro.build.version.sdk", timeout=4.0).stdout)
    abi = parse_getprop_value(shell(executable, serial, "getprop ro.product.cpu.abi", timeout=4.0).stdout)
    capabilities = trace_capabilities(executable, serial)
    surface = resolve_surface(executable, serial, pid, package_name)
    selected_layer = str(surface.get("selected_layer") or "")
    initial_identity = target_identity(executable, serial, pid, package_name)

    baseline_cpu_before = sample_cpu(executable, serial)
    time.sleep(trace_seconds)
    baseline_cpu_after = sample_cpu(executable, serial)
    battery_before = sample_battery(executable, serial)
    trace_cpu_before = sample_cpu(executable, serial)
    trace_started = time.monotonic()
    trace_result = (capture_frame_timeline(
        executable, serial, pid, selected_layer, bool(surface.get("target_verified")),
        trace_seconds, trace_processor, work_directory,
    ) if work_directory is not None and selected_layer and initial_identity is not None else {
        "status": "unavailable", "metrics": {"available": False, "reason": "trace_processor_and_work_directory_required"}})
    trace_elapsed = time.monotonic() - trace_started
    trace_cpu_after = sample_cpu(executable, serial)
    battery_after = sample_battery(executable, serial)
    # Revalidate target ownership after capture. A switched/recreated Surface cannot inherit confidence.
    after_surface = resolve_surface(executable, serial, pid, package_name)
    owner_stable = (surface.get("target_verified") is True and after_surface.get("target_verified") is True
                    and selected_layer == after_surface.get("selected_layer")
                    and surface.get("owner_pid") == after_surface.get("owner_pid")
                    and initial_identity is not None
                    and initial_identity == target_identity(executable, serial, pid, package_name))
    frame_metrics = trace_result["metrics"]
    if not owner_stable:
        frame_metrics = {"available": False, "reason": "surface_owner_changed_or_unverified"}
        trace_result["metrics"] = frame_metrics
    trace_validated = bool(frame_metrics.get("available") and trace_result.get("integrity_checked"))
    batch_result = (collect_device_batches(executable, serial, pid, selected_layer, owner_stable, trace_seconds)
                    if device_batch and selected_layer and initial_identity is not None else {"status": "not_requested_or_target_unverified"})
    if device_batch and selected_layer and initial_identity is not None:
        final_surface = resolve_surface(executable, serial, pid, package_name)
        if final_surface != after_surface or initial_identity != target_identity(executable, serial, pid, package_name):
            batch_result["metrics"] = {"available": False, "reason": "surface_owner_changed"}
    return {
        "schema_version": 2,
        "platform": "android",
        "pid": pid,
        "package": package_name,
        "android_sdk": sdk,
        "abi": abi,
        "capabilities": capabilities,
        "surface": surface,
        "trace_capture": {**trace_result, "elapsed_sec": round(trace_elapsed, 3),
                          "target_specific_frame_events_parsed": trace_validated},
        "frame_metrics": frame_metrics,
        "frame_metrics_source": trace_result.get("source", "perfetto_actual_surface_frame_timeline"),
        "device_batch_experiment": batch_result,
        "cpu": {
            "baseline_percent": cpu_utilization_percent(baseline_cpu_before, baseline_cpu_after),
            "trace_period_percent": cpu_utilization_percent(trace_cpu_before, trace_cpu_after),
            "delta_percentage_points": (
                round((cpu_utilization_percent(trace_cpu_before, trace_cpu_after) or 0.0) - (cpu_utilization_percent(baseline_cpu_before, baseline_cpu_after) or 0.0), 3)
                if cpu_utilization_percent(trace_cpu_before, trace_cpu_after) is not None
                and cpu_utilization_percent(baseline_cpu_before, baseline_cpu_after) is not None
                else None
            ),
        },
        "battery": {
            "before": battery_before,
            "after": battery_after,
            "level_delta_percent": delta_number(battery_after.get("level_percent"), battery_before.get("level_percent")),
            "charge_counter_delta_uah": delta_number(battery_after.get("charge_counter_uah"), battery_before.get("charge_counter_uah")),
        },
        "is_device_side_trace_validated": trace_validated,
        "validation_boundary": (
            "Trace FPS/Jank/FrameTime require intact actual target SurfaceFrame or PresentFenceSignaled events decoded by official Perfetto. "
            "Device batches are a separate SF-latency experiment, not simultaneous trace ground truth. "
            "Whole-device CPU deltas are exploratory and include capture/transfer/parsing time; "
            "they cannot isolate collector overhead from game variation. Short USB-powered battery samples do not establish power cost. "
            "Default product capture is unchanged."
        ),
    }


def main(argv: Iterable[str]) -> int:
    parser = argparse.ArgumentParser(description="MoTuPerf Android device-side frame collection prototype")
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--serial", default="")
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--package", required=True)
    parser.add_argument("--trace-seconds", type=float, default=3.0)
    parser.add_argument("--output", default="")
    parser.add_argument("--trace-processor", default="", help="Path to official trace_processor_shell executable")
    parser.add_argument("--work-directory", default="", help="Directory for bounded diagnostic traces")
    parser.add_argument("--device-batch", action="store_true", help="Run the separate persistent ADB/SF batch experiment")
    args = parser.parse_args(list(argv))
    if args.pid <= 0 or not math.isfinite(args.trace_seconds) or not 0 < args.trace_seconds <= 30:
        emit_error("invalid_arguments", "--pid must be positive and --trace-seconds must be within (0, 30]")
        return 2
    devices = adb(args.adb, "", ["devices"], timeout=6.0)
    if devices.returncode != 0:
        emit_error("adb_unavailable", (devices.stderr or devices.stdout).strip())
        return 2
    selection = select_online_device(devices.stdout, args.serial)
    if selection.error_code:
        emit_error(selection.error_code, selection.error_message)
        return 2
    work_directory = Path(args.work_directory) if args.work_directory else (Path(args.output).resolve().parent if args.output else None)
    report = collect_report(args.adb, selection.serial, args.pid, args.package, args.trace_seconds,
                            args.trace_processor, work_directory, args.device_batch)
    if args.output:
        Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("android_frame_probe " + json.dumps(report, ensure_ascii=False, separators=(",", ":")), flush=True)
    return 0


def emit_error(code: str, message: str) -> None:
    print("android_frame_probe " + json.dumps({"schema_version": 1, "ok": False, "code": code, "message": message}, ensure_ascii=False, separators=(",", ":")), flush=True)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
