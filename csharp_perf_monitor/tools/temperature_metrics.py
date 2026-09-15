from __future__ import annotations

import math
import re
from collections.abc import Mapping


ANDROID_THERMAL_TYPES = {
    0: "CPU",
    1: "GPU",
    2: "Battery",
    3: "Skin",
    4: "USB",
    5: "Power Amplifier",
    9: "NPU",
}

ANDROID_THERMAL_STATUS_NAMES = {
    0: "none",
    1: "light",
    2: "moderate",
    3: "severe",
    4: "critical",
    5: "emergency",
    6: "shutdown",
}

_ANDROID_TEMPERATURE = re.compile(
    r"Temperature\{[^}]*?mValue\s*=\s*(?P<value>[-+0-9.eE]+)"
    r"[^}]*?mType\s*=\s*(?P<type>\d+)",
    re.IGNORECASE,
)

_ANDROID_THERMAL_STATUS = re.compile(
    r"^\s*Thermal\s+Status\s*:\s*(?P<level>\d+)\s*$",
    re.IGNORECASE | re.MULTILINE,
)


def valid_temperature_c(value: object) -> float | None:
    try:
        parsed = float(value)
    except (TypeError, ValueError):
        return None
    if not math.isfinite(parsed) or parsed < -20.0 or parsed > 120.0:
        return None
    return parsed


def parse_android_thermalservice(text: str) -> dict[str, float]:
    values: dict[str, float] = {}
    for match in _ANDROID_TEMPERATURE.finditer(text or ""):
        sensor = ANDROID_THERMAL_TYPES.get(int(match.group("type")))
        value = valid_temperature_c(match.group("value"))
        if sensor is None or value is None:
            continue
        previous = values.get(sensor)
        if previous is None or value > previous:
            values[sensor] = value
    return values


def parse_android_thermal_status(text: str) -> tuple[int, str] | None:
    """Parse Android's aggregate PowerManager thermal status.

    Per-sensor ``mStatus`` fields are intentionally ignored because they are
    not the device-wide throttling status exposed by ``Thermal Status``.
    """
    match = _ANDROID_THERMAL_STATUS.search(text or "")
    if match is None:
        return None
    level = int(match.group("level"))
    name = ANDROID_THERMAL_STATUS_NAMES.get(level)
    return (level, name) if name is not None else None


def parse_android_battery_temperature(text: str) -> float | None:
    match = re.search(r"^\s*temperature\s*:\s*(-?\d+)\s*$", text or "", re.IGNORECASE | re.MULTILINE)
    if match is None:
        return None
    return valid_temperature_c(int(match.group(1)) / 10.0)


def parse_ios_battery_temperature(info: Mapping[str, object] | None) -> float | None:
    if not info:
        return None
    try:
        raw = float(info.get("Temperature"))
    except (TypeError, ValueError):
        return None
    if not math.isfinite(raw):
        return None
    return valid_temperature_c(raw / 100.0)


IOS_THERMAL_STATE_NAMES = {
    0: "nominal",
    1: "fair",
    2: "serious",
    3: "critical",
}


def parse_ios_thermal_state(payload: object, pid: int) -> tuple[int, str] | None:
    """Read the real Xcode Energy thermal-state field for one process.

    ``energy.inducedthermalstate.cost`` is deliberately ignored because it is
    the condition-inducer value, not the device's observed thermal state.
    """
    attributes = _ios_thermal_state_attributes(payload, pid)
    if attributes is None:
        return None
    raw = attributes.get("energy.thermalstate.cost")
    if raw is None or isinstance(raw, bool):
        return None
    try:
        numeric = float(raw)
    except (TypeError, ValueError):
        return None
    if not math.isfinite(numeric) or numeric != int(numeric):
        return None
    level = int(numeric)
    name = IOS_THERMAL_STATE_NAMES.get(level)
    return (level, name) if name is not None else None


def ios_thermal_state_unavailable_reason(payload: object, pid: int) -> str | None:
    """Return a safe diagnostic reason when the real field is unavailable."""
    if not isinstance(payload, Mapping):
        return "response_not_mapping"
    if not payload:
        return "empty_response"
    if isinstance(pid, bool) or pid <= 0:
        return "invalid_pid"
    attributes = _ios_thermal_state_attributes(payload, pid)
    if attributes is None:
        return "pid_attributes_missing"
    if "energy.thermalstate.cost" not in attributes:
        return "thermal_state_field_missing"
    raw = attributes.get("energy.thermalstate.cost")
    if isinstance(raw, bool):
        return "thermal_state_field_invalid"
    try:
        numeric = float(raw)
    except (TypeError, ValueError):
        return "thermal_state_field_invalid"
    if not math.isfinite(numeric) or numeric != int(numeric) or int(numeric) not in IOS_THERMAL_STATE_NAMES:
        return "thermal_state_field_invalid"
    return None


def _ios_thermal_state_attributes(payload: object, pid: int) -> Mapping[str, object] | None:
    if not isinstance(payload, Mapping) or isinstance(pid, bool) or pid <= 0:
        return None
    attributes = payload.get(pid)
    if attributes is None:
        attributes = payload.get(str(pid))
    return attributes if isinstance(attributes, Mapping) else None
