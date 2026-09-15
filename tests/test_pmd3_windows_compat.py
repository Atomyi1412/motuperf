from __future__ import annotations

import unittest
import sys
from pathlib import Path
from types import SimpleNamespace


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "csharp_perf_monitor" / "tools"))


class Pmd3WindowsCompatTests(unittest.TestCase):
    def test_missing_windows_writev_becomes_expected_os_error(self) -> None:
        from pmd3_windows_compat import apply_windows_pytcp_cleanup_compat

        fake_os = SimpleNamespace(name="nt")

        def writev(fd: int, buffers: object) -> int:
            return fake_os.writev(fd, buffers)

        backend = SimpleNamespace(writev=writev)

        self.assertTrue(
            apply_windows_pytcp_cleanup_compat(
                os_module=fake_os,
                io_backend_module=backend,
            )
        )
        with self.assertRaises(OSError):
            backend.writev(123, [b"closing"])

    def test_cleanup_guard_is_idempotent_and_keeps_successful_writes(self) -> None:
        from pmd3_windows_compat import apply_windows_pytcp_cleanup_compat

        fake_os = SimpleNamespace(name="nt")
        backend = SimpleNamespace(writev=lambda _fd, buffers: sum(map(len, buffers)))

        self.assertTrue(
            apply_windows_pytcp_cleanup_compat(
                os_module=fake_os,
                io_backend_module=backend,
            )
        )
        self.assertFalse(
            apply_windows_pytcp_cleanup_compat(
                os_module=fake_os,
                io_backend_module=backend,
            )
        )
        self.assertEqual(backend.writev(123, [b"ab", b"c"]), 3)

    def test_unrelated_attribute_error_is_not_hidden(self) -> None:
        from pmd3_windows_compat import apply_windows_pytcp_cleanup_compat

        fake_os = SimpleNamespace(name="nt")

        def writev(_fd: int, _buffers: object) -> int:
            return fake_os.unrelated_attribute

        backend = SimpleNamespace(writev=writev)
        apply_windows_pytcp_cleanup_compat(
            os_module=fake_os,
            io_backend_module=backend,
        )

        with self.assertRaises(AttributeError):
            backend.writev(123, [b"data"])


if __name__ == "__main__":
    unittest.main()
