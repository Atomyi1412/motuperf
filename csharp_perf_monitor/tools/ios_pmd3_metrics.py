from __future__ import annotations

import asyncio
import contextlib
import math
import struct
import time
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from display_metrics import (
    IdleFrameWindowTracker,
    IOS_DISPLAY_FRAME_EVENT_ID,
    IOS_DISPLAY_FRAME_EVENT_NAME,
    OrderedFrameWindow,
    OrderedTimestampAccumulator,
    ordered_frame_payload,
    ordered_zero_fps_payload,
)
from ios_sysmon_schema_cache import load_sysmon_schema
from pmd3_windows_compat import apply_windows_pytcp_cleanup_compat
from temperature_metrics import ios_thermal_state_unavailable_reason, parse_ios_thermal_state
from ios_target_rebinding import find_application_rebind_candidate, is_webkit_process


ORDERED_FPS_FALLBACK_SECONDS = 4.0
GRAPHICS_START_DELAY_SECONDS = 0.0
GRAPHICS_RETRY_SECONDS = 5.0
CORE_PROFILE_RETRY_SECONDS = 5.0
# A healthy display stream should deliver kdebug records continuously while a
# screen is presenting. Reopen a tap that goes silent instead of waiting
# forever and leaving the UI on the FPS-only fallback.
CORE_PROFILE_MESSAGE_TIMEOUT_SECONDS = 6.0
TARGET_PROCESS_TIMEOUT_SECONDS = 8.0
TARGET_CONFIRMATION_GRACE_SECONDS = 2.0
TARGET_REBIND_SCAN_SECONDS = 1.0
KDEBUG_V2_MAGIC = b"\x00\x02\xaa\x55"
KDEBUG_V3_MAGIC = b"\x00\x03\xaa\x55"
KDEBUG_V2_HEADER_FIXED_BYTES = 288
KDEBUG_V2_THREAD_ENTRY_BYTES = 32
KDEBUG_RECORD_BYTES = 64
KDEBUG_EVENT_ID_MASK = 0xFFFFFFFC
_KDEBUG_DISPLAY_RECORD = struct.Struct("<Q40xI12x")


def _display_only_tap_config(tap_uuid: str) -> dict[str, Any]:
    return {
        "tc": [
            {
                "kdf2": {IOS_DISPLAY_FRAME_EVENT_ID},
                "tk": 3,
                "uuid": tap_uuid,
            }
        ],
        "rp": 10,
        "bm": 0,
        "ur": 500,
    }


def _pmd3_imports() -> dict[str, Any]:
    apply_windows_pytcp_cleanup_compat()
    from pymobiledevice3.lockdown import create_using_usbmux
    from pymobiledevice3.remote.userspace_tunnel import UserspaceRsdTunnel
    from pymobiledevice3.services.dvt.instruments.core_profile_session_tap import CoreProfileSessionTap
    from pymobiledevice3.services.dvt.instruments.device_info import DeviceInfo
    from pymobiledevice3.services.dvt.instruments.dvt_provider import DvtProvider
    from pymobiledevice3.services.dvt.instruments.energy_monitor import EnergyMonitor
    from pymobiledevice3.services.dvt.instruments.graphics import Graphics
    from pymobiledevice3.services.dvt.instruments.sysmontap import Sysmontap

    return {
        "CoreProfileSessionTap": CoreProfileSessionTap,
        "DeviceInfo": DeviceInfo,
        "DvtProvider": DvtProvider,
        "EnergyMonitor": EnergyMonitor,
        "Graphics": Graphics,
        "Sysmontap": Sysmontap,
        "UserspaceRsdTunnel": UserspaceRsdTunnel,
        "create_using_usbmux": create_using_usbmux,
    }


def _non_negative_number(value: Any) -> float | None:
    if value is None or isinstance(value, bool):
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    if not math.isfinite(number) or number < 0:
        return None
    return number


