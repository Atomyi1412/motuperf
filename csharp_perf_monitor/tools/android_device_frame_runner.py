#!/usr/bin/env python3
"""Aggregate MoTuPerf process metrics with the Android device-side frame backend.

The existing Android runner remains responsible for process identity and the
non-frame metrics. This adapter replaces only FPS/Jank/FrameTime with validated
Perfetto events and emits the same JSONL contract consumed by PerfCollector.
"""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys
import threading
import time
from typing import Iterable


FRAME_PREFIX = "android_frame_experiment "
OUTPUT_LOCK = threading.Lock()


def configure_output_streams() -> None:
    """Keep JSONL output valid even when Windows starts Python with a GBK console."""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass


def emit(kind: str, payload: dict) -> None:
    with OUTPUT_LOCK:
        print(f"{kind} {json.dumps(payload, ensure_ascii=False, separators=(',', ':'))}", flush=True)


def _number(record: dict, name: str) -> float | None:
    value = record.get(name)
    if isinstance(value, bool):
        return None
    try:
        parsed = float(value)
    except (TypeError, ValueError):
        return None
    return parsed if math.isfinite(parsed) else None


def frame_record_to_events(record: dict, *, source_elapsed_sec: float = 0.0) -> list[tuple[str, dict]]:
    """Translate one experiment record into the existing MoTuPerf event contract."""
    kind = str(record.get("kind") or "")
    if kind == "sample":
        fps = _number(record, "fps")
        if fps is None or fps < 0 or fps > 240:
            return [("status", {
                "code": "device_frame_invalid_fps",
                "message": "设备侧帧窗口返回了无效 FPS，当前窗口保持缺测。",
            })]
        payload = {
            "platform": "android",
            "fps": fps,
            "average_fps": fps,
            "source": "android-device-perfetto",
            "scope": "android app/surface",
            "frame_source": str(record.get("source") or "perfetto-frame-events"),
            "ordered_frames": True,
            "approximate": False,
            "source_degraded": False,
            "target_verified": True,
            "source_sequence": int(_number(record, "window") or 0),
            "source_elapsed_sec": max(0.0, float(source_elapsed_sec)),
        }
        frame_count = _number(record, "frame_count")
        if frame_count is not None and frame_count >= 0:
            payload["frame_count"] = int(frame_count)
        observation_ms = _number(record, "observation_ms")
        if observation_ms is not None and observation_ms > 0:
            payload["frame_observation_ms"] = observation_ms
        frame_time_ms = _number(record, "frame_time_ms")
        p95_ms = _number(record, "frame_time_p95_ms")
        mean_ms = _number(record, "frame_time_mean_ms")
        maximum_ms = _number(record, "frame_time_max_ms")
        if frame_time_ms is not None and frame_time_ms > 0:
            payload["frame_time_ms"] = frame_time_ms
        if p95_ms is not None and p95_ms > 0:
            payload["frame_time_p95_ms"] = p95_ms
        if mean_ms is not None and mean_ms > 0:
            payload["frame_time_mean_ms"] = mean_ms
        if maximum_ms is not None and maximum_ms > 0:
            payload["frame_time_max_ms"] = maximum_ms
        for name in ("jank", "big_jank"):
            value = _number(record, name)
            if value is not None and value >= 0:
                payload[name] = value
        return [("fps", payload)]
    if kind == "unavailable":
        code = str(record.get("code") or "device_frame_unavailable")
        message = str(record.get("message") or "设备侧帧窗口不可用，当前窗口保持缺测。")
        return [("status", {
            "code": code,
            "message": f"设备侧帧数据不可用：{message}",
        })]
    if kind == "fatal":
        code = str(record.get("code") or "device_frame_backend_failed")
        message = str(record.get("message") or "设备侧帧后端失败。")
        return [("status", {
            "code": code,
            "message": f"设备侧帧后端失败：{message}；FPS/Jank/FrameTime 保持缺测，其他指标继续采集。",
        })]
    if kind == "status":
        message = str(record.get("message") or "").strip()
        return [("status", {"message": message})] if message else []
    if kind == "stopped":
        return [("status", {"code": "device_frame_backend_stopped", "message": "设备侧帧后端已停止；其他指标继续采集。"})]
    return []


def build_metrics_command(config: argparse.Namespace, metrics_runner: str, python_executable: str) -> list[str]:
    command = [
        python_executable,
        metrics_runner,
        "--adb", config.adb,
        "--serial", config.serial,
        "--pid", str(config.pid),
        "--package", config.package,
        "--target-name", config.target_name,
        "--target-start-time-ticks", str(config.target_start_time_ticks),
        "--interval", str(config.interval),
        "--no-fps",
    ]
    if config.no_memory:
        command.append("--no-memory")
    if config.no_cpu:
        command.append("--no-cpu")
    if config.no_temperature:
        command.append("--no-temperature")
    if config.no_thermal_state:
        command.append("--no-thermal-state")
    return command


