#!/usr/bin/env python3
from __future__ import annotations

import argparse
import asyncio
import json
import sys
from collections import defaultdict
from typing import Any

from ios_sysmon_schema_cache import save_sysmon_schema
from pmd3_windows_compat import apply_windows_pytcp_cleanup_compat


_USER_APPLICATION_CONTAINER = "/containers/bundle/application/"


def _integer(value: Any) -> int:
    if value is None or isinstance(value, bool):
        return 0
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def _application_owner(
    process: dict[str, Any],
    sysmon_by_pid: dict[int, dict[str, Any]],
    applications_by_pid: dict[int, dict[str, Any]],
    applications_by_coalition: dict[int, list[int]],
) -> tuple[dict[str, Any] | None, str, bool]:
    pid = _integer(process.get("pid"))
    sysmon = sysmon_by_pid.get(pid, {})
    coalition_id = _integer(sysmon.get("coalitionID"))
    responsible_pid = _integer(sysmon.get("responsiblePID"))

    direct = applications_by_pid.get(responsible_pid)
    if direct is not None and responsible_pid != pid:
        owner_sysmon = sysmon_by_pid.get(responsible_pid, {})
        owner_coalition = _integer(owner_sysmon.get("coalitionID"))
        if coalition_id <= 0 or owner_coalition <= 0 or owner_coalition == coalition_id:
            return direct, "responsible-pid", False
        return None, "responsible-pid-coalition-mismatch", True

    candidates = [
        candidate_pid
        for candidate_pid in applications_by_coalition.get(coalition_id, [])
        if candidate_pid != pid
    ]
    if len(candidates) == 1:
        return applications_by_pid[candidates[0]], "coalition", False
    if len(candidates) > 1:
        return None, "ambiguous-coalition", True
    return None, "unavailable", False


