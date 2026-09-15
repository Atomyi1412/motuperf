from __future__ import annotations

import os
import shutil
import sys
from pathlib import Path


def is_frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def resource_root() -> Path:
    if is_frozen():
        return Path(getattr(sys, "_MEIPASS")).resolve()
    return Path(__file__).resolve().parents[1]


def app_dir() -> Path:
    if is_frozen():
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[1]


def frontend_dir() -> Path:
    return resource_root() / "frontend"


def data_dir() -> Path:
    env_dir = os.environ.get("IOS_PERF_DATA_DIR")
    if env_dir:
        return Path(env_dir).expanduser().resolve()
    return app_dir() / "data"


def tool_command(name: str) -> str:
    if is_frozen() and name in {"tidevice", "pyidevice"}:
        return str(Path(sys.executable).resolve())
    return name


def tool_args(name: str, args: list[str]) -> list[str]:
    if is_frozen() and name in {"tidevice", "pyidevice"}:
        return [tool_command(name), "--tool", name, *args]
    return [tool_command(name), *args]


def packaged_tool_available(name: str) -> bool:
    if not is_frozen():
        return False
    return name in {"tidevice", "pyidevice"}


def tool_status(name: str) -> str:
    if packaged_tool_available(name):
        return tool_command(name)
    path = shutil.which(name)
    return path if path else "missing"
