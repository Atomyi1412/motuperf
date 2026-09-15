"""Strict process identity matching used when an iOS app restarts in place.

The live sysmontap stream does not consistently carry a bundle identifier, so
the candidate is resolved through the device-info process list first.  A
same-name process alone is never sufficient to switch a target.
"""

from __future__ import annotations

from typing import Any


WEBKIT_PROCESS_PREFIX = "com.apple.WebKit."


def _integer(value: Any) -> int:
    if value is None or isinstance(value, bool):
        return 0
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def _text(value: Any) -> str:
    return str(value or "").strip()


def _bundle_id(process: dict[str, Any]) -> str:
    for key in (
        "bundleIdentifier",
        "bundleId",
        "bundleID",
        "applicationBundleIdentifier",
    ):
        value = _text(process.get(key))
        if value:
            return value
    return ""


def is_webkit_process(name: Any) -> bool:
    return _text(name).startswith(WEBKIT_PROCESS_PREFIX)


def find_application_rebind_candidate(
    processes: list[dict[str, Any]],
    current_pid: int,
    expected_name: str,
    expected_bundle_id: str,
) -> dict[str, Any] | None:
    """Return one verified application-main-process candidate, if unique.

    DeviceInfo's ``runningProcesses`` records include the application bundle
    identifier.  Require that identity and an application record.  WebKit
    extension processes are deliberately excluded because their page/owner
    relationship needs a separate coalition proof and must not be rebound by
    name.
    """

    name = _text(expected_name)
    bundle_id = _text(expected_bundle_id)
    if not name or not bundle_id or is_webkit_process(name):
        return None

    candidates: list[dict[str, Any]] = []
    for raw_process in processes:
        if not isinstance(raw_process, dict):
            continue
        pid = _integer(raw_process.get("pid"))
        if pid <= 0 or pid == current_pid:
            continue
        if _text(raw_process.get("name")) != name:
            continue
        if _bundle_id(raw_process) != bundle_id:
            continue
        if not bool(raw_process.get("isApplication")):
            continue
        candidates.append(dict(raw_process))

    return candidates[0] if len(candidates) == 1 else None

