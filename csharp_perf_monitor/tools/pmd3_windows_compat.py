from __future__ import annotations

import errno
import os
from typing import Any


_GUARD_MARKER = "_motuperf_windows_cleanup_guard"


def apply_windows_pytcp_cleanup_compat(
    *,
    os_module: Any = os,
    io_backend_module: Any = None,
) -> bool:
    """Make PyTCP's already-closed Windows interface fail through its OSError path."""
    if getattr(os_module, "name", "") != "nt" or hasattr(os_module, "writev"):
        return False

    if io_backend_module is None:
        try:
            from pmd_pytcp.lib import io_backend as io_backend_module
        except ImportError:
            return False

    original_writev = io_backend_module.writev
    if getattr(original_writev, _GUARD_MARKER, False):
        return False

    def guarded_writev(fd: int, buffers: Any) -> int:
        try:
            return original_writev(fd, buffers)
        except AttributeError as exc:
            missing_os_writev = (
                getattr(exc, "name", None) == "writev"
                and getattr(exc, "obj", None) is os_module
            )
            if not missing_os_writev:
                raise
            raise OSError(
                errno.EBADF,
                "PyTCP interface was unregistered during Windows tunnel shutdown",
            ) from exc

    setattr(guarded_writev, _GUARD_MARKER, True)
    io_backend_module.writev = guarded_writev
    return True