def _integer(value: Any) -> int:
    if value is None or isinstance(value, bool):
        return 0
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def _target_identity_error(
    process: dict[str, Any],
    processes_by_pid: dict[int, dict[str, Any]],
    pid: int,
    expected_name: str,
    expected_start_abs_time: int,
    expected_coalition_id: int,
    expected_owner_pid: int,
    expected_owner_name: str,
) -> tuple[str, str] | None:
    observed_name = str(process.get("name") or "").strip()
    if expected_name and observed_name != expected_name:
        return (
            "target_process_mismatch",
            f"所选 iOS pid {pid} 当前属于 {observed_name or '未知进程'}，不是已选择的 {expected_name}，采集已停止，请重新选择进程。",
        )

    observed_start = _integer(process.get("startAbsTime"))
    if expected_start_abs_time > 0 and observed_start != expected_start_abs_time:
        return (
            "target_process_identity_changed",
            f"所选 iOS pid {pid} 的启动身份已变化，采集已停止，请刷新并重新选择当前进程。",
        )

    observed_coalition = _integer(process.get("coalitionID"))
    if expected_coalition_id > 0 and observed_coalition != expected_coalition_id:
        return (
            "target_process_identity_changed",
            f"所选 iOS pid {pid} 的进程归属已变化，采集已停止，请刷新并重新选择当前进程。",
        )

    if expected_owner_pid > 0:
        owner = processes_by_pid.get(expected_owner_pid)
        if owner is None:
            return (
                "target_process_owner_changed",
                f"未检测到所选 iOS 子进程的宿主 pid {expected_owner_pid}，采集已停止，请重新选择应用和进程。",
            )
        observed_owner_name = str(owner.get("name") or "").strip()
        if expected_owner_name and observed_owner_name != expected_owner_name:
            return (
                "target_process_owner_changed",
                f"所选 iOS 子进程的宿主 pid {expected_owner_pid} 已变为 {observed_owner_name or '未知进程'}，采集已停止。",
            )
        owner_coalition = _integer(owner.get("coalitionID"))
        target_coalition = expected_coalition_id or observed_coalition
        if target_coalition > 0 and owner_coalition != target_coalition:
            return (
                "target_process_owner_changed",
                f"所选 iOS 子进程与宿主 pid {expected_owner_pid} 已不属于同一进程组，采集已停止。",
            )
    return None


def _memory_payload(pid: int, footprint_bytes: Any, rss_bytes: Any) -> dict[str, Any] | None:
    footprint = _non_negative_number(footprint_bytes)
    rss = _non_negative_number(rss_bytes)
    if footprint is not None and footprint <= 0:
        footprint = None
    if rss is not None and rss <= 0:
        rss = None
    if footprint is None and rss is None:
        return None
    fallback = footprint is None
    value = rss if fallback else footprint
    assert value is not None
    payload: dict[str, Any] = {
        "platform": "ios",
        "pid": pid,
        "value": value / 1024.0 / 1024.0,
        "unit": "MB",
        "metric": "rss" if fallback else "physical_footprint",
        "fallback": fallback,
        "source": "pymobiledevice3-sysmontap-rsd",
        "scope": "process",
    }
    if rss is not None:
        payload["rss_value"] = rss / 1024.0 / 1024.0
    return payload


def _fps_from_graphics_event(value: Any) -> float | None:
    pending = [value]
    while pending:
        current = pending.pop()
        if isinstance(current, dict):
            for key in ("CoreAnimationFramesPerSecond", "FramesPerSecond", "fps", "FPS"):
                fps = _non_negative_number(current.get(key))
                if fps is not None:
                    return fps
            pending.extend(current.values())
        elif isinstance(current, (list, tuple)):
            pending.extend(current)
    return None


async def _detect_ios_version(udid: str) -> str:
    imports = _pmd3_imports()
    lockdown = await imports["create_using_usbmux"](serial=udid, autopair=False)
    try:
        return str(getattr(lockdown, "product_version", "") or "")
    finally:
        await lockdown.close()


def detect_ios_version(udid: str) -> str:
    return asyncio.run(_detect_ios_version(udid))


def _selected_processes_from_sysmontap_row(
    row: Any,
    attributes: list[str],
    selected_pids: set[int],
) -> dict[int, dict[str, Any]]:
    if not isinstance(row, dict) or not isinstance(row.get("Processes"), dict):
        return {}
    selected: dict[int, dict[str, Any]] = {}
    for raw_pid, raw_process in row["Processes"].items():
        process_pid = _integer(raw_pid)
        if process_pid <= 0 or process_pid not in selected_pids:
            continue
        if isinstance(raw_process, dict):
            process = dict(raw_process)
        elif isinstance(raw_process, (list, tuple)):
            process = dict(zip(attributes, raw_process))
        else:
            continue
        process.setdefault("pid", process_pid)
        selected[process_pid] = process
    return selected


