"""HDC process telemetry. Unsupported metrics remain unavailable, never zero-filled."""
from __future__ import annotations

import argparse
import json
import re
import shlex
import subprocess
import sys
import time
from dataclasses import dataclass, replace

from display_metrics import (
    OrderedTimestampAccumulator,
    ordered_frame_payload,
)
from temperature_metrics import valid_temperature_c


@dataclass(frozen=True)
class ProcessSnapshot:
    pid: int
    name: str
    start_ticks: int
    process_ticks: int
    cores: tuple[tuple[int, int, int], ...]
    memory_mb: float | None
    memory_metric: str
    memory_source: str = ""
    name_source: str = "cmdline"
    user_id: int | None = None
    comm_name: str = ""


class HdcFailure(RuntimeError):
    """A bounded HDC failure with a user-actionable reason code."""

    def __init__(self, detail: str, code: str = "harmony_probe_failed", hint: str = "") -> None:
        self.detail = ((detail or "").strip() or "HDC 未提供错误详情")[:240]
        self.code = code or "harmony_probe_failed"
        self.hint = (hint or "").strip()
        super().__init__(self.detail)


def classify_hdc_failure(detail: str) -> tuple[str, str]:
    """Classify transport output without treating missing data as process exit."""
    text = (detail or "").lower()
    if any(token in text for token in ("permission denied", "access denied", "not authorized", "unauthorized")):
        return "harmony_hdc_permission_denied", "请解锁设备并确认已授权 HDC 调试；设备仍拒绝访问时，对应指标不可用。"
    if any(token in text for token in ("device not found", "device not founded", "device disconnected", "no devices", "offline")):
        return "harmony_device_disconnected", "请检查 USB 连接、设备授权和 HDC 状态；恢复后会重新建立采集基线。"
    if "command not found" in text or "unknown command" in text or "not recognized" in text:
        return "harmony_hdc_command_unsupported", "当前设备或 HDC 不支持该命令，对应指标保持缺测。"
    return "harmony_hdc_command_failed", "请检查设备连接和 HDC 输出；连续三次失败后采集会停止。"


class TargetChanged(RuntimeError):
    pass


@dataclass(frozen=True)
class RenderSurface:
    node_id: int
    name: str


FOREGROUND_WINDOW_COMMAND = "ohos-window list-windows --filter foreground --type meta"


def parse_render_surfaces(text: str, pid: int) -> list[RenderSurface]:
    surfaces: dict[int, RenderSurface] = {}
    for match in re.finditer(
        r"^\s*surface\s*\[(.*?)\]\s+NodeId\[(\d{1,20})\]\s+LayerId\[\d+\]:\s*$",
        text or "", re.MULTILINE,
    ):
        node_id = int(match[2])
        if 0 < node_id < 2**64 and node_id >> 32 == pid:
            surface = RenderSurface(node_id, match[1])
            if node_id in surfaces and surfaces[node_id] != surface:
                return []  # Conflicting identity evidence.
            surfaces[node_id] = surface
    return list(surfaces.values())


def parse_foreground_window_surface_ids(text: str, pid: int) -> set[int] | None:
    """Return exact foreground window Surface ids for *pid*.

    The window-manager command is optional and its output is deliberately
    strict: malformed, denied, or partial metadata must not turn a window
    name or focus hint into a frame-source identity.
    """
    try:
        payload = json.loads(text or "")
    except (TypeError, ValueError):
        return None
    if (not isinstance(payload, dict) or payload.get("type") != "result"
            or payload.get("status") != "success"):
        return None
    data = payload.get("data")
    if not isinstance(data, dict) or not isinstance(data.get("windows"), list):
        return None
    ids: set[int] = set()
    for item in data["windows"]:
        if not isinstance(item, dict):
            return None
        meta = item.get("metaInfo")
        if not isinstance(meta, dict):
            return None
        window_pid = meta.get("pid")
        node_id = meta.get("surfaceNodeId")
        if isinstance(window_pid, bool) or not isinstance(window_pid, int):
            return None
        if isinstance(node_id, bool) or not isinstance(node_id, int):
            return None
        if window_pid == pid and 0 < node_id < 2**64:
            ids.add(node_id)
    return ids


