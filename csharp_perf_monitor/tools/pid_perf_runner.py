import argparse
import asyncio
import json
import math
import signal
import sys
import threading
import time
import uuid
from copy import deepcopy

from display_metrics import (
    BIG_JANK_FRAME_TIME_MS,
    IdleFrameWindowTracker,
    IOS_DISPLAY_FRAME_EVENT,
    IOS_DISPLAY_FRAME_EVENT_ID,
    IOS_DISPLAY_FRAME_EVENT_NAME,
    JANK_FRAME_TIME_MS,
    OrderedTimestampAccumulator,
    ordered_frame_payload,
    ordered_frame_metrics,
    ordered_zero_fps_payload,
    perfdog_jank_counts as shared_perfdog_jank_counts,
    recent_interval_history as shared_recent_interval_history,
)
from temperature_metrics import (
    ios_thermal_state_unavailable_reason,
    parse_ios_battery_temperature,
    parse_ios_thermal_state,
)
from ios_target_rebinding import find_application_rebind_candidate, is_webkit_process
NANO_SECOND = 1e9
ORDERED_FPS_FALLBACK_SECONDS = 4.0
TARGET_PROCESS_TIMEOUT_SECONDS = 8.0
TARGET_CONFIRMATION_GRACE_SECONDS = 2.0
TARGET_REBIND_SCAN_SECONDS = 1.0
TEMPERATURE_INTERVAL_SECONDS = 5.0

InstrumentsBase = None
InstrumentsService = None
InstrumentRPCParseError = None
kdbg_extract_all = None
kperf_data = None
bpylist2 = None


def _load_legacy_ios_device_runtime():
    global InstrumentsBase, InstrumentsService, InstrumentRPCParseError
    global kdbg_extract_all, kperf_data, bpylist2

    from ios_device.cli.base import InstrumentsBase as imported_instruments_base
    from ios_device.util import bpylist2 as imported_bpylist2
    from ios_device.util.exceptions import InstrumentRPCParseError as imported_parse_error
    from ios_device.util.kperf_data import kdbg_extract_all as imported_kdbg_extract_all
    from ios_device.util.utils import kperf_data as imported_kperf_data
    from ios_device.util.variables import InstrumentsService as imported_instruments_service

    if InstrumentsBase is None:
        InstrumentsBase = imported_instruments_base
    if InstrumentsService is None:
        InstrumentsService = imported_instruments_service
    if InstrumentRPCParseError is None:
        InstrumentRPCParseError = imported_parse_error
    if kdbg_extract_all is None:
        kdbg_extract_all = imported_kdbg_extract_all
    if kperf_data is None:
        kperf_data = imported_kperf_data
    if bpylist2 is None:
        bpylist2 = imported_bpylist2
    bpylist2.UNARCHIVE_CLASS_MAP.setdefault("DTTapStatusMessage", bpylist2.DTTapMessageArchive)


if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