async def _iter_selected_sysmontap_processes(
    sysmontap: Any,
    attributes: list[str],
    selected_pids: set[int],
):
    if hasattr(sysmontap, "__aiter__"):
        async for row in sysmontap:
            yield _selected_processes_from_sysmontap_row(row, attributes, selected_pids)
        return

    async for processes in sysmontap.iter_processes():
        selected: dict[int, dict[str, Any]] = {}
        for process in processes:
            if not isinstance(process, dict):
                continue
            process_pid = _integer(process.get("pid"))
            if process_pid > 0 and process_pid in selected_pids:
                selected[process_pid] = process
        yield selected


async def _find_rebind_candidate(
    dvt: Any,
    imports: dict[str, Any],
    current_pid: int,
    expected_name: str,
    expected_bundle_id: str,
) -> dict[str, Any] | None:
    device_info_type = imports.get("DeviceInfo")
    if device_info_type is None:
        return None
    try:
        async with device_info_type(dvt) as device_info:
            processes = await device_info.proclist()
    except Exception:
        return None
    return find_application_rebind_candidate(
        processes,
        current_pid,
        expected_name,
        expected_bundle_id,
    )


@dataclass
class _SourceState:
    last_ordered_sample_at: float | None = None
    last_ordered_frame_at: float | None = None
    target_confirmed: bool = False
    target_generation: int = 0

    def set_target_confirmed(self, confirmed: bool) -> None:
        value = bool(confirmed)
        if self.target_confirmed == value:
            return
        self.target_confirmed = value
        self.target_generation += 1
        if not value:
            self.last_ordered_sample_at = None
            self.last_ordered_frame_at = None

    def ordered_is_fresh(self, now: float) -> bool:
        recent = [
            timestamp
            for timestamp in (self.last_ordered_frame_at, self.last_ordered_sample_at)
            if timestamp is not None
        ]
        return bool(recent) and now - max(recent) < ORDERED_FPS_FALLBACK_SECONDS

    def mark_ordered_frame(self, received_at: float) -> None:
        self.last_ordered_frame_at = float(received_at)

    def mark_ordered_integrity_break(self) -> None:
        self.last_ordered_sample_at = None
        self.last_ordered_frame_at = None


def _should_start_graphics_fallback(
    *,
    collect_fps: bool,
    state: _SourceState,
    now: float,
    next_retry_at: float,
    graphics_task: asyncio.Task[Any] | None,
) -> bool:
    if not collect_fps or not state.target_confirmed or state.ordered_is_fresh(now):
        return False
    if now < next_retry_at:
        return False
    return graphics_task is None or graphics_task.done()


class _KdebugV2DisplayScanner:
    def __init__(self, event_id: int = IOS_DISPLAY_FRAME_EVENT_ID) -> None:
        self._event_id = event_id
        self._started = False
        self._header_buffer = b""
        self._record_carry = b""

    @staticmethod
    def _header_size(data: bytes) -> int | None:
        if len(data) < 8:
            return None
        thread_count = struct.unpack_from("<I", data, 4)[0]
        unaligned_size = KDEBUG_V2_HEADER_FIXED_BYTES + thread_count * KDEBUG_V2_THREAD_ENTRY_BYTES
        return (unaligned_size + KDEBUG_RECORD_BYTES - 1) // KDEBUG_RECORD_BYTES * KDEBUG_RECORD_BYTES

    def feed(self, data: Any) -> list[int]:
        if not isinstance(data, (bytes, bytearray, memoryview)):
            return []
        chunk = bytes(data)
        if not chunk or chunk.startswith(b"bplist"):
            return []

        if not self._started:
            if self._header_buffer:
                self._header_buffer += chunk
            elif (
                chunk.startswith(KDEBUG_V2_MAGIC)
                or chunk.startswith(KDEBUG_V3_MAGIC)
                or KDEBUG_V2_MAGIC.startswith(chunk)
                or KDEBUG_V3_MAGIC.startswith(chunk)
            ):
                self._header_buffer = chunk
            else:
                return []

            if len(self._header_buffer) < len(KDEBUG_V2_MAGIC):
                return []
            if self._header_buffer.startswith(KDEBUG_V3_MAGIC):
                raise RuntimeError("CoreProfile returned unsupported kdebug v3 data")
            if not self._header_buffer.startswith(KDEBUG_V2_MAGIC):
                self._header_buffer = b""
                return []

            header_size = self._header_size(self._header_buffer)
            if header_size is None or len(self._header_buffer) < header_size:
                return []
            chunk = self._header_buffer[header_size:]
            self._header_buffer = b""
            self._started = True

        payload = self._record_carry + chunk
        complete_size = len(payload) // KDEBUG_RECORD_BYTES * KDEBUG_RECORD_BYTES
        self._record_carry = payload[complete_size:]
        timestamps: list[int] = []
        for timestamp, debug_id in _KDEBUG_DISPLAY_RECORD.iter_unpack(payload[:complete_size]):
            if debug_id & KDEBUG_EVENT_ID_MASK == self._event_id:
                timestamps.append(timestamp)
        return timestamps


