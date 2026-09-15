from __future__ import annotations

import os
import sys
from pathlib import Path


def is_frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def app_dir() -> Path:
    if is_frozen():
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[1]


def data_dir() -> Path:
    env_dir = os.environ.get("NATIVE_IOS_PERF_DATA_DIR")
    if env_dir:
        return Path(env_dir).expanduser().resolve()
    return app_dir() / "data"