def parse_render_frame_records(text: str, name: str) -> list[tuple[int, int]] | None:
    """Validate the complete official 384-slot, newest-first RSSurfaceFps dump."""
    lines = (text or "").splitlines()
    headers = [i for i, line in enumerate(lines) if line.strip().startswith("surface [")]
    if len(headers) != 1 or lines[headers[0]].strip() != f"surface [{name}]:":
        return None
    body = [line.strip() for line in lines[headers[0] + 1:] if line.strip()]
    if len(body) != 384:
        return None
    records: list[tuple[int, int]] = []
    padded = False
    for line in body:
        match = re.fullmatch(r"(\d{1,20}):(\d{1,20})", line)
        if not match:
            return None
        flush, present = int(match[1]), int(match[2])
        if flush == present == 0:
            padded = True
            continue
        if padded or not 0 < flush <= present < 2**63 - 1:
            return None
        if records and (present > records[-1][1] or flush > records[-1][0]):
            return None
        records.append((flush, present))
    # This reversal is the documented wire order, not a sort that hides corruption.
    return records[::-1]


class HarmonyDisplayFrameSource:
    """Optional target-surface polling with bounded retries and continuity checks."""

    def __init__(self, hdc: "Hdc", pid: int) -> None:
        self.hdc = hdc
        self.pid = pid
        self.surface: RenderSurface | None = None
        self.last_record: tuple[int, int] | None = None
        self.accumulator = OrderedTimestampAccumulator(1.0, 1000.0)
        self.next_probe_at = 0.0
        self.source_origin_present_ns: int | None = None
        self.sequence = 0
        self.generation = 0
        self.status: tuple[str, str] | None = None
        self.last_status: tuple[str, str] | None = None

    def _status(self, code: str, message: str) -> None:
        value = (code, message)
        if value != self.last_status:
            self.status = value
            self.last_status = value

    def consume_status(self) -> tuple[str, str] | None:
        value, self.status = self.status, None
        return value

    def reset(self) -> None:
        self.surface = None
        self.last_record = None
        self.accumulator.reset_sequence()
        self.source_origin_present_ns = None
        self.generation += 1

    def _baseline(self, records: list[tuple[int, int]]) -> None:
        self.accumulator.reset_sequence()
        self.last_record = records[-1] if records else None
        # The historical endpoint proves overlap only. Its interval to the first
        # newly observed frame may include time before capture began.
        self.source_origin_present_ns = None

    def _select_surface(self, surfaces: list[RenderSurface]) -> tuple[RenderSurface | None, str]:
        """Resolve one owned Surface, using foreground metadata only when needed."""
        if not surfaces:
            return None, "surface_missing"
        if len(surfaces) == 1:
            return surfaces[0], "ok"
        try:
            metadata = self.hdc.shell(FOREGROUND_WINDOW_COMMAND, timeout=1.0)
        except HdcFailure:
            return None, "unavailable"
        window_ids = parse_foreground_window_surface_ids(metadata, self.pid)
        if window_ids is None:
            return None, "unavailable"
        # All foreground windows reported for this PID must be represented by
        # the current RenderService inventory. One matching node plus an
        # unlisted node is still conflicting evidence, not a reason to guess.
        if len(window_ids) != 1:
            return None, "ambiguous"
        matches = [surface for surface in surfaces if surface.node_id in window_ids]
        if len(matches) != 1:
            return None, "ambiguous"
        return matches[0], "ok"

    def _reset_for_window_change(self, now: float) -> list[dict[str, object]]:
        self.reset()
        # A window switch is a valid state transition, not a transport error.
        # Re-establish a baseline on the next poll without carrying old frames.
        self.next_probe_at = now
        self._status("harmony_frame_window_changed",
                     "鸿蒙：前台显示窗口已切换，帧指标重新建立基线。")
        return []

    def poll(self, now: float) -> list[dict[str, object]]:
        if now < self.next_probe_at:
            return []
        try:
            return self._poll(now)
        except HdcFailure:
            self.reset()
            self.next_probe_at = now + 5.0
            self._status("harmony_frame_probe_failed",
                         "鸿蒙：RenderService 帧记录读取失败，将自动重试；其他指标继续采集。")
            return []

    def _poll(self, now: float) -> list[dict[str, object]]:
        # Refresh ownership each poll: a previously unique surface may disappear
        # or become ambiguous even while its historical fps dump remains readable.
        try:
            surfaces = parse_render_surfaces(
                self.hdc.shell("hidumper -s RenderService -a surface", timeout=2.0), self.pid)
        except HdcFailure:
            self.reset()
            self.next_probe_at = now + 5.0
            self._status("harmony_frame_probe_failed",
                         "鸿蒙：RenderService 帧记录读取失败，将自动重试；其他指标继续采集。")
            return []
        surface, selection = self._select_surface(surfaces)
        if surface is None:
            self.reset()
            self.next_probe_at = now + 5.0
            if selection == "surface_missing":
                self._status("harmony_frame_surface_unavailable",
                             "鸿蒙：未找到所选 PID 的显示 Surface，帧指标保持缺测并自动重试。")
            elif selection == "unavailable":
                self._status("harmony_frame_window_unavailable",
                             "鸿蒙：无法读取前台窗口归属，多个显示 Surface 暂不计入帧指标。")
            else:
                self._status("harmony_frame_surface_ambiguous",
                             "鸿蒙：所选 PID 的前台显示窗口不唯一，帧指标保持缺测。")
            return []
        if self.surface != surface:
            if self.surface is not None:
                return self._reset_for_window_change(now)
            self.reset()
            self.surface = surface
        text = self.hdc.shell(
            "hidumper -s RenderService -a " + shlex.quote(f"fps -id {surface.node_id}"),
            timeout=2.0)
        records = parse_render_frame_records(text, surface.name)
        if records is None:
            self.reset()
            self.next_probe_at = now + 5.0
            self._status("harmony_frame_dump_invalid",
                         "鸿蒙：帧记录格式不受支持、不完整或乱序，已丢弃并等待有效帧源。")
            return []
        # Re-check the ownership after reading the historical ring. A window
        # can change during the dump, including a transition from one Surface
        # to multiple Surfaces; never attribute that batch to the old window.
        try:
            current_surfaces = parse_render_surfaces(
                self.hdc.shell("hidumper -s RenderService -a surface", timeout=2.0), self.pid)
        except HdcFailure:
            self.reset()
            self.next_probe_at = now + 5.0
            self._status("harmony_frame_probe_failed",
                         "鸿蒙：RenderService 窗口复核失败，将自动重试；其他指标继续采集。")
            return []
        current_surface, current_selection = self._select_surface(current_surfaces)
        if current_surface is None:
            if current_selection == "unavailable":
                self.reset()
                self.next_probe_at = now + 5.0
                self._status("harmony_frame_window_unavailable",
                             "鸿蒙：无法复核前台窗口归属，帧指标保持缺测并自动重试。")
                return []
            return self._reset_for_window_change(now)
        if current_surface != surface or current_selection != "ok":
            return self._reset_for_window_change(now)
        if not records or self.last_record is None:
            if self.last_record is not None:
                self.generation += 1
            self._baseline(records)
            self._status("harmony_frame_source_waiting",
                         "鸿蒙：已建立帧源基线，等待新增的显示帧。")
            return []
        if self.last_record not in records:
            self.generation += 1
            self._baseline(records)
            self._status("harmony_frame_source_gap",
                         "鸿蒙：帧记录已覆盖或重置，无法确认连续性，已重新建立基线。")
            return []
        # Match the exact flush/present pair, so repeated reads never add frames.
        overlap = len(records) - 1 - records[::-1].index(self.last_record)
        fresh = records[overlap + 1:]
        if not fresh:
            self._status("harmony_frame_source_waiting",
                         "鸿蒙：尚无新增显示帧；历史记录不作为零帧或卡顿的证据。")
            return []
        payloads: list[dict[str, object]] = []
        for _, present in fresh:
            if self.source_origin_present_ns is None:
                self.source_origin_present_ns = present
            window = self.accumulator.add_timestamp(present)
            if self.accumulator.last_event_breaks_sequence:
                self.generation += 1
                self.source_origin_present_ns = present
            if window is None:
                continue
            self.sequence += 1
            payloads.append({
                **ordered_frame_payload(window),
                "source": "hdc-renderservice-surface-fps",
                "scope": "surface",
                "frame_source": f"harmony-present/node-{surface.node_id}/generation-{self.generation}",
                "target_verified": True,
                "surface_owner_pid": self.pid,
                # RenderService timestamps are device-monotonic nanoseconds.
                # Keep this timeline entirely in the source clock; the C#
                # collector anchors it to host receipt time at the boundary.
                "source_elapsed_sec": (present - self.source_origin_present_ns) / 1e9,
                "source_lag_sec": (fresh[-1][1] - present) / 1e9,
                "source_sequence": self.sequence,
                "surface_node_id": surface.node_id,
            })
        self.last_record = records[-1]
        if payloads:
            self._status("harmony_frame_source_ready",
                         "鸿蒙：已接入所选 PID 的 RenderService 有序显示帧源。")
        return payloads