def _ordered_payload(
    pid: int,
    window: OrderedFrameWindow,
    source_elapsed_sec: float | None = None,
    source_sequence: int | None = None,
) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "platform": "ios",
        "pid": pid,
        **ordered_frame_payload(window),
        "source": "pymobiledevice3-coreprofile-rsd-display",
        "scope": "screen",
        "frame_source": "iomfb-swap-on-glass",
        "display_event": IOS_DISPLAY_FRAME_EVENT_NAME,
        "display_event_id": hex(IOS_DISPLAY_FRAME_EVENT_ID),
    }
    if source_elapsed_sec is not None:
        payload["source_elapsed_sec"] = source_elapsed_sec
    if source_sequence is not None:
        payload["source_sequence"] = source_sequence
    return payload


def _ordered_zero_payload(
    pid: int,
    window_seconds: float,
    source_elapsed_sec: float,
    source_sequence: int,
) -> dict[str, Any]:
    return {
        "platform": "ios",
        "pid": pid,
        **ordered_zero_fps_payload(window_seconds),
        "source": "pymobiledevice3-coreprofile-rsd-display",
        "scope": "screen",
        "frame_source": "iomfb-swap-on-glass",
        "display_event": IOS_DISPLAY_FRAME_EVENT_NAME,
        "display_event_id": hex(IOS_DISPLAY_FRAME_EVENT_ID),
        "source_elapsed_sec": source_elapsed_sec,
        "source_sequence": source_sequence,
    }


async def _create_sysmontap(
    dvt: Any,
    interval_ms: int,
    imports: dict[str, Any],
    schema_udid: str = "",
) -> Any:
    cached_schema = load_sysmon_schema(schema_udid) if schema_udid else None
    if cached_schema is not None:
        process_attributes, system_attributes = cached_schema
        return imports["Sysmontap"](
            dvt,
            process_attributes,
            system_attributes,
            interval_ms=interval_ms,
        )
    return await imports["Sysmontap"].create(dvt, interval=interval_ms)


