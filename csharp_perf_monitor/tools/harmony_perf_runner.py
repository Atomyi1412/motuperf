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


class HdcFailure(RuntimeError):
    pass


class TargetChanged(RuntimeError):
    pass


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
        except (OSError, subprocess.TimeoutExpired) as exc:
            raise HdcFailure(str(exc)) from exc
        output = (result.stderr + " " + result.stdout).strip()
        # HDC versions differ on which stream receives transport failures.
        # Treat the marker in either stream as a failed command so a device
        # error cannot be parsed as a valid process snapshot.
        if result.returncode != 0 or "[Fail]" in output:
            raise HdcFailure(output[:240])
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


def parse_snapshot(text: str, pid: int) -> ProcessSnapshot | None:
    data = sections(text)
    stat_text = data.get("__MOTUPERF_STAT__", "")
    identity = proc_stat(stat_text, pid)
    name = data.get("__MOTUPERF_NAME__", "").split("\x00", 1)[0].strip()
    if not name:
        # Some system or restricted processes expose an empty cmdline while
        # /proc/<pid>/stat remains readable. The comm field is still the
        # device-reported process identity and is safe for PID reuse checks.
        name = proc_stat_name(stat_text, pid)
    if identity is None or not name:
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
    return ProcessSnapshot(pid, name, identity[0], identity[1], tuple(sorted(cores)), memory, metric, source)


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
    if previous.pid != current.pid or previous.start_ticks != current.start_ticks or previous.name != current.name:
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
    paths = [("STAT", f"/proc/{pid}/stat"), ("NAME", f"/proc/{pid}/cmdline"), ("CPU", "/proc/stat")]
    if memory:
        paths += [("PSS", f"/proc/{pid}/smaps_rollup"), ("STATUS", f"/proc/{pid}/status")]
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
        except ValueError:
            continue
        if value is not None and parts[1].strip():
            result[f"{parts[1].strip()} ({parts[0]})"] = value
    return result


def collect(args: argparse.Namespace, hdc: Hdc) -> int:
    previous: ProcessSnapshot | None = None
    bound_start = args.target_start_time_ticks
    failures = 0
    notices: set[str] = set()
    next_temperature = 0.0

    def notice(code: str, message: str) -> None:
        if code not in notices:
            notices.add(code)
            emit("status", {"code": code, "message": message, "platform": "harmony"})

    if not args.no_fps:
        notice("harmony_frame_source_unavailable", "鸿蒙：尚未验证目标应用的显示帧源，FPS、FrameTime、Jank/BigJank 暂不可用。")
    if not args.no_thermal_state:
        notice("harmony_thermal_state_unavailable", "鸿蒙：尚无已验证的系统热状态来源，Thermal State 保持缺测。")
    while True:
        started = time.monotonic()
        try:
            text = hdc.shell(snapshot_command(args.pid, not args.no_memory))
            current = parse_snapshot(text, args.pid)
            if current is None:
                # A successful shell with unreadable /proc is not proof of process exit.
                raise HdcFailure("无法读取目标进程身份或 /proc 权限受限，请解锁并检查 HDC 调试授权")
            if args.target_name and current.name != args.target_name:
                raise TargetChanged("所选 PID 已属于其他进程，请重新选择。")
            if bound_start and current.start_ticks != bound_start:
                raise TargetChanged("目标进程已退出或重启，请重新选择当前进程。")
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
            # Recheck after every process-metric command; never commit a sample
            # that may belong to a replacement process using the same PID.
            after = proc_stat(hdc.shell(f"cat /proc/{args.pid}/stat", timeout=3.0), args.pid)
            if after is None:
                raise HdcFailure("采样结束时无法复核目标进程身份")
            if after[0] != current.start_ticks:
                raise TargetChanged("采样期间 PID 被复用，本次数据已丢弃。")
            if not bound_start or previous is None:
                bound_start = current.start_ticks
                emit("target", {"pid": args.pid, "confirmed": True, "platform": "harmony"})
            failures = 0
            if not args.no_cpu:
                if cpu is not None:
                    emit("cpu", cpu)
                else:
                    notice("harmony_cpu_unavailable", "鸿蒙：CPU 计数器不可读或已变化，等待有效计数区间。")
            previous = current
            if not args.no_memory:
                if current.memory_mb is not None:
                    emit("memory", {"value": current.memory_mb, "metric": current.memory_metric, "unit": "MB",
                                    "pid": args.pid, "platform": "harmony", "scope": "process",
                                    "source": current.memory_source,
                                    "fallback": current.memory_metric == "rss"})
                else:
                    notice("harmony_memory_unavailable", "鸿蒙：所选 PID 内存数据不可读，内存保持缺测。")
        except TargetChanged as exc:
            emit("target", {"pid": args.pid, "confirmed": False})
            emit("fatal", {"code": "harmony_target_changed", "message": str(exc)})
            return 2
        except HdcFailure as exc:
            previous = None
            failures += 1
            emit("target", {"pid": args.pid, "confirmed": False})
            emit("status", {"code": "harmony_probe_failed", "message": "鸿蒙采集连接或进程校验失败：" + str(exc)})
            if failures >= 3:
                emit("fatal", {"code": "harmony_target_unavailable", "message": "连续三次无法校验鸿蒙设备/目标进程：" + str(exc)})
                return 2
        if failures == 0 and not args.no_temperature and time.monotonic() >= next_temperature:
            next_temperature = time.monotonic() + 5.0
            try:
                raw = hdc.shell("for z in /sys/class/thermal/thermal_zone*; do "
                                "printf '%s|' \"${z##*/}\"; tr -d '\\n' < \"$z/type\"; "
                                "printf '|'; cat \"$z/temp\"; printf '\\n'; done", timeout=3.0)
                values = parse_temperatures(raw)
                if values:
                    emit("temperature", {"values": values, "source": "hdc-sysfs-thermal-millidegrees", "scope": "device", "platform": "harmony"})
                else:
                    notice("harmony_temperature_unavailable", "鸿蒙：未读取到可用温度传感器，温度保持缺测。")
            except HdcFailure as exc:
                notice("harmony_temperature_unavailable", "鸿蒙温度读取失败：" + str(exc))
        time.sleep(max(0.05, args.interval - (time.monotonic() - started)))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--hdc", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--target-name", default="")
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