def emit(kind, payload):
    sys.stdout.write(kind + " " + json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


async def _ios_temperature_session(udid, interval_seconds, stop_event):
    from pymobiledevice3.lockdown import create_using_usbmux
    from pymobiledevice3.services.diagnostics import DiagnosticsService

    lockdown = await create_using_usbmux(serial=udid)
    try:
        async with DiagnosticsService(lockdown=lockdown) as diagnostics:
            while not stop_event.is_set():
                info = await diagnostics.get_battery()
                temperature = parse_ios_battery_temperature(info)
                if temperature is not None:
                    emit(
                        "temperature",
                        {
                            "platform": "ios",
                            "values": {"Battery": temperature},
                            "unit": "C",
                            "source": "pymobiledevice3-diagnostics-ioregistry",
                            "scope": "device",
                            "sensor_policy": "battery_ioregistry",
                        },
                    )
                deadline = time.monotonic() + interval_seconds
                while not stop_event.is_set() and time.monotonic() < deadline:
                    await asyncio.sleep(min(0.2, max(0.0, deadline - time.monotonic())))
    finally:
        await lockdown.close()


def ios_temperature_loop(udid, interval_ms, stop_event):
    try:
        interval_seconds = max(TEMPERATURE_INTERVAL_SECONDS, interval_ms / 1000.0)
        asyncio.run(_ios_temperature_session(udid, interval_seconds, stop_event))
    except Exception as exc:
        if not stop_event.is_set():
            emit("status", {"message": "iOS 电池温度不可用，其他指标继续采集：" + str(exc)})


def fps_from_selector(selector):
    if not isinstance(selector, dict):
        return None
    for key in ("CoreAnimationFramesPerSecond", "FramesPerSecond", "fps", "FPS"):
        value = selector.get(key)
        if value is None:
            continue
        try:
            fps = float(value)
        except (TypeError, ValueError):
            continue
        if fps >= 0:
            return fps
    return None


def recent_interval_history(previous_intervals_ms, intervals_ms):
    return shared_recent_interval_history(previous_intervals_ms, intervals_ms)


def perfdog_jank_counts(intervals_ms, previous_intervals_ms=None):
    return shared_perfdog_jank_counts(intervals_ms, previous_intervals_ms)


def representative_frame_time_ms(intervals_ms, average_frame_time_ms):
    values = sorted(value for value in intervals_ms if value > 0)
    if not values:
        return average_frame_time_ms
    index = min(len(values) - 1, int(math.ceil(len(values) * 0.95)) - 1)
    return max(average_frame_time_ms, values[index])


def ordered_display_metrics(intervals_ms, previous_intervals_ms=None):
    metrics = ordered_frame_metrics(intervals_ms, previous_intervals_ms)
    if metrics is None:
        return None
    return metrics.fps, metrics.jank, metrics.big_jank, metrics.frame_time_p95_ms


def non_negative_number(value):
    if value is None or isinstance(value, bool):
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    if not math.isfinite(number) or number < 0:
        return None
    return number


def process_memory_payload(pid, footprint_bytes, rss_bytes):
    footprint = non_negative_number(footprint_bytes)
    rss = non_negative_number(rss_bytes)
    if footprint is not None and footprint <= 0:
        footprint = None
    if rss is not None and rss <= 0:
        rss = None
    if footprint is None and rss is None:
        return None
    fallback = footprint is None
    value = rss if fallback else footprint
    payload = {
        "platform": "ios",
        "pid": pid,
        "value": value / 1024 / 1024,
        "unit": "MB",
        "metric": "rss" if fallback else "physical_footprint",
        "fallback": fallback,
        "source": "pyidevice-sysmontap",
        "scope": "process",
    }
    if rss is not None:
        payload["rss_value"] = rss / 1024 / 1024
    return payload


def ordered_source_is_fresh(*sample_times, now=None):
    available = [float(value) for value in sample_times if value is not None]
    if not available:
        return False
    current = time.monotonic() if now is None else float(now)
    return current - max(available) < ORDERED_FPS_FALLBACK_SECONDS


def sysmontap_process_values(attributes, process):
    if isinstance(process, dict):
        return process
    if isinstance(process, (list, tuple)):
        return dict(zip(attributes, process))
    return {}


def find_sysmontap_process(rows, pid, attributes):
    if not isinstance(rows, list):
        return None
    for row in rows:
        processes = row.get("Processes") if isinstance(row, dict) else None
        if not isinstance(processes, dict):
            continue
        for raw_pid, process in processes.items():
            try:
                process_pid = int(raw_pid)
            except (TypeError, ValueError):
                continue
            if process_pid == pid:
                return sysmontap_process_values(attributes, process)
    return None


def process_name_matches(expected_name, observed_name):
    expected = str(expected_name or "").strip()
    observed = str(observed_name or "").strip()
    return not expected or bool(observed and expected == observed)


def is_valid_pending_rebind_pid(raw_pid, active_pid, pending_rebind_pid):
    """Return whether a Sysmontap row is an eligible verified rebind target.

    ``pending_rebind_pid == 0`` is the sentinel for no pending candidate.  It
    must never match a real Sysmontap row because PID 0 is ``kernel_task`` on
    iOS and is not an application process.
    """

    return (
        pending_rebind_pid > 0
        and raw_pid == pending_rebind_pid
        and active_pid != pending_rebind_pid
    )


def run_legacy_metrics(
    udid,
    pid,
    interval_ms,
    collect_fps,
    stop_event,
    target_name="",
    collect_cpu=True,
    collect_memory=True,
    collect_thermal_state=False,
    target_bundle_id="",
):
    _load_legacy_ios_device_runtime()
    attrs = ["pid", "name"]
    if collect_cpu:
        attrs.append("cpuUsage")
    if collect_memory:
        attrs.extend(["physFootprint", "memResidentSize"])
    sysmontap_started = False
    graphics_started = False
    core_profile_started = False
    thermal_state_started = False
    thermal_state_notice_reported = False
    ordered_started_at = None
    last_ordered_sample_at = None
    last_ordered_frame_at = None
    ordered_accumulator = None
    source_origin_timestamp = None
    tick_to_seconds = None
    last_source_elapsed_sec = 0.0
    source_sequence = 0
    idle_tracker = IdleFrameWindowTracker(max(0.25, float(interval_ms) / 1000.0))
    state_lock = threading.Lock()
    last_target_seen_at = None
    target_error_emitted = False
    target_confirmed = False
    active_pid = pid
    pending_rebind_pid = 0
    last_rebind_scan_at = 0.0
    missing_status_reported = False
    allow_rebind = bool(
        target_bundle_id
        and target_name
        and not is_webkit_process(target_name)
    )

    def set_target_confirmation(confirmed):
        nonlocal target_confirmed, last_ordered_sample_at, last_ordered_frame_at
        value = bool(confirmed)
        if target_confirmed == value:
            return
        target_confirmed = value
        emit("target", {"platform": "ios", "pid": active_pid, "confirmed": value})
        if value:
            return
        with state_lock:
            last_ordered_sample_at = None
            last_ordered_frame_at = None
            idle_tracker.last_frame_at = None
            idle_tracker.last_zero_at = None
            if ordered_accumulator is not None:
                ordered_accumulator.reset_sequence()

    def on_sysmontap_message(res):
        nonlocal last_target_seen_at, target_error_emitted, target_confirmed
        nonlocal active_pid, pending_rebind_pid, last_rebind_scan_at, missing_status_reported
        if not isinstance(res.selector, list):
            return
        rows = deepcopy(res.selector)
        has_process_snapshot = False
        for row in rows:
            if not isinstance(row, dict) or not isinstance(row.get("Processes"), dict):
                continue
            has_process_snapshot = True
            for raw_pid, process in row["Processes"].items():
                try:
                    raw_pid_int = int(raw_pid)
                except Exception:
                    raw_pid_int = raw_pid
                is_pending_rebind = is_valid_pending_rebind_pid(
                    raw_pid_int,
                    active_pid,
                    pending_rebind_pid,
                )
                if raw_pid_int != active_pid and not is_pending_rebind:
                    continue
                values = sysmontap_process_values(attrs, process)
                if is_pending_rebind:
                    old_pid = active_pid
                    active_pid = pending_rebind_pid
                    pending_rebind_pid = 0
                    last_target_seen_at = time.monotonic()
                    missing_status_reported = False
                    set_target_confirmation(True)
                    emit(
                        "target_rebound",
                        {
                            "platform": "ios",
                            "old_pid": old_pid,
                            "new_pid": active_pid,
                            "name": target_name,
                            "bundle_id": target_bundle_id,
                            "reason": "application_identity_verified",
                        },
                    )
                observed_name = values.get("name")
                if target_name and not str(observed_name or "").strip():
                    if (
                        last_target_seen_at is None
                        or time.monotonic() - last_target_seen_at >= TARGET_CONFIRMATION_GRACE_SECONDS
                    ):
                        set_target_confirmation(False)
                    continue
                if not process_name_matches(target_name, observed_name):
                    target_error_emitted = True
                    set_target_confirmation(False)
                    stop_event.set()
                    emit(
                        "fatal",
                        {
                            "pid": active_pid,
                            "code": "target_process_mismatch",
                            "message": (
                                "所选 iOS pid %s 当前属于 %s，不是已选择的 %s，采集已停止，请重新选择进程。"
                                % (active_pid, observed_name, target_name)
                            ),
                        },
                    )
                    return
                last_target_seen_at = time.monotonic()
                set_target_confirmation(True)
                cpu = non_negative_number(values.get("cpuUsage")) if collect_cpu else None
                memory_payload = (
                    process_memory_payload(active_pid, values.get("physFootprint"), values.get("memResidentSize"))
                    if collect_memory
                    else None
                )
                if collect_cpu and cpu is not None:
                    emit("cpu", {"platform": "ios", "pid": active_pid, "value": cpu, "unit": "%", "semantics": "raw_process_cpu", "source": "pyidevice-sysmontap", "scope": "process"})
                if collect_memory and memory_payload is not None:
                    emit("memory", memory_payload)
                return
        if (
            has_process_snapshot
            and (
                last_target_seen_at is None
                or time.monotonic() - last_target_seen_at >= TARGET_CONFIRMATION_GRACE_SECONDS
            )
        ):
            set_target_confirmation(False)
            if allow_rebind and not missing_status_reported:
                missing_status_reported = True
                emit("status", {"pid": active_pid, "message": "等待 %s 主进程恢复，进程指标暂不可用。" % target_name})

    def on_graphics_message(res):
        if not target_confirmed:
            return
        with state_lock:
            if ordered_source_is_fresh(last_ordered_frame_at, last_ordered_sample_at):
                return
        fps = fps_from_selector(getattr(res, "selector", None))
        if fps is None:
            return
        emit(
            "fps",
            {
                "platform": "ios",
                "pid": active_pid,
                "fps": fps,
                "source": "pyidevice-graphics-global",
                "scope": "screen",
                "ordered_frames": False,
                "frame_source": "sampled-screen-fps",
            },
        )

    def on_core_profile_message(res):
        nonlocal last_ordered_sample_at, last_ordered_frame_at, source_origin_timestamp, last_source_elapsed_sec, source_sequence
        selector = getattr(res, "selector", None)
        if type(selector) is not InstrumentRPCParseError:
            return
        try:
            rows = kperf_data(selector.data)
        except Exception:
            return
        for args in rows:
            try:
                frame_time, code = args[0], args[7]
            except Exception:
                continue
            if kdbg_extract_all(code) != IOS_DISPLAY_FRAME_EVENT:
                continue
            if source_origin_timestamp is None:
                source_origin_timestamp = frame_time
            source_elapsed_sec = (
                max(0.0, (frame_time - source_origin_timestamp) * tick_to_seconds)
                if tick_to_seconds is not None
                else None
            )
            if not target_confirmed:
                continue
            if ordered_accumulator is None:
                continue
            received_at = time.monotonic()
            payload = None
            with state_lock:
                if source_elapsed_sec is not None:
                    last_source_elapsed_sec = source_elapsed_sec
                window = ordered_accumulator.add_timestamp(frame_time)
                if ordered_accumulator.last_event_breaks_sequence:
                    last_ordered_sample_at = None
                    last_ordered_frame_at = None
                    idle_tracker.last_frame_at = None
                    idle_tracker.last_zero_at = None
                elif ordered_accumulator.last_event_accepted:
                    last_ordered_frame_at = received_at
                    idle_tracker.mark_frame(received_at)
                if window is not None:
                    last_ordered_sample_at = received_at
                    source_sequence += 1
                    payload = {
                        "platform": "ios",
                "pid": active_pid,
                        **ordered_frame_payload(window),
                        "source": "pyidevice-coreprofile-display",
                        "scope": "screen",
                        "frame_source": "iomfb-swap-on-glass",
                        "display_event": IOS_DISPLAY_FRAME_EVENT_NAME,
                        "display_event_id": hex(IOS_DISPLAY_FRAME_EVENT_ID),
                        "source_elapsed_sec": source_elapsed_sec,
                        "source_sequence": source_sequence,
                    }
            if payload is None:
                continue
            emit("fps", payload)

    def start_graphics_fallback(rpc):
        nonlocal graphics_started
        if graphics_started:
            return
        rpc.instruments.register_channel_callback(InstrumentsService.GraphicsOpengl, on_graphics_message)
        try:
            rpc.instruments.call(InstrumentsService.GraphicsOpengl, "availableStatistics")
            rpc.instruments.call(InstrumentsService.GraphicsOpengl, "driverNames")
        except Exception as exc:
            emit("status", {"pid": pid, "message": "屏幕 FPS 元数据读取失败，继续尝试采集：" + str(exc)})
        rpc.instruments.call(InstrumentsService.GraphicsOpengl, "setSamplingRate:", float(interval_ms / 100))
        rpc.instruments.call(InstrumentsService.GraphicsOpengl, "startSamplingAtTimeInterval:", 0.0)
        graphics_started = True
        emit("status", {"pid": pid, "message": "已回退到屏幕 FPS 采样；FPS 不按 PID 过滤，Jank 不从 FPS 估算。"})

    rpc = None
    try:
        with InstrumentsBase(udid=udid) as rpc:
            rpc.instruments.register_channel_callback(InstrumentsService.Sysmontap, on_sysmontap_message)
            rpc.instruments.call(
                InstrumentsService.Sysmontap,
                "setConfig:",
                {
                    "ur": interval_ms,
                    "bm": 0,
                    "cpuUsage": bool(collect_cpu),
                    "sampleInterval": interval_ms * 1000000,
                    "procAttrs": attrs,
                },
            )
            rpc.instruments.call(InstrumentsService.Sysmontap, "start")
            sysmontap_started = True
            last_target_seen_at = time.monotonic()
            enabled_process_metrics = []
            if collect_cpu:
                enabled_process_metrics.append("CPU")
            if collect_memory:
                enabled_process_metrics.append("内存")
            process_source_label = "/".join(enabled_process_metrics) if enabled_process_metrics else "身份校验"
            emit("status", {"pid": pid, "message": "PID %s已启动。" % process_source_label})

            if collect_thermal_state:
                try:
                    rpc.instruments.call(InstrumentsService.XcodeEnergy, "startSamplingForPIDs:", {pid})
                    thermal_state_started = True
                except Exception as exc:
                    emit("status", {"pid": pid, "message": "iOS Thermal State 不可用，其他指标继续采集：" + str(exc)})

            if collect_fps:
                try:
                    mach_time_info = rpc.instruments.call(InstrumentsService.DeviceInfo, "machTimeInfo").selector
                    mach_time_factor = mach_time_info[1] / mach_time_info[2]
                    tick_to_seconds = mach_time_factor / NANO_SECOND
                    ordered_accumulator = OrderedTimestampAccumulator(
                        tick_to_nanoseconds=mach_time_factor,
                        window_ms=max(250.0, float(interval_ms)),
                    )
                    rpc.instruments.register_channel_callback(InstrumentsService.CoreProfileSessionTap, on_core_profile_message)
                    rpc.instruments.call(
                        InstrumentsService.CoreProfileSessionTap,
                        "setConfig:",
                        {
                            "rp": 10,
                            "tc": [
                                {
                                    "kdf2": {630784000, 833617920, 830472456},
                                    "tk": 3,
                                    "uuid": str(uuid.uuid4()).upper(),
                                }
                            ],
                            "ur": 500,
                        },
                    )
                    rpc.instruments.call(InstrumentsService.CoreProfileSessionTap, "start")
                    core_profile_started = True
                    ordered_started_at = time.monotonic()
                    emit("status", {"pid": pid, "message": "iOS 有序 Display FrameTime 采集已启动；FPS/Jank 为屏幕级数据。"})
                except Exception as exc:
                    emit("status", {"pid": pid, "message": "iOS 有序帧源不可用，尝试回退屏幕 FPS：" + str(exc)})
                    try:
                        start_graphics_fallback(rpc)
                    except Exception as graphics_exc:
                        emit("status", {"pid": pid, "message": "屏幕 FPS 采集不可用：" + str(graphics_exc)})

            while not stop_event.wait(1):
                if thermal_state_started and target_confirmed:
                    try:
                        response = rpc.instruments.call(
                            InstrumentsService.XcodeEnergy,
                            "sampleAttributes:forPIDs:",
                            {},
                            {active_pid},
                        )
                        payload = getattr(response, "selector", None)
                        parsed = parse_ios_thermal_state(payload, active_pid)
                        if parsed is not None:
                            level, name = parsed
                            emit(
                                "thermal_state",
                                {
                                    "platform": "ios",
                                    "pid": active_pid,
                                    "value": level,
                                    "state": name,
                                    "source": "pyidevice-xcode-energy",
                                    "scope": "device",
                                    "semantics": "process_info_thermal_state",
                                },
                            )
                        elif not thermal_state_notice_reported:
                            thermal_state_notice_reported = True
                            reason = ios_thermal_state_unavailable_reason(payload, active_pid)
                            emit(
                                "status",
                                {
                                    "pid": pid,
                                    "message": (
                                        "当前 iOS 设备/系统未返回 Thermal State 数据，已保留为空；"
                                        "其他指标继续采集（原因：%s）。" % (reason or "invalid_value")
                                    ),
                                },
                            )
                    except Exception as exc:
                        thermal_state_started = False
                        emit("status", {"pid": pid, "message": "iOS Thermal State 采集中断，其他指标继续采集：" + str(exc)})
                if (
                    allow_rebind
                    and last_target_seen_at is not None
                    and time.monotonic() - last_target_seen_at >= TARGET_CONFIRMATION_GRACE_SECONDS
                    and time.monotonic() - last_rebind_scan_at >= TARGET_REBIND_SCAN_SECONDS
                ):
                    last_rebind_scan_at = time.monotonic()
                    pending_rebind_pid = 0
                    try:
                        candidate = find_application_rebind_candidate(
                            rpc.device_info.runningProcesses(),
                            active_pid,
                            target_name,
                            target_bundle_id,
                        )
                    except Exception:
                        candidate = None
                    if candidate is not None:
                        try:
                            candidate_pid = int(candidate.get("pid") or 0)
                        except (TypeError, ValueError):
                            candidate_pid = 0
                        if candidate_pid > 0:
                            pending_rebind_pid = candidate_pid
                if (
                    target_confirmed
                    and last_target_seen_at is not None
                    and time.monotonic() - last_target_seen_at >= TARGET_CONFIRMATION_GRACE_SECONDS
                ):
                    set_target_confirmation(False)
                if (
                    sysmontap_started
                    and not target_error_emitted
                    and last_target_seen_at is not None
                    and not allow_rebind
                    and time.monotonic() - last_target_seen_at >= TARGET_PROCESS_TIMEOUT_SECONDS
                ):
                    target_error_emitted = True
                    set_target_confirmation(False)
                    stop_event.set()
                    emit(
                        "fatal",
                        {
                            "pid": active_pid,
                            "code": "target_process_missing",
                            "message": "未检测到所选 iOS pid %s，采集已停止，请刷新并重新选择当前运行的进程。" % active_pid,
                        },
                    )
                    break
                idle_window_seconds = None
                idle_payload = None
                if collect_fps and target_confirmed and core_profile_started and ordered_accumulator is not None:
                    now = time.monotonic()
                    with state_lock:
                        idle_window_seconds = idle_tracker.take_due_window(now)
                        if idle_window_seconds is not None:
                            ordered_accumulator.reset_sequence()
                            last_ordered_sample_at = now
                            source_sequence += 1
                            source_elapsed_sec = last_source_elapsed_sec + max(
                                0.0,
                                now - (idle_tracker.last_frame_at or now),
                            )
                            idle_payload = {
                                "platform": "ios",
                                "pid": pid,
                                **ordered_zero_fps_payload(idle_window_seconds),
                                "source": "pyidevice-coreprofile-display",
                                "scope": "screen",
                                "frame_source": "iomfb-swap-on-glass",
                                "display_event": IOS_DISPLAY_FRAME_EVENT_NAME,
                                "display_event_id": hex(IOS_DISPLAY_FRAME_EVENT_ID),
                                "source_elapsed_sec": source_elapsed_sec,
                                "source_sequence": source_sequence,
                            }
                    if idle_payload is not None:
                        emit("fps", idle_payload)
                if collect_fps and target_confirmed and core_profile_started and not graphics_started:
                    with state_lock:
                        has_ordered_sample = ordered_source_is_fresh(last_ordered_frame_at, last_ordered_sample_at)
                    if (
                        not has_ordered_sample
                        and ordered_started_at is not None
                        and time.monotonic() - ordered_started_at >= ORDERED_FPS_FALLBACK_SECONDS
                    ):
                        try:
                            start_graphics_fallback(rpc)
                        except Exception as exc:
                            emit("status", {"pid": pid, "message": "屏幕 FPS 回退不可用：" + str(exc)})
                if collect_fps and graphics_started:
                    with state_lock:
                        has_ordered_sample = ordered_source_is_fresh(last_ordered_frame_at, last_ordered_sample_at)
                    if has_ordered_sample:
                        try:
                            rpc.instruments.call(InstrumentsService.GraphicsOpengl, "stopSampling")
                        except Exception:
                            pass
                        graphics_started = False
    except Exception as exc:
        if not stop_event.is_set():
            set_target_confirmation(False)
            stop_event.set()
            emit(
                "fatal",
                {
                    "pid": pid,
                    "code": "ios_legacy_metrics_failed",
                    "message": "iOS CPU/内存采集失败，采集已停止：" + str(exc),
                },
            )
    finally:
        try:
            if rpc is not None and thermal_state_started:
                rpc.instruments.call(InstrumentsService.XcodeEnergy, "stopSamplingForPIDs:", {pid})
        except Exception:
            pass
        try:
            if rpc is not None and graphics_started:
                rpc.instruments.call(InstrumentsService.GraphicsOpengl, "stopSampling")
        except Exception:
            pass
        try:
            if rpc is not None and core_profile_started:
                rpc.instruments.call(InstrumentsService.CoreProfileSessionTap, "stop")
        except Exception:
            pass
        try:
            if rpc is not None and sysmontap_started:
                rpc.instruments.call(InstrumentsService.Sysmontap, "stop")
        except Exception:
            pass


def ios_major_version(version):
    if version is None:
        return None
    text = str(version).strip()
    if not text:
        return None
    try:
        return int(text.split(".", 1)[0])
    except (TypeError, ValueError):
        return None


def run_metrics(
    udid,
    pid,
    interval_ms,
    collect_fps,
    stop_event,
    ios_version="",
    target_name="",
    target_start_abs_time=0,
    target_coalition_id=0,
    target_owner_pid=0,
    target_owner_name="",
    collect_cpu=True,
    collect_memory=True,
    collect_thermal_state=False,
    target_bundle_id="",
):
    major_version = ios_major_version(ios_version)
    if major_version is None:
        try:
            from ios_pmd3_metrics import detect_ios_version

            detected_version = detect_ios_version(udid)
            major_version = ios_major_version(detected_version)
        except Exception as exc:
            emit("status", {"pid": pid, "message": "无法预读 iOS 版本，先尝试兼容模式：" + str(exc)})

    if major_version is not None and major_version >= 17:
        try:
            from ios_pmd3_metrics import run_metrics as run_modern_metrics

            run_modern_metrics(
                udid=udid,
                pid=pid,
                interval_ms=interval_ms,
                collect_fps=collect_fps,
                stop_event=stop_event,
                emit=emit,
                target_name=target_name,
                target_bundle_id=target_bundle_id,
                target_start_abs_time=target_start_abs_time,
                target_coalition_id=target_coalition_id,
                target_owner_pid=target_owner_pid,
                target_owner_name=target_owner_name,
                collect_cpu=collect_cpu,
                collect_memory=collect_memory,
                collect_thermal_state=collect_thermal_state,
            )
        except Exception as exc:
            if not stop_event.is_set():
                stop_event.set()
                emit(
                    "fatal",
                    {
                        "pid": pid,
                        "code": "ios_modern_metrics_failed",
                        "message": "iOS 17+ RSD 采集失败，请确认开发者模式、开发者镜像和 USB 信任状态："
                        + str(exc),
                    },
                )
        return

    run_legacy_metrics(
        udid,
        pid,
        interval_ms,
        collect_fps,
        stop_event,
        target_name=target_name,
        collect_cpu=collect_cpu,
        collect_memory=collect_memory,
        collect_thermal_state=collect_thermal_state,
        target_bundle_id=target_bundle_id,
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--udid", required=True)
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--target-name", default="")
    parser.add_argument("--target-bundle-id", default="")
    parser.add_argument("--target-start-abs-time", default=0, type=int)
    parser.add_argument("--target-coalition-id", default=0, type=int)
    parser.add_argument("--target-owner-pid", default=0, type=int)
    parser.add_argument("--target-owner-name", default="")
    parser.add_argument("--interval", type=int, default=1000)
    parser.add_argument("--ios-version", default="")
    parser.add_argument("--no-fps", action="store_true")
    parser.add_argument("--no-memory", action="store_true")
    parser.add_argument("--no-cpu", action="store_true")
    parser.add_argument("--no-temperature", action="store_true")
    parser.add_argument("--no-thermal-state", action="store_true")
    args = parser.parse_args()

    stop_event = threading.Event()
    signal.signal(signal.SIGTERM, lambda *_: stop_event.set())
    signal.signal(signal.SIGINT, lambda *_: stop_event.set())

    metrics_thread = threading.Thread(
        target=run_metrics,
        args=(
            args.udid,
            args.pid,
            args.interval,
            not args.no_fps,
            stop_event,
            args.ios_version,
            args.target_name,
            args.target_start_abs_time,
            args.target_coalition_id,
            args.target_owner_pid,
            args.target_owner_name,
            not args.no_cpu,
            not args.no_memory,
            not args.no_thermal_state,
            args.target_bundle_id,
        ),
        daemon=True,
    )
    temperature_thread = threading.Thread(
        target=ios_temperature_loop,
        args=(args.udid, args.interval, stop_event),
        daemon=True,
    )
    metrics_thread.start()
    if not args.no_temperature:
        temperature_thread.start()
    while not stop_event.wait(0.5):
        if not metrics_thread.is_alive():
            break
    stop_event.set()
    metrics_thread.join(timeout=2.0)
    if not args.no_temperature:
        temperature_thread.join(timeout=2.0)
    time.sleep(0.2)


if __name__ == "__main__":
    main()
