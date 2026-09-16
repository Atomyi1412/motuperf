from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
HELPER = ROOT / "motuperf_cross_platform/packaging/windows/ResolveInstallDirectory.ps1"


@unittest.skipUnless(os.name == "nt", "Windows installer directory resolution")
class InstallerDirectoryTests(unittest.TestCase):
    def test_origin_wins_and_ambiguous_fallback_does_not_guess(self):
        with tempfile.TemporaryDirectory(prefix="motuperf-directory-") as temp:
            root = Path(temp).resolve()
            current = root / "当前软件 Program Files"
            other = root / "old-copy"
            invalid = root / "unrelated"
            for directory in (current, other, invalid):
                directory.mkdir()
                shutil.copy2(Path(os.environ["SystemRoot"]) / "System32/ping.exe",
                             directory / "MoTuPerf.CrossPlatform.exe")
            for directory in (current, other):
                (directory / "motuperf-package.json").write_text(
                    json.dumps({"schema": 1, "target": "win-x64"}), encoding="utf-8")
            cases = [
                # Installer -> shell -> invoking client, plus another installed copy.
                (30, [(30, 20, ""), (20, 10, ""), (10, 0, str(current)), (40, 0, str(other))], str(current)),
                (99, [(10, 0, str(current))], str(current)),
                (99, [(10, 0, str(current)), (40, 0, str(other))], ""),
                (99, [(10, 0, str(invalid))], ""),
                (99, [], ""),
                (30, [(30, 20, ""), (20, 30, "")], ""),
            ]
            env = dict(os.environ, MOTUPERF_RESOLVER=str(HELPER))
            command = r"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. $env:MOTUPERF_RESOLVER
$cases = Get-Content -LiteralPath $env:MOTUPERF_CASES -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($case in $cases) {
    $actual = Resolve-MoTuPerfDirectory @($case.processes) $case.origin
    if ([string]$actual -cne [string]$case.expected) { throw "Unexpected directory: $actual; expected $($case.expected)" }
}
Write-Output 'All directory cases passed'
"""
            fixture = root / "cases.json"
            fixture.write_text(json.dumps([
                {"origin": origin, "expected": expected, "processes": [
                    {"ProcessId": pid, "ParentProcessId": parent,
                     "ExecutablePath": str(Path(directory) / "MoTuPerf.CrossPlatform.exe") if directory else ""}
                    for pid, parent, directory in entries]}
                for origin, entries, expected in cases
            ]), encoding="utf-8")
            env["MOTUPERF_CASES"] = str(fixture)
            result = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", command],
                                    env=env, capture_output=True, encoding="utf-8", timeout=30)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
