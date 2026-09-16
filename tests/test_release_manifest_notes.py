from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/generate-update-manifest.ps1"
SHELL = shutil.which("pwsh") or shutil.which("powershell")


@unittest.skipUnless(SHELL, "PowerShell required")
class ReleaseManifestNotesTests(unittest.TestCase):
    def test_default_changelog_path_in_packaging_shells(self):
        project = ROOT / "motuperf_cross_platform/src/MoTuPerf.Desktop/MoTuPerf.Desktop.csproj"
        version = ET.parse(project).findtext(".//Version")
        shells = {path for name in ("powershell", "pwsh") if (path := shutil.which(name))}
        # Let each PowerShell version use its own built-in modules.
        shell_env = {key: value for key, value in os.environ.items() if key.casefold() != "psmodulepath"}
        for shell in sorted(shells):
            with self.subTest(shell=shell), tempfile.TemporaryDirectory(prefix="motuperf-manifest-default-") as temp:
                root = Path(temp)
                for name in (f"MoTuPerf-Setup-v{version}.exe", f"MoTuPerf-v{version}-osx-arm64.dmg"):
                    (root / name).write_bytes(b"asset")
                output = root / "latest.json"
                result = subprocess.run(
                    [shell, "-NoProfile", "-File", str(SCRIPT), "-Version", version,
                     "-AssetsDirectory", str(root), "-OutputPath", str(output)],
                    cwd=root, env=shell_env, capture_output=True, timeout=30)
                self.assertEqual(0, result.returncode, result.stderr)
                manifest = json.loads(output.read_text(encoding="utf-8-sig"))
                self.assertEqual(version, manifest["version"])
                self.assertGreater(len(manifest["releaseNotes"]), 0)

    def test_extracts_only_published_version_and_preserves_schema(self):
        with tempfile.TemporaryDirectory(prefix="motuperf-manifest-") as temp:
            root = Path(temp)
            for name in ("MoTuPerf-Setup-v1.2.3.exe", "MoTuPerf-v1.2.3-osx-arm64.dmg"):
                (root / name).write_bytes(b"asset")
            notes = root / "CHANGELOG.md"
            notes.write_text("# 更新日志\n\n## v9.9.9\n- 其他版本\n\n## v1.2.3 - 2026-09-16\n- 修复窗口\n- 支持 `JSON`\n\n## v1.2.2\n- 旧内容\n", encoding="utf-8")
            output = root / "latest.json"
            body = root / "release.md"
            args = [SHELL, "-NoProfile", "-File", str(SCRIPT), "-Version", "1.2.3",
                    "-AssetsDirectory", str(root), "-OutputPath", str(output),
                    "-ChangelogPath", str(notes), "-ReleaseNotesPath", str(body)]
            result = subprocess.run(args, capture_output=True, timeout=30)
            self.assertEqual(0, result.returncode, result.stderr)
            manifest = json.loads(output.read_text(encoding="utf-8-sig"))
            self.assertEqual(1, manifest["schema"])
            self.assertEqual(["修复窗口", "支持 JSON"], manifest["releaseNotes"])
            self.assertNotIn("其他版本", body.read_text(encoding="utf-8-sig"))
            self.assertIn("支持 `JSON`", body.read_text(encoding="utf-8-sig"))
            notes.write_text("# No matching version\n", encoding="utf-8")
            result = subprocess.run(args, capture_output=True, timeout=30)
            self.assertNotEqual(0, result.returncode)