async def _run_sysmontap(
    dvt: Any,
    pid: int,
    interval_ms: int,
    stop_event: Any,
    emit: Callable[[str, dict[str, Any]], None],
    imports: dict[str, Any],
    expected_name: str = "",
    expected_bundle_id: str = "",
    expected_start_abs_time: int = 0,
    expected_coalition_id: int = 0,
    expected_owner_pid: int = 0,
    expected_owner_name: str = "",
    target_timeout_seconds: float = TARGET_PROCESS_TIMEOUT_SECONDS,
    state: _SourceState | None = None,
    collect_cpu: bool = True,
    collect_memory: bool = True,
    schema_udid: str = "",
) -> None:
    # Current iOS versions return rows in the device's canonical full schema.
    sysmontap = await _create_sysmontap(dvt, interval_ms, imports, schema_udid)
    fields = getattr(getattr(sysmontap, "process_attributes_cls", None), "__dataclass_fields__", {})
    if "pid" not in fields:
        raise RuntimeError("sysmontap does not expose a pid attribute on this device")
    attributes = list(fields)

    expected_name = str(expected_name or "").strip()
    expected_bundle_id = str(expected_bundle_id or "").strip()
    expected_start_abs_time = max(0, _integer(expected_start_abs_time))
    expected_coalition_id = max(0, _integer(expected_coalition_id))
    expected_owner_pid = max(0, _integer(expected_owner_pid))
    expected_owner_name = str(expected_owner_name or "").strip()
    active_pid = pid
    allow_rebind = bool(
        expected_bundle_id
        and expected_name
        and expected_owner_pid <= 0
        and not is_webkit_process(expected_name)
    )
    selected_pids = {pid}
    if expected_owner_pid > 0:
        selected_pids.add(expected_owner_pid)
    last_target_seen_at = time.monotonic()
    last_rebind_scan_at = 0.0
    pending_rebind_pid = 0
    missing_status_reported = False
    target_event_state = state.target_confirmed if state is not None else False

    def report_target_confirmation(confirmed: bool) -> None:
        nonlocal target_event_state
        value = bool(confirmed)
        if target_event_state == value:
            return
        target_event_state = value
        emit("target", {"platform": "ios", "pid": active_pid, "confirmed": value})

    async with sysmontap:
        async for processes_by_pid in _iter_selected_sysmontap_processes(
            sysmontap,
            attributes,
            selected_pids,
        ):
            if stop_event.is_set():
                return
            process = processes_by_pid.get(active_pid)
            if process is None and pending_rebind_pid > 0:
                candidate_process = processes_by_pid.get(pending_rebind_pid)
                if candidate_process is not None:
                    old_pid = active_pid
                    active_pid = pending_rebind_pid
                    pending_rebind_pid = 0
                    observed_start = _integer(candidate_process.get("startAbsTime"))
                    observed_coalition = _integer(candidate_process.get("coalitionID"))
                    if observed_start > 0:
                        expected_start_abs_time = observed_start
                    if observed_coalition > 0:
                        expected_coalition_id = observed_coalition
                    last_target_seen_at = time.monotonic()
                    missing_status_reported = False
                    if state is not None:
                        state.set_target_confirmed(True)
                    report_target_confirmation(True)
                    emit(
                        "target_rebound",
                        {
                            "platform": "ios",
                            "old_pid": old_pid,
                            "new_pid": active_pid,
                            "name": expected_name,
                            "bundle_id": expected_bundle_id,
                            "reason": "application_identity_verified",
                        },
                    )
                    process = candidate_process
            target_found = process is not None
            if process is not None:
                identity_error = _target_identity_error(
                    process,
                    processes_by_pid,
                    active_pid,
                    expected_name,
                    expected_start_abs_time,
                    expected_coalition_id,
                    expected_owner_pid,
                    expected_owner_name,
                )
                if identity_error is not None:
                    code, message = identity_error
                    if state is not None:
                        state.set_target_confirmed(False)
                    report_target_confirmation(False)
                    stop_event.set()
                    emit(
                        "fatal",
                        {
                            "pid": active_pid,
                            "code": code,
                            "message": message,
                        },
                    )
                    return
                last_target_seen_at = time.monotonic()
                if state is not None:
                    state.set_target_confirmed(True)
                report_target_confirmation(True)
                cpu = _non_negative_number(process.get("cpuUsage")) if collect_cpu else None
                if collect_cpu and cpu is not None:
                    emit(
                        "cpu",
                        {
                            "platform": "ios",
                            "pid": active_pid,
                            "value": cpu,
                            "unit": "%",
                            "semantics": "raw_process_cpu",
                            "source": "pymobiledevice3-sysmontap-rsd",
                            "scope": "process",
                        },
                    )
                memory = (
                    _memory_payload(active_pid, process.get("physFootprint"), process.get("memResidentSize"))
                    if collect_memory
                    else None
                )
                if collect_memory and memory is not None:
                    emit("memory", memory)
            missing_seconds = time.monotonic() - last_target_seen_at
            if (
                not target_found
                and state is not None
                and missing_seconds >= TARGET_CONFIRMATION_GRACE_SECONDS
            ):
                state.set_target_confirmed(False)
                report_target_confirmation(False)
            if not target_found and allow_rebind and missing_seconds >= TARGET_CONFIRMATION_GRACE_SECONDS:
                now = time.monotonic()
                if now - last_rebind_scan_at >= TARGET_REBIND_SCAN_SECONDS:
                    last_rebind_scan_at = now
                    candidate = await _find_rebind_candidate(
                        dvt,
                        imports,
                        active_pid,
                        expected_name,
                        expected_bundle_id,
                    )
                    if candidate is not None:
                        pending_rebind_pid = _integer(candidate.get("pid"))
                        if pending_rebind_pid > 0:
                            selected_pids.add(pending_rebind_pid)
                    if not missing_status_reported:
                        missing_status_reported = True
                        emit(
                            "status",
                            {
                                "pid": active_pid,
                                "message": f"等待 {expected_name} 主进程恢复，进程指标暂不可用。",
                            },
                        )
            if (
                not target_found
                and not allow_rebind
                and missing_seconds >= max(0.0, target_timeout_seconds)
            ):
                if state is not None:
                    state.set_target_confirmed(False)
                report_target_confirmation(False)
                stop_event.set()
                emit(
                    "fatal",
                    {
                        "pid": active_pid,
                        "code": "target_process_missing",
                        "message": f"未检测到所选 iOS pid {active_pid}，采集已停止，请刷新并重新选择当前运行的进程。",
                    },
                )
                return


