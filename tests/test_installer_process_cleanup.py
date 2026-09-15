from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import time
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
HELPER = ROOT / "csharp_perf_monitor" / "installer" / "StopInstalledProcesses.ps1"


@unittest.skipUnless(os.name == "nt", "Windows installer process cleanup is Windows-only")
class InstallerProcessCleanupTests(unittest.TestCase):
    def test_cleanup_stops_only_processes_inside_install_directory(self) -> None:
        ping = Path(os.environ["SystemRoot"]) / "System32" / "ping.exe"
        with tempfile.TemporaryDirectory(prefix="motuperf-installer-") as temp:
            root = Path(temp)
            install_dir = root / "installed"
            inside_dir = install_dir / "runtime" / "android"
            outside_dir = root / "external"
            inside_dir.mkdir(parents=True)
            outside_dir.mkdir(parents=True)
            inside_exe = inside_dir / "adb.exe"
            outside_exe = outside_dir / "adb.exe"
            shutil.copy2(ping, inside_exe)
            shutil.copy2(ping, outside_exe)

            flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            inside = subprocess.Popen(
                [str(inside_exe), "-t", "127.0.0.1"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=flags,
            )
            outside = subprocess.Popen(
                [str(outside_exe), "-t", "127.0.0.1"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=flags,
            )
            try:
                time.sleep(0.5)
                detected = self._run_helper(install_dir, "-DetectOnly")
                self.assertEqual(10, detected.returncode, detected.stderr)

                stopped = self._run_helper(install_dir)
                self.assertEqual(0, stopped.returncode, stopped.stderr)
                inside.wait(timeout=5)
                self.assertIsNone(outside.poll(), "cleanup terminated an external adb.exe")
            finally:
                for process in (inside, outside):
                    if process.poll() is None:
                        process.kill()
                    process.wait(timeout=5)

    @staticmethod
    def _run_helper(install_dir: Path, *extra: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(HELPER),
                "-InstallDirectory",
                str(install_dir),
                *extra,
            ],
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )


if __name__ == "__main__":
    unittest.main()
