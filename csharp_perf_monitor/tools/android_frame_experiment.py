#!/usr/bin/env python3
"""Continuous, opt-in Android device-side frame experiment.

This runner deliberately does not call android_perf_runner. Every window is
decoded from target-owned Perfetto/SurfaceFlinger present events.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import signal
import sys
import time
from typing import Iterable

from android_frame_probe import adb, capture_frame_timeline, resolve_surface, target_identity


def configure_output_streams() -> None:
    """Emit UTF-8 JSONL regardless of the Windows console code page."""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass


def emit(kind: str, **payload: object) -> None:
    record = {"kind": kind, "timestamp": time.time(), **payload}
    print("android_frame_experiment " + json.dumps(record, ensure_ascii=False, separators=(",", ":")), flush=True)


def process_rows(executable: str, serial: str) -> list[dict[str, object]]:
    result = adb(executable, serial, ["shell", "ps", "-A", "-o", "PID,NAME"], timeout=8.0)
    if result.returncode != 0:
        raise RuntimeError((result.stderr or result.stdout).strip() or "adb ps failed")
    packages_result = adb(executable, serial, ["shell", "pm", "list", "packages", "-3"], timeout=8.0)
    user_packages = {
        line.strip().removeprefix("package:")
        for line in packages_result.stdout.splitlines()
        if line.strip().startswith("package:")
    }
    rows: list[dict[str, object]] = []
    for line in result.stdout.splitlines():
        parts = line.strip().split(None, 1)
        if len(parts) != 2:
            continue
        try:
            pid = int(parts[0])
        except ValueError:
            continue
        name = parts[1].strip()
        package = name.split(":", 1)[0]
        if pid > 0 and name and package in user_packages and not name.startswith("["):
            rows.append({"pid": pid, "name": name, "package": package})
    unique: dict[tuple[int, str], dict[str, object]] = {(int(row["pid"]), str(row["name"])): row for row in rows}
    return sorted(unique.values(), key=lambda row: (str(row["package"]), int(row["pid"])))


def online_serial(executable: str, requested: str) -> str:
    result = adb(executable, "", ["devices"], timeout=8.0)
    if result.returncode != 0:
        raise RuntimeError((result.stderr or result.stdout).strip() or "adb devices failed")
    devices = [line.split()[0] for line in result.stdout.splitlines()[1:]
               if len(line.split()) >= 2 and line.split()[1] == "device"]
    if requested:
        if requested not in devices:
            raise RuntimeError(f"设备未连接或未授权：{requested}")
        return requested
    if not devices:
        raise RuntimeError("没有检测到已授权的 Android 设备")
    if len(devices) > 1:
        raise RuntimeError("检测到多台 Android 设备，请先选择设备")
    return devices[0]


def run(args: argparse.Namespace) -> int:
    stop = False

    def request_stop(_signum: int, _frame: object) -> None:
        nonlocal stop
        stop = True

    signal.signal(signal.SIGINT, request_stop)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, request_stop)
    if args.window_seconds <= 0 or args.window_seconds > 30:
        emit("fatal", code="invalid_window", message="窗口时长必须在 0 到 30 秒之间")
        return 2
    try:
        serial = online_serial(args.adb, args.serial)
        emit("status", message=f"已连接设备 {serial}", serial=serial)
        rows = process_rows(args.adb, serial)
        emit("processes", processes=rows)
        package = args.package.strip()
        pid = int(args.pid)
        if not package or pid <= 0:
            raise RuntimeError("必须选择有效的目标进程和包名")
        emit("status", message=f"正在验证目标 {package} (PID {pid})")
        if target_identity(args.adb, serial, pid, package) is None:
            raise RuntimeError("目标进程不存在、已重启，或进程包名与选择不一致")
        emit("target", pid=pid, package=package, confirmed=True)
        window = 0
        while not stop:
            window += 1
            started = time.monotonic()
            identity = target_identity(args.adb, serial, pid, package)
            if identity is None:
                emit("unavailable", window=window, code="target_identity_changed", message="目标进程已退出或 PID 已变化，请停止后重新选择")
                return 3
            surface = resolve_surface(args.adb, serial, pid, package)
            layer = str(surface.get("selected_layer") or "")
            if not layer or surface.get("target_verified") is not True:
                emit("unavailable", window=window, code="target_surface_unverified", message="没有找到目标应用归属且有实际呈现事件的 Surface，当前窗口不计入指标", surface=surface)
                time.sleep(1.0)
                continue
            emit("status", window=window, message=f"设备侧采集中：{args.window_seconds:.1f} 秒窗口", layer=layer)
            result = capture_frame_timeline(
                args.adb, serial, pid, layer, True, args.window_seconds,
                args.trace_processor, Path(args.work_directory),
            )
            after = resolve_surface(args.adb, serial, pid, package)
            stable = (after.get("target_verified") is True
                      and after.get("selected_layer") == layer
                      and identity == target_identity(args.adb, serial, pid, package))
            metrics = result.get("metrics") if isinstance(result.get("metrics"), dict) else {}
            if not stable:
                emit("unavailable", window=window, code="target_surface_changed", message="采集窗口内目标进程或 Surface 发生变化，当前窗口丢弃", surface=after)
            elif metrics.get("available") is True and result.get("integrity_checked") is True:
                emit("sample", window=window, seconds=round(time.monotonic() - started, 3), source=result.get("source"), layer=layer, **metrics)
            else:
                emit("unavailable", window=window, code=str(metrics.get("reason") or "frame_metrics_unavailable"), message="当前窗口没有完整的目标设备侧帧事件，未生成指标", source=result.get("source"))
    except (OSError, RuntimeError, ValueError) as exc:
        emit("fatal", code="experiment_failed", message=str(exc)[:500])
        return 1
    emit("stopped", message="设备侧实验已停止")
    return 0


def main(argv: Iterable[str]) -> int:
    configure_output_streams()
    parser = argparse.ArgumentParser(description="MoTuPerf Android device-side frame experiment")
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--serial", default="")
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--package", required=True)
    parser.add_argument("--trace-processor", required=True)
    parser.add_argument("--work-directory", required=True)
    parser.add_argument("--window-seconds", type=float, default=5.0)
    return run(parser.parse_args(list(argv)))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