async def _run_core_profile(
    dvt: Any,
    pid: int,
    interval_ms: int,
    stop_event: Any,
    emit: Callable[[str, dict[str, Any]], None],
    state: _SourceState,
    imports: dict[str, Any],
) -> None:
    core_profile = imports["CoreProfileSessionTap"]
    time_config = await core_profile.get_time_config(dvt)
    accumulator = OrderedTimestampAccumulator(
        tick_to_nanoseconds=float(time_config["numer"]) / float(time_config["denom"]),
        window_ms=max(250.0, float(interval_ms)),
    )
    scanner = _KdebugV2DisplayScanner()
    tick_to_seconds = float(time_config["numer"]) / float(time_config["denom"]) / 1_000_000_000.0
    source_origin_timestamp: int | None = None
    last_source_elapsed_sec = 0.0
    source_sequence = 0
    idle_tracker = IdleFrameWindowTracker(max(0.25, float(interval_ms) / 1000.0))
    target_generation = state.target_generation

    tap = core_profile(dvt, time_config, {IOS_DISPLAY_FRAME_EVENT_ID})
    tap._config = _display_only_tap_config(tap.uuid)
    async with tap:
        while not stop_event.is_set():
            data = await asyncio.wait_for(
                tap._next_message(),
                timeout=CORE_PROFILE_MESSAGE_TIMEOUT_SECONDS,
            )
            if isinstance(data, bytes) and data.startswith(b"bplist"):
                core_profile._raise_if_tap_start_failed(data)
            received_at = time.monotonic()
            for timestamp in scanner.feed(data):
                if stop_event.is_set():
                    return
                if target_generation != state.target_generation:
                    accumulator.reset_sequence()
                    idle_tracker.last_frame_at = None
                    idle_tracker.last_zero_at = None
                    target_generation = state.target_generation
                if source_origin_timestamp is None:
                    source_origin_timestamp = timestamp
                if not state.target_confirmed:
                    accumulator.reset_sequence()
                    continue
                last_source_elapsed_sec = (timestamp - source_origin_timestamp) * tick_to_seconds
                window = accumulator.add_timestamp(timestamp)
                if accumulator.last_event_breaks_sequence:
                    state.mark_ordered_integrity_break()
                    idle_tracker.last_frame_at = None
                    idle_tracker.last_zero_at = None
                elif accumulator.last_event_accepted:
                    state.mark_ordered_frame(received_at)
                    idle_tracker.mark_frame(received_at)
                if window is not None:
                    if stop_event.is_set() or not state.target_confirmed:
                        return
                    source_sequence += 1
                    state.last_ordered_sample_at = received_at
                    emit(
                        "fps",
                        _ordered_payload(pid, window, last_source_elapsed_sec, source_sequence),
                    )

            if state.target_confirmed and source_origin_timestamp is not None:
                idle_window_seconds = idle_tracker.take_due_window(received_at)
                if idle_window_seconds is not None and idle_tracker.last_frame_at is not None:
                    accumulator.reset_sequence()
                    source_sequence += 1
                    source_elapsed_sec = last_source_elapsed_sec + max(
                        0.0,
                        received_at - idle_tracker.last_frame_at,
                    )
                    state.last_ordered_sample_at = received_at
                    emit(
                        "fps",
                        _ordered_zero_payload(
                            pid,
                            idle_window_seconds,
                            source_elapsed_sec,
                            source_sequence,
                        ),
                    )


async def _run_core_profile_with_retries(
    dvt: Any,
    pid: int,
    interval_ms: int,
    stop_event: Any,
    emit: Callable[[str, dict[str, Any]], None],
    state: _SourceState,
    imports: dict[str, Any],
) -> None:
    """Keep the exact display source recoverable after a transient DVT/kperf failure."""
    attempt = 0
    while not stop_event.is_set():
        attempt += 1
        try:
            await _run_core_profile(dvt, pid, interval_ms, stop_event, emit, state, imports)
            if stop_event.is_set():
                return
            failure = "通道意外结束"
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            failure = str(exc) or exc.__class__.__name__

        state.mark_ordered_integrity_break()
        emit(
            "status",
            {
                "pid": pid,
                "message": (
                    "iOS 有序 Display FrameTime 源第 %s 次不可用：%s；%s 秒后自动重试。"
                    % (attempt, failure, int(CORE_PROFILE_RETRY_SECONDS))
                ),
            },
        )
        await asyncio.sleep(CORE_PROFILE_RETRY_SECONDS)