class Hdc:
    def __init__(self, executable: str, serial: str) -> None:
        self.executable = executable
        self.serial = serial

    def shell(self, command: str, timeout: float = 5.0) -> str:
        try:
            result = subprocess.run(
                [self.executable, "-t", self.serial, "shell", command],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, encoding="utf-8", errors="replace", timeout=timeout,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except subprocess.TimeoutExpired as exc:
            raise HdcFailure(
                f"HDC 命令超时（{timeout:g}s）：{command[:120]}",
                "harmony_hdc_timeout",
                "请检查设备响应和 USB 状态；本次未产生有效指标。",
            ) from exc
        except OSError as exc:
            raise HdcFailure(
                f"无法启动 HDC：{exc}",
                "harmony_hdc_launch_failed",
                "请检查 HDC 路径或 DevEco/鸿蒙工具链安装。",
            ) from exc
        output = (result.stderr + " " + result.stdout).strip()
        # HDC versions differ on which stream receives transport failures.
        # Treat the marker in either stream as a failed command so a device
        # error cannot be parsed as a valid process snapshot.
        if result.returncode != 0 or "[Fail]" in output:
            code, hint = classify_hdc_failure(output)
            raise HdcFailure(output[:240], code, hint)
        return result.stdout


def emit(kind: str, payload: dict[str, object]) -> None:
    print(kind + " " + json.dumps(payload, ensure_ascii=False, allow_nan=False), flush=True)


def sections(text: str) -> dict[str, str]:
    result: dict[str, list[str]] = {}
    key = ""
    for line in text.splitlines():
        if re.fullmatch(r"__MOTUPERF_[A-Z]+__", line.strip()):
            key = line.strip()
            result[key] = []
        elif key:
            result[key].append(line)
    return {key: "\n".join(lines) for key, lines in result.items()}


def proc_stat(text: str, pid: int) -> tuple[int, int] | None:
    match = re.fullmatch(r"\s*(\d+) \(.*\) (.+)\s*", text.strip())
    if match is None or int(match[1]) != pid:
        return None
    fields = match[2].split()
    try:
        start = int(fields[19])
        ticks = int(fields[11]) + int(fields[12])
        return (start, ticks) if start > 0 and ticks >= 0 else None
    except (ValueError, IndexError):
        return None


def proc_stat_name(text: str, pid: int) -> str:
    match = re.fullmatch(r"\s*(\d+) \((.*)\) (.+)\s*", text.strip())
    if match is None or int(match[1]) != pid:
        return ""
    return match[2].strip()


def proc_harmony_user_id(text: str) -> int | None:
    """Map the Linux process UID to its Harmony profile when exposed."""
    match = re.search(r"^Uid:\s+(\d+)\b", text or "", re.MULTILINE)
    if match is None:
        return None
    try:
        uid = int(match[1])
    except ValueError:
        return None
    # OpenHarmony Bundle Manager uses BASE_USER_RANGE=200000. This is not
    # Android's UID namespace. Linux system accounts do not identify a profile.
    if uid < 10000:
        return None
    return uid // 200000


def parse_snapshot(text: str, pid: int) -> ProcessSnapshot | None:
    data = sections(text)
    stat_text = data.get("__MOTUPERF_STAT__", "")
    identity = proc_stat(stat_text, pid)
    comm_name = proc_stat_name(stat_text, pid)
    name = data.get("__MOTUPERF_NAME__", "").split("\x00", 1)[0].strip()
    name_source = "cmdline" if name else ""
    if not name:
        # Some system or restricted processes expose an empty cmdline while
        # /proc/<pid>/stat remains readable. The comm field is still the
        # device-reported process identity and is safe for PID reuse checks.
        name = comm_name
        name_source = "comm" if name else "unknown"
    # Bundle Manager/Ability Manager can provide an authoritative Bundle+PID
    # even when both /proc/<pid>/cmdline and the stat COMM field are hidden.
    # The PID start clock remains the required identity proof; an empty label
    # must not make an otherwise verified target uncollectable.
    if identity is None:
        return None
    cores: list[tuple[int, int, int]] = []
    for line in data.get("__MOTUPERF_CPU__", "").splitlines():
        match = re.match(r"^cpu(\d+)\s+(.+)$", line)
        if not match:
            continue
        try:
            values = [int(v) for v in match[2].split()[:8]]
            if len(values) < 4 or any(v < 0 for v in values):
                cores = []
                break
            # guest counters are already part of user/nice.
            total = sum(values)
            idle = values[3] + (values[4] if len(values) > 4 else 0)
            cores.append((int(match[1]), total, total - idle))
        except ValueError:
            cores = []
            break
    memory = None
    metric = ""
    for section, field, label in (("__MOTUPERF_PSS__", "Pss", "pss"), ("__MOTUPERF_STATUS__", "VmRSS", "rss")):
        match = re.search(r"^" + field + r":\s*(\d+)\s+kB\s*$", data.get(section, ""), re.MULTILINE)
        if match and int(match[1]) > 0:
            memory, metric = int(match[1]) / 1024.0, label
            break
    source = "hdc-proc-smaps-rollup" if metric == "pss" else "hdc-proc-status" if metric else ""
    return ProcessSnapshot(pid, name, identity[0], identity[1], tuple(sorted(cores)), memory, metric, source, name_source,
                           proc_harmony_user_id(data.get("__MOTUPERF_STATUS__", "")), comm_name)


def normalized_process_name(name: str, source: str) -> str:
    # Only cmdline paths have a basename; kworker/0:1 is a complete COMM.
    return name.rsplit("/", 1)[-1] if source == "cmdline" and name.startswith("/") else name


def comm_alias_matches(comm: str, full_name: str) -> bool:
    return len(comm.encode("utf-8")) == 15 and len(full_name) > len(comm) and full_name.startswith(comm)


def target_name_matches(snapshot: ProcessSnapshot, expected: str, *,
                        expected_is_comm: bool = False, bound_start: int = 0) -> bool:
    """Match a selected process without confusing a truncated comm for another PID."""
    expected = (expected or "").strip()
    expected = normalized_process_name(expected, "comm" if expected_is_comm else "cmdline")
    actual = normalized_process_name(snapshot.name, snapshot.name_source)
    if not expected:
        # An unnamed target is still bound by its real PID/start clock. Do not
        # let an empty label bypass PID reuse protection on the next poll.
        return bound_start <= 0 or snapshot.start_ticks == bound_start
    if actual == expected:
        return True
    if expected_is_comm:
        # Complete only a previously verified process instance, then bind its
        # full name in collect so sibling executables cannot share this alias.
        return (snapshot.name_source == "cmdline" and bound_start > 0
                and snapshot.start_ticks == bound_start and snapshot.comm_name == expected
                and comm_alias_matches(expected, actual))
    return (snapshot.name_source == "comm" and bound_start > 0 and snapshot.start_ticks == bound_start
            and comm_alias_matches(actual, expected))


def parse_hidumper_cpu(text: str, pid: int) -> float | None:
    if not re.search(r"PID\s+Total Usage\s+User Space", text or ""):
        return None
    match = re.search(r"^\s*" + str(pid) + r"\s+([0-9]+(?:\.[0-9]+)?)%\s+", text or "", re.MULTILINE)
    if match is None:
        return None
    value = float(match.group(1))
    return value if 0 <= value <= 10000 else None


def parse_hidumper_pss_mb(text: str) -> float | None:
    # `hidumper --mem <pid>` has a final row beginning with Total. Use the
    # first numeric column after it, which is the process PSS total in kB.
    if not re.search(r"^\s*Pss\s+Shared\s+Shared\s+Private", text or "", re.MULTILINE) or not re.search(r"\(\s*kB\s*\)", text):
        return None
    match = re.search(r"^\s*Total\s+([0-9]+)\s+", text or "", re.MULTILINE | re.IGNORECASE)
    if match is None:
        return None
    value = int(match.group(1)) / 1024.0
    return value if value > 0 else None


def cpu_payload(previous: ProcessSnapshot, current: ProcessSnapshot) -> dict[str, object] | None:
    if previous.pid != current.pid or previous.start_ticks != current.start_ticks:
        return None
    if not target_name_matches(current, normalized_process_name(previous.name, previous.name_source),
                               expected_is_comm=previous.name_source == "comm", bound_start=previous.start_ticks):
        return None
    if previous.user_id is not None and current.user_id is not None and previous.user_id != current.user_id:
        return None
    if not previous.cores or tuple(c[0] for c in previous.cores) != tuple(c[0] for c in current.cores):
        return None
    deltas = [(b[1] - a[1], b[2] - a[2]) for a, b in zip(previous.cores, current.cores)]
    # Offline/reconfigured cores require a new baseline instead of a guessed denominator.
    if any(total <= 0 or busy < 0 or busy > total for total, busy in deltas):
        return None
    process = current.process_ticks - previous.process_ticks
    if process < 0:
        return None
    total = sum(d[0] for d in deltas)
    if process > total:
        return None
    normalized = process * 100.0 / total
    return {
        "value": normalized * len(deltas), "normalized_value": normalized,
        "source": "hdc-proc-stat", "normalized_source": f"hdc-proc-stat/{len(deltas)}-cores",
        "scope": "process", "pid": current.pid, "platform": "harmony",
        # Existing chart contract labels contiguous CPU0..CPU<N> only.
        **({"core_values": [busy * 100.0 / total for total, busy in deltas],
            "core_source": "hdc-proc-stat", "core_scope": "device"}
           if [c[0] for c in current.cores] == list(range(len(deltas))) else {}),
    }


def snapshot_command(pid: int, memory: bool) -> str:
    paths = [("STAT", f"/proc/{pid}/stat"), ("NAME", f"/proc/{pid}/cmdline"), ("CPU", "/proc/stat"),
             ("STATUS", f"/proc/{pid}/status")]
    if memory:
        paths.insert(3, ("PSS", f"/proc/{pid}/smaps_rollup"))
    commands = ["printf '__MOTUPERF_" + marker + "__\\n'; cat " + shlex.quote(path) + " 2>/dev/null; printf '\\n'" for marker, path in paths]
    return "; ".join(commands)


def parse_temperatures(text: str) -> dict[str, float]:
    result: dict[str, float] = {}
    for line in text.splitlines():
        parts = line.split("|")
        if len(parts) != 3 or not re.fullmatch(r"thermal_zone\d+", parts[0]):
            continue
        try:
            value = valid_temperature_c(int(parts[2]) / 1000.0)
        except (ValueError, OverflowError):
            continue
        if value is not None and parts[1].strip():
            result[f"{parts[1].strip()} ({parts[0]})"] = value
    return result


def parse_harmony_thermal_temperatures(text: str) -> dict[str, float]:
    """Parse Thermal Manager's read-only ``hidumper -t`` output.

    Thermal Manager exposes sensor values as integer milli-degrees Celsius
    through ``Type:`` / ``Temperature:`` pairs.  Keep the type attached to
    the value and reject incomplete or non-numeric pairs rather than
    associating one sensor with another sensor's value.
    """
    if re.search(r"(?im)^\s*(?:\[(?:fail(?:ed)?|error)\]|permission denied\b)", text or ""):
        return {}
    result: dict[str, float] = {}
    rejected: set[str] = set()
    sensor = ""
    for raw in (text or "").splitlines():
        if not raw.strip():
            continue
        type_match = re.fullmatch(r"\s*Type\s*:\s*(?P<type>[^\x00-\x1f\x7f]{1,128}?)\s*", raw)
        if type_match:
            if sensor:
                rejected.add(sensor)
            sensor = type_match.group("type").strip()
            continue
        if not sensor:
            continue
        value_match = re.fullmatch(r"\s*Temperature\s*:\s*(?P<value>[+-]?[0-9]{1,10})\s*", raw)
        value = valid_temperature_c(int(value_match.group("value")) / 1000.0) if value_match else None
        if value is None or (sensor in result and result[sensor] != value):
            rejected.add(sensor)
        else:
            result[sensor] = value
        sensor = ""
    if sensor:
        rejected.add(sensor)
    return {name: value for name, value in result.items() if name not in rejected}


def read_device_temperatures(hdc: Hdc) -> tuple[dict[str, float], str]:
    """Use independent optional sources within a two-second command budget."""
    try:
        raw = hdc.shell("for z in /sys/class/thermal/thermal_zone*; do "
                        "printf '%s|' \"${z##*/}\"; tr -d '\\n' < \"$z/type\"; "
                        "printf '|'; cat \"$z/temp\"; printf '\\n'; done", timeout=1.0)
        values = parse_temperatures(raw)
        if values:
            return values, "hdc-sysfs-thermal-millidegrees"
    except HdcFailure:
        pass
    try:
        values = parse_harmony_thermal_temperatures(hdc.shell("hidumper -s 3303 -a -t", timeout=1.0))
        if values:
            # The manager may rename/aggregate sensors; never splice its
            # readings into a sysfs zone series based on a similar name.
            return ({f"{name} (Thermal Manager)": value for name, value in values.items()},
                    "hdc-hidumper-3303-temperature")
    except HdcFailure:
        pass
    return {}, ""


def provenance(args: argparse.Namespace, pid: int | None = None) -> dict[str, object]:
    return {
        "platform": "harmony",
        "device_serial": args.serial,
        "bundle_id": args.target_bundle_id,
        "target_name": args.target_name,
        "target_name_is_comm": args.target_name_is_comm,
        "user_id": args.target_user_id,
        # Keep hand-built test/embedding namespaces backward compatible while
        # carrying the real clone/app-index identity when the CLI provides it.
        "app_index": getattr(args, "target_app_index", -1),
        "target_start_time_ticks": args.target_start_time_ticks,
        "pid": args.pid if pid is None else pid,
    }


def collect(args: argparse.Namespace, hdc: Hdc) -> int:
    previous: ProcessSnapshot | None = None
    frames = HarmonyDisplayFrameSource(hdc, args.pid) if not args.no_fps else None
    bound_start = args.target_start_time_ticks
    bound_name = args.target_name
    bound_name_is_comm = args.target_name_is_comm
    failures = 0
    notices: set[str] = set()
    next_temperature = 0.0

    def notice(code: str, message: str) -> None:
        if code not in notices:
            notices.add(code)
            emit("status", {**provenance(args), "code": code, "message": message})

    if frames is not None:
        notice("harmony_frame_source_waiting", "鸿蒙：正在查找所选 PID 的 RenderService 有序显示帧源。")
    if not args.no_thermal_state:
        notice("harmony_thermal_state_unavailable", "鸿蒙：尚无已验证的系统热状态来源，Thermal State 保持缺测。")
    while True:
        started = time.monotonic()
        try:
            text = hdc.shell(snapshot_command(args.pid, not args.no_memory))
            current = parse_snapshot(text, args.pid)
            if current is None:
                # A successful shell with unreadable /proc is not proof of process exit.
                raise HdcFailure(
                    "无法读取目标进程身份或 /proc 权限受限",
                    "harmony_process_identity_unavailable",
                    "这不等同于进程已退出；请解锁设备并检查 HDC 调试授权。",
                )
            if not target_name_matches(current, bound_name, expected_is_comm=bound_name_is_comm,
                                       bound_start=bound_start):
                raise TargetChanged("所选 PID 已属于其他进程，请重新选择。")
            if bound_start and current.start_ticks != bound_start:
                raise TargetChanged("目标进程已退出或重启，请重新选择当前进程。")
            if args.target_user_id >= 0:
                if current.user_id is not None and current.user_id != args.target_user_id:
                    raise TargetChanged("目标进程已切换到其他鸿蒙用户资料，请重新选择当前进程。")
                if current.user_id is None:
                    notice("harmony_user_identity_unavailable", "鸿蒙：当前进程的用户资料不可读，已保留用户归属缺测并继续校验 PID/启动时钟。")
            cpu = None
            if not args.no_cpu and previous is not None:
                cpu = cpu_payload(previous, current)
            # Prefer counter deltas with a known denominator. HiDumper can still
            # supply raw process CPU when /proc/stat is restricted.
            if not args.no_cpu and not current.cores:
                try:
                    raw_cpu = parse_hidumper_cpu(hdc.shell(f"hidumper --cpuusage {args.pid}", timeout=3.0), args.pid)
                except HdcFailure:
                    raw_cpu = None
                if raw_cpu is not None:
                    cpu = {"value": raw_cpu, "source": "hdc-hidumper-cpuusage", "scope": "process",
                           "pid": current.pid, "platform": "harmony"}
            if not args.no_memory and current.memory_metric != "pss":
                try:
                    pss = parse_hidumper_pss_mb(hdc.shell(f"hidumper --mem {args.pid}", timeout=3.0))
                except HdcFailure:
                    pss = None
                if pss is not None:
                    current = replace(current, memory_mb=pss, memory_metric="pss", memory_source="hdc-hidumper-mem")
            frame_payloads = frames.poll(time.monotonic()) if frames is not None else []
            # Recheck after every process/frame command; never commit a sample
            # that may belong to a replacement process using the same PID.
            after = proc_stat(hdc.shell(f"cat /proc/{args.pid}/stat", timeout=3.0), args.pid)
            if after is None:
                raise HdcFailure(
                    "采样结束时无法复核目标进程身份",
                    "harmony_process_identity_unavailable",
                    "这不等同于进程已退出；本次样本已丢弃并等待下一次有效校验。",
                )
            if after[0] != current.start_ticks:
                raise TargetChanged("采样期间 PID 被复用，本次数据已丢弃。")
            if not bound_name or current.name_source == "cmdline":
                bound_name = normalized_process_name(current.name, current.name_source)
                bound_name_is_comm = current.name_source == "comm"
            if not bound_start or previous is None:
                bound_start = current.start_ticks
                emit("target", {**provenance(args, args.pid), "confirmed": True})
            recovered_after = failures
            failures = 0
            if recovered_after:
                emit("status", {**provenance(args), "code": "harmony_probe_recovered",
                                 "recovered_after": recovered_after,
                                 "message": "鸿蒙目标进程校验已恢复，继续采集；CPU 与帧指标重新建立基线，缺测区间不补值。"})
            if frames is not None:
                frame_status = frames.consume_status()
                if frame_status is not None:
                    emit("status", {**provenance(args), "code": frame_status[0], "message": frame_status[1]})
                for payload in frame_payloads:
                    emit("fps", {**provenance(args, current.pid), **payload})
            if not args.no_cpu:
                if cpu is not None:
                    emit("cpu", {**provenance(args, current.pid), **cpu})
                else:
                    notice("harmony_cpu_unavailable", "鸿蒙：CPU 计数器不可读或已变化，等待有效计数区间。")
            previous = current
            if not args.no_memory:
                if current.memory_mb is not None:
                    emit("memory", {**provenance(args, args.pid), "value": current.memory_mb,
                                    "metric": current.memory_metric, "unit": "MB", "scope": "process",
                                    "source": current.memory_source,
                                    "fallback": current.memory_metric == "rss"})
                else:
                    notice("harmony_memory_unavailable", "鸿蒙：所选 PID 内存数据不可读，内存保持缺测。")
        except TargetChanged as exc:
            emit("target", {**provenance(args), "confirmed": False})
            emit("fatal", {**provenance(args), "code": "harmony_target_changed", "message": str(exc)})
            return 2
        except HdcFailure as exc:
            previous = None
            if frames is not None:
                frames.reset()
            failures += 1
            emit("target", {**provenance(args), "confirmed": False})
            failure_message = "鸿蒙采集失败（第 {}/3 次）：{}。{}".format(
                failures, str(exc), exc.hint or "请检查设备连接、授权和目标进程状态。")
            emit("status", {**provenance(args), "code": exc.code, "attempt": failures,
                             "max_attempts": 3, "message": failure_message})
            if failures >= 3:
                emit("fatal", {**provenance(args), "code": "harmony_target_unavailable",
                                "reason_code": exc.code, "attempts": failures,
                                "message": "连续三次无法校验鸿蒙设备/目标进程：" + str(exc) + "。" + (exc.hint or "")})
                return 2
        if failures == 0 and not args.no_temperature and time.monotonic() >= next_temperature:
            values, source = read_device_temperatures(hdc)
            next_temperature = time.monotonic() + 5.0
            if values:
                emit("temperature", {**provenance(args), "values": values, "unit": "C", "source": source, "scope": "device"})
            else:
                notice("harmony_temperature_unavailable", "鸿蒙：温度传感器与 Thermal Manager 均未返回可用数据，请检查 HDC 授权；设备未开放的温度保持缺测。")
        time.sleep(max(0.05, args.interval - (time.monotonic() - started)))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--hdc", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--target-name", default="")
    parser.add_argument("--target-name-is-comm", action="store_true")
    parser.add_argument("--target-bundle-id", default="")
    parser.add_argument("--target-user-id", type=int, default=-1)
    parser.add_argument("--target-app-index", type=int, default=-1)
    parser.add_argument("--target-start-time-ticks", type=int, default=0)
    parser.add_argument("--interval", type=float, default=1.0)
    for metric in ("fps", "cpu", "memory", "temperature", "thermal-state"):
        parser.add_argument("--no-" + metric, action="store_true")
    args = parser.parse_args()
    if args.pid <= 0 or not 0.1 <= args.interval <= 60:
        parser.error("pid/interval out of range")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    try:
        return collect(args, Hdc(args.hdc, args.serial))
    except KeyboardInterrupt:
        return 0
    except Exception as exc:
        emit("fatal", {"code": "harmony_runner_failed", "message": "鸿蒙采集异常：" + str(exc)})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