def enrich_processes(
    processes: list[dict[str, Any]],
    sysmon_rows: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    sysmon_by_pid = {
        _integer(row.get("pid")): row
        for row in sysmon_rows
        if _integer(row.get("pid")) > 0
    }
    applications_by_pid = {
        _integer(process.get("pid")): process
        for process in processes
        if bool(process.get("isApplication")) and _integer(process.get("pid")) > 0
    }
    applications_by_coalition: dict[int, list[int]] = defaultdict(list)
    for pid in applications_by_pid:
        coalition_id = _integer(sysmon_by_pid.get(pid, {}).get("coalitionID"))
        if coalition_id > 0:
            applications_by_coalition[coalition_id].append(pid)

    enriched: list[dict[str, Any]] = []
    for raw_process in processes:
        process = dict(raw_process)
        pid = _integer(process.get("pid"))
        sysmon = sysmon_by_pid.get(pid, {})
        owner, ownership_source, ownership_ambiguous = _application_owner(
            process,
            sysmon_by_pid,
            applications_by_pid,
            applications_by_coalition,
        )
        process["responsiblePID"] = _integer(sysmon.get("responsiblePID"))
        process["coalitionID"] = _integer(sysmon.get("coalitionID"))
        process["startAbsTime"] = _integer(sysmon.get("startAbsTime"))
        process["processUniqueID"] = _integer(sysmon.get("uniqueID"))
        process["ownerPID"] = _integer(owner.get("pid")) if owner is not None else 0
        process["ownerName"] = str(owner.get("name") or "") if owner is not None else ""
        process["ownerBundleIdentifier"] = (
            str(owner.get("bundleIdentifier") or "") if owner is not None else ""
        )
        process["ownerDisplayLocalizedAppName"] = (
            str(owner.get("displayLocalizedAppName") or "") if owner is not None else ""
        )
        process["ownershipSource"] = ownership_source
        process["ownershipVerified"] = owner is not None
        process["ownershipAmbiguous"] = ownership_ambiguous
        if "startDate" in process:
            process["startDate"] = str(process["startDate"])
        enriched.append(process)
    return enriched


def _is_user_application_path(path: Any) -> bool:
    return _USER_APPLICATION_CONTAINER in str(path or "").replace("\\", "/").lower()


def apply_application_states(
    processes: list[dict[str, Any]],
    state_rows: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    states_by_pid = {
        _integer(row.get("pid")): row
        for row in state_rows
        if _integer(row.get("pid")) > 0
    }
    running_user_application_pids: list[int] = []
    enriched: list[dict[str, Any]] = []
    for raw_process in processes:
        process = dict(raw_process)
        pid = _integer(process.get("pid"))
        state = states_by_pid.get(pid, {})
        application_state = str(state.get("state_description") or "")
        executable_path = str(state.get("execName") or "")
        process["applicationState"] = application_state
        process["applicationExecutablePath"] = executable_path
        process["foregroundApplication"] = False
        if (
            bool(process.get("isApplication"))
            and application_state == "Running"
            and _is_user_application_path(executable_path)
        ):
            running_user_application_pids.append(pid)
        enriched.append(process)

    if len(running_user_application_pids) == 1:
        foreground_pid = running_user_application_pids[0]
        for process in enriched:
            process["foregroundApplication"] = _integer(process.get("pid")) == foreground_pid
    return enriched


def _application_state_rows(notification: Any) -> list[dict[str, Any]]:
    if not isinstance(notification, (list, tuple)) or len(notification) < 2:
        return []
    if notification[0] != "applicationStateNotification:":
        return []
    values = notification[1]
    if not isinstance(values, (list, tuple)):
        values = [values]
    return [value for value in values if isinstance(value, dict)]


async def collect_application_states(
    dvt: Any,
    initial_timeout_seconds: float = 2.5,
    idle_timeout_seconds: float = 0.2,
) -> list[dict[str, Any]]:
    from pymobiledevice3.services.dvt.instruments.notifications import Notifications

    states_by_pid: dict[int, dict[str, Any]] = {}
    loop = asyncio.get_running_loop()
    deadline = loop.time() + initial_timeout_seconds
    try:
        async with Notifications(dvt) as notifications:
            while loop.time() < deadline:
                remaining = deadline - loop.time()
                timeout = min(remaining, idle_timeout_seconds) if states_by_pid else remaining
                try:
                    notification = await asyncio.wait_for(
                        notifications.service.events.get(),
                        timeout=max(0.01, timeout),
                    )
                except asyncio.TimeoutError:
                    break
                for row in _application_state_rows(notification):
                    pid = _integer(row.get("pid"))
                    if pid > 0:
                        states_by_pid[pid] = dict(row)
    except Exception as exc:
        print(f"iOS application-state snapshot unavailable: {exc}", file=sys.stderr, flush=True)
    return list(states_by_pid.values())


async def emit_processes(udid: str, interval_ms: int) -> None:
    apply_windows_pytcp_cleanup_compat()
    from pymobiledevice3.remote.userspace_tunnel import UserspaceRsdTunnel
    from pymobiledevice3.services.dvt.instruments.device_info import DeviceInfo
    from pymobiledevice3.services.dvt.instruments.dvt_provider import DvtProvider
    from pymobiledevice3.services.dvt.instruments.sysmontap import Sysmontap

    tunnel = UserspaceRsdTunnel(serial=udid, autopair=True)
    async with tunnel as rsd, DvtProvider(rsd) as dvt:
        async with DeviceInfo(dvt) as device_info:
            processes = await device_info.proclist()

        application_states = await collect_application_states(dvt)

        sysmon_rows: list[dict[str, Any]] = []
        try:
            sysmon = await Sysmontap.create(dvt, interval=max(250, interval_ms))
            save_sysmon_schema(
                udid,
                list(sysmon.process_attributes_cls.__dataclass_fields__),
                list(sysmon.system_attributes_cls.__dataclass_fields__),
            )
            async with sysmon:
                async for rows in sysmon.iter_processes():
                    sysmon_rows = list(rows)
                    break
        except Exception as exc:
            print(f"iOS process ownership snapshot unavailable: {exc}", file=sys.stderr, flush=True)
        enriched = enrich_processes(list(processes), sysmon_rows)
        enriched = apply_application_states(enriched, application_states)
        print(
            json.dumps(enriched, ensure_ascii=False, separators=(",", ":"), default=str),
            flush=True,
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--udid", required=True)
    parser.add_argument("--interval", type=int, default=1000)
    args = parser.parse_args()
    asyncio.run(emit_processes(args.udid, args.interval))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