async def _run_graphics_fallback(
    dvt: Any,
    pid: int,
    stop_event: Any,
    emit: Callable[[str, dict[str, Any]], None],
    state: _SourceState,
    imports: dict[str, Any],
) -> None:
    async with imports["Graphics"](dvt) as graphics:
        primed = False
        async for stats in graphics:
            if stop_event.is_set():
                return
            if not primed:
                primed = True
                continue
            if not state.target_confirmed:
                continue
            if state.ordered_is_fresh(time.monotonic()):
                continue
            fps = _fps_from_graphics_event(stats)
            if fps is None:
                continue
            emit(
                "fps",
                {
                    "platform": "ios",
                    "pid": pid,
                    "fps": fps,
                    "source": "pymobiledevice3-graphics-rsd-global",
                    "scope": "screen",
                    "frame_source": "sampled-screen-fps",
                    "ordered_frames": False,
                    "approximate": False,
                },
            )


async def _run_thermal_state(
    dvt: Any,
    pid: int,
    interval_ms: int,
    stop_event: Any,
    emit: Callable[[str, dict[str, Any]], None],
    state: _SourceState,
    imports: dict[str, Any],
) -> None:
    interval_seconds = max(1.0, interval_ms / 1000.0)
    thermal_state_notice_reported = False
    async with imports["EnergyMonitor"](dvt, [pid]) as energy_monitor:
        async for telemetry in energy_monitor:
            if stop_event.is_set():
                return
            if state.target_confirmed:
                parsed = parse_ios_thermal_state(telemetry, pid)
                if parsed is not None:
                    level, name = parsed
                    emit(
                        "thermal_state",
                        {
                            "platform": "ios",
                            "pid": pid,
                            "value": level,
                            "state": name,
                            "source": "pymobiledevice3-xcode-energy-rsd",
                            "scope": "device",
                            "semantics": "process_info_thermal_state",
                        },
                    )
                elif not thermal_state_notice_reported:
                    thermal_state_notice_reported = True
                    reason = ios_thermal_state_unavailable_reason(telemetry, pid)
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
            await asyncio.sleep(interval_seconds)


async def _guard_source(
    label: str,
    source: Any,
    pid: int,
    emit: Callable[[str, dict[str, Any]], None],
    stop_event: Any | None = None,
    fatal_code: str = "",
    state: _SourceState | None = None,
) -> None:
    try:
        await source
    except asyncio.CancelledError:
        raise
    except Exception as exc:
        if fatal_code and stop_event is not None and not stop_event.is_set():
            if state is not None:
                state.set_target_confirmed(False)
            stop_event.set()
            emit(
                "fatal",
                {
                    "pid": pid,
                    "code": fatal_code,
                    "message": f"{label}失败，采集已停止：{exc}",
                },
            )
        elif stop_event is None or not stop_event.is_set():
            emit("status", {"pid": pid, "message": f"{label}不可用：{exc}"})
        return

    if fatal_code and stop_event is not None and not stop_event.is_set():
        if state is not None:
            state.set_target_confirmed(False)
        stop_event.set()
        emit(
            "fatal",
            {
                "pid": pid,
                "code": fatal_code,
                "message": f"{label}意外结束，采集已停止，请重新连接设备并重新选择进程。",
            },
        )


