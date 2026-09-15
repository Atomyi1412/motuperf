from __future__ import annotations

import hashlib
import json
import math
import os
import tempfile
import time
import uuid
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 1
DEFAULT_MAX_AGE_SECONDS = 3600.0


def _cache_directory(cache_dir: Path | str | None) -> Path:
    if cache_dir is not None:
        return Path(cache_dir)
    return Path(tempfile.gettempdir()) / "motuperf" / "ios-sysmon-schema"


def sysmon_schema_cache_path(udid: str, cache_dir: Path | str | None = None) -> Path:
    normalized_udid = str(udid or "").strip()
    digest = hashlib.sha256(normalized_udid.encode("utf-8")).hexdigest()[:24]
    return _cache_directory(cache_dir) / f"schema-{digest}.json"


def _attribute_names(value: Any, *, require_pid: bool) -> list[str] | None:
    if not isinstance(value, list):
        return None
    names: list[str] = []
    seen: set[str] = set()
    for item in value:
        name = str(item or "").strip()
        if not name or name in seen:
            continue
        seen.add(name)
        names.append(name)
    if require_pid and "pid" not in seen:
        return None
    return names


def save_sysmon_schema(
    udid: str,
    process_attributes: list[str],
    system_attributes: list[str],
    *,
    cache_dir: Path | str | None = None,
    now: float | None = None,
) -> bool:
    normalized_udid = str(udid or "").strip()
    normalized_process = _attribute_names(list(process_attributes), require_pid=True)
    normalized_system = _attribute_names(list(system_attributes), require_pid=False)
    if not normalized_udid or normalized_process is None or normalized_system is None:
        return False

    created_unix = time.time() if now is None else float(now)
    if not math.isfinite(created_unix):
        return False

    path = sysmon_schema_cache_path(normalized_udid, cache_dir=cache_dir)
    temporary_path = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    payload = {
        "schema_version": SCHEMA_VERSION,
        "udid": normalized_udid,
        "created_unix": created_unix,
        "process_attributes": normalized_process,
        "system_attributes": normalized_system,
    }
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary_path.write_text(
            json.dumps(payload, ensure_ascii=True, separators=(",", ":")),
            encoding="utf-8",
        )
        os.replace(temporary_path, path)
        return True
    except (OSError, TypeError, ValueError):
        try:
            temporary_path.unlink(missing_ok=True)
        except OSError:
            pass
        return False


def load_sysmon_schema(
    udid: str,
    *,
    cache_dir: Path | str | None = None,
    now: float | None = None,
    max_age_seconds: float = DEFAULT_MAX_AGE_SECONDS,
) -> tuple[list[str], list[str]] | None:
    normalized_udid = str(udid or "").strip()
    if not normalized_udid:
        return None
    try:
        payload = json.loads(
            sysmon_schema_cache_path(normalized_udid, cache_dir=cache_dir).read_text(encoding="utf-8")
        )
    except (OSError, UnicodeError, json.JSONDecodeError):
        return None
    if not isinstance(payload, dict):
        return None
    if payload.get("schema_version") != SCHEMA_VERSION or payload.get("udid") != normalized_udid:
        return None

    try:
        created_unix = float(payload.get("created_unix"))
        current_unix = time.time() if now is None else float(now)
        maximum_age = float(max_age_seconds)
    except (TypeError, ValueError):
        return None
    if not all(math.isfinite(value) for value in (created_unix, current_unix, maximum_age)):
        return None
    age_seconds = current_unix - created_unix
    if maximum_age < 0 or age_seconds < 0 or age_seconds > maximum_age:
        return None

    process_attributes = _attribute_names(payload.get("process_attributes"), require_pid=True)
    system_attributes = _attribute_names(payload.get("system_attributes"), require_pid=False)
    if process_attributes is None or system_attributes is None:
        return None
    return process_attributes, system_attributes