def build_frame_command(config: argparse.Namespace, frame_runner: str, python_executable: str) -> list[str]:
    return [
        python_executable,
        frame_runner,
        "--adb", config.adb,
        "--serial", config.serial,
        "--pid", str(config.pid),
        "--package", config.package,
        "--trace-processor", config.trace_processor,
        "--work-directory", config.work_directory,
        "--window-seconds", str(config.frame_window_seconds),
    ]


def _write_stderr(prefix: str, stream) -> None:
    for line in stream:
        text = line.rstrip()
        if text:
            with OUTPUT_LOCK:
                print(f"{prefix}{text}", file=sys.stderr, flush=True)


def _forward_metrics(stream) -> None:
    for line in stream:
        text = line.rstrip()
        if text:
            with OUTPUT_LOCK:
                print(text, flush=True)


def _forward_frames(stream, started_at: float) -> None:
    for line in stream:
        text = line.rstrip()
        if not text.startswith(FRAME_PREFIX):
            if text:
                with OUTPUT_LOCK:
                    print(f"[device-frame] {text}", file=sys.stderr, flush=True)
            continue
        try:
            record = json.loads(text[len(FRAME_PREFIX):])
        except json.JSONDecodeError:
            emit("status", {"code": "device_frame_bad_payload", "message": "设备侧帧后端返回了无法解析的数据。"})
            continue
        for kind, payload in frame_record_to_events(record, source_elapsed_sec=time.monotonic() - started_at):
            emit(kind, payload)


def _terminate_tree(process: subprocess.Popen | None) -> None:
    if process is None or process.poll() is not None:
        return
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        else:
            process.terminate()
        process.wait(timeout=3)
    except (OSError, subprocess.SubprocessError):
        try:
            process.kill()
        except OSError:
            pass


def run(config: argparse.Namespace) -> int:
    metrics_runner = str(Path(config.metrics_runner).resolve())
    frame_runner = str(Path(config.frame_runner).resolve())
    for path, label in ((metrics_runner, "android_perf_runner.py"), (frame_runner, "android_frame_experiment.py")):
        if not Path(path).is_file():
            emit("fatal", {"code": "device_frame_missing_runtime", "message": f"缺少 {label}。"})
            return 2
    if not config.no_fps and not Path(config.trace_processor).is_file():
        emit("status", {
            "code": "device_frame_trace_processor_missing",
            "message": "缺少官方 trace_processor_shell.exe；FPS/Jank/FrameTime 保持缺测，其他指标继续采集。",
        })
        config.no_fps = True
    Path(config.work_directory).mkdir(parents=True, exist_ok=True)

    started_at = time.monotonic()
    metrics_command = build_metrics_command(config, metrics_runner, sys.executable)
    metrics = subprocess.Popen(
        metrics_command,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    frame = None
    if not config.no_fps:
        frame = subprocess.Popen(
            build_frame_command(config, frame_runner, sys.executable),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )

    threads = [
        threading.Thread(target=_forward_metrics, args=(metrics.stdout,), daemon=True),
        threading.Thread(target=_write_stderr, args=("[metrics] ", metrics.stderr), daemon=True),
    ]
    if frame is not None:
        threads.extend([
            threading.Thread(target=_forward_frames, args=(frame.stdout, started_at), daemon=True),
            threading.Thread(target=_write_stderr, args=("[device-frame] ", frame.stderr), daemon=True),
        ])
    for thread in threads:
        thread.start()

    stop_requested = threading.Event()

    def request_stop(_signum: int, _frame: object) -> None:
        stop_requested.set()

    signal.signal(signal.SIGINT, request_stop)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, request_stop)

    frame_failure_reported = False
    try:
        while not stop_requested.is_set() and metrics.poll() is None:
            if frame is not None and frame.poll() is not None and not frame_failure_reported:
                frame_failure_reported = True
                emit("status", {
                    "code": "device_frame_backend_exited",
                    "message": "设备侧帧后端已退出；FPS/Jank/FrameTime 保持缺测，其他指标继续采集。",
                })
            time.sleep(0.2)
    finally:
        _terminate_tree(frame)
        _terminate_tree(metrics)
        for thread in threads:
            thread.join(timeout=2.0)

    if stop_requested.is_set():
        return 130
    return metrics.returncode if metrics.returncode is not None else 1


def parse_args(argv: Iterable[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="MoTuPerf Android device-side frame aggregator")
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
    parser.add_argument("--trace-processor", required=True)
    parser.add_argument("--work-directory", required=True)
    parser.add_argument("--frame-window-seconds", type=float, default=5.0)
    parser.add_argument("--metrics-runner", default=str(Path(__file__).with_name("android_perf_runner.py")))
    parser.add_argument("--frame-runner", default=str(Path(__file__).with_name("android_frame_experiment.py")))
    return parser.parse_args(list(argv))


def main(argv: Iterable[str]) -> int:
    configure_output_streams()
    return run(parse_args(argv))


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        raise SystemExit(130)