async def _run_metrics_async(
    udid: str,
    pid: int,
    interval_ms: int,
    collect_fps: bool,
    stop_event: Any,
    emit: Callable[[str, dict[str, Any]], None],
    target_name: str = "",
    target_bundle_id: str = "",
    target_start_abs_time: int = 0,
    target_coalition_id: int = 0,
    target_owner_pid: int = 0,
    target_owner_name: str = "",
    collect_cpu: bool = True,
    collect_memory: bool = True,
    collect_thermal_state: bool = False,
) -> None:
    emit("status", {"pid": pid, "message": "正在建立 iOS 17+ RSD 用户态隧道..."})
    imports = _pmd3_imports()
    state = _SourceState()
    tunnel = imports["UserspaceRsdTunnel"](serial=udid, autopair=True)
    async with tunnel as rsd, imports["DvtProvider"](rsd) as dvt:
        emit("status", {"pid": pid, "message": "已通过 iOS 17+ RSD 用户态隧道连接设备。"})
        sysmon_task = asyncio.create_task(
            _guard_source(
                "iOS PID CPU/内存源",
                _run_sysmontap(
                    dvt,
                    pid,
                    interval_ms,
                    stop_event,
                    emit,
                    imports,
                    expected_name=target_name,
                    expected_bundle_id=target_bundle_id,
                    expected_start_abs_time=target_start_abs_time,
                    expected_coalition_id=target_coalition_id,
                    expected_owner_pid=target_owner_pid,
                    expected_owner_name=target_owner_name,
                    state=state,
                    collect_cpu=collect_cpu,
                    collect_memory=collect_memory,
                    schema_udid=udid,
                ),
                pid,
                emit,
                stop_event=stop_event,
                fatal_code="process_metric_source_failed",
                state=state,
            )
        )
        core_task: asyncio.Task[Any] | None = None
        if collect_fps:
            core_task = asyncio.create_task(
                _run_core_profile_with_retries(
                    dvt,
                    pid,
                    interval_ms,
                    stop_event,
                    emit,
                    state,
                    imports,
                )
            )
            emit(
                "status",
                {"pid": pid, "message": "正在启动 CoreProfileSessionTap 有序显示帧采集，FPS/Jank 为屏幕级数据。"},
            )

        thermal_state_task: asyncio.Task[Any] | None = None
        if collect_thermal_state:
            thermal_state_task = asyncio.create_task(
                _guard_source(
                    "iOS Thermal State",
                    _run_thermal_state(dvt, pid, interval_ms, stop_event, emit, state, imports),
                    pid,
                    emit,
                )
            )

        graphics_task: asyncio.Task[Any] | None = None
        started_at = time.monotonic()
        next_graphics_retry_at = started_at + GRAPHICS_START_DELAY_SECONDS
        try:
            while not stop_event.is_set():
                now = time.monotonic()
                if collect_fps and state.target_confirmed:
                    if state.ordered_is_fresh(now):
                        if graphics_task is not None and not graphics_task.done():
                            graphics_task.cancel()
                            with contextlib.suppress(asyncio.CancelledError):
                                await graphics_task
                            graphics_task = None
                    elif _should_start_graphics_fallback(
                        collect_fps=collect_fps,
                        state=state,
                        now=now,
                        next_retry_at=next_graphics_retry_at,
                        graphics_task=graphics_task,
                    ):
                        if graphics_task is not None:
                            with contextlib.suppress(asyncio.CancelledError, Exception):
                                await graphics_task
                        emit(
                            "status",
                            {
                                "pid": pid,
                                "message": "启动阶段先使用真实屏幕 FPS；收到有序显示帧后自动切换，Display FrameTime/Jank 仍等待有序帧。",
                            },
                        )
                        graphics_task = asyncio.create_task(
                            _guard_source(
                                "iOS 屏幕 FPS 回退源",
                                _run_graphics_fallback(dvt, pid, stop_event, emit, state, imports),
                                pid,
                                emit,
                            )
                        )
                        next_graphics_retry_at = now + GRAPHICS_RETRY_SECONDS
                await asyncio.sleep(0.25)
        finally:
            tasks = [sysmon_task]
            if core_task is not None:
                tasks.append(core_task)
            if graphics_task is not None:
                tasks.append(graphics_task)
            if thermal_state_task is not None:
                tasks.append(thermal_state_task)
            for task in tasks:
                task.cancel()
            await asyncio.gather(*tasks, return_exceptions=True)


def run_metrics(
    udid: str,
    pid: int,
    interval_ms: int,
    collect_fps: bool,
    stop_event: Any,
    emit: Callable[[str, dict[str, Any]], None],
    target_name: str = "",
    target_bundle_id: str = "",
    target_start_abs_time: int = 0,
    target_coalition_id: int = 0,
    target_owner_pid: int = 0,
    target_owner_name: str = "",
    collect_cpu: bool = True,
    collect_memory: bool = True,
    collect_thermal_state: bool = False,
) -> None:
    asyncio.run(
        _run_metrics_async(
            udid,
            pid,
            interval_ms,
            collect_fps,
            stop_event,
            emit,
            target_name,
            target_bundle_id,
            target_start_abs_time,
            target_coalition_id,
            target_owner_pid,
            target_owner_name,
            collect_cpu,
            collect_memory,
            collect_thermal_state,
        )
    )
