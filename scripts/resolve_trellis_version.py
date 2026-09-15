#!/usr/bin/env python3
"""Resolve the newest appropriate Trellis npm install target."""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys


PACKAGE = "@mindfoldhq/trellis"


def find_executable(name: str) -> str | None:
    candidates = [name]
    if sys.platform.startswith("win"):
        candidates.extend([f"{name}.cmd", f"{name}.exe", f"{name}.ps1"])
    for candidate in candidates:
        path = shutil.which(candidate)
        if path:
            return path
    return None


def run(cmd: list[str]) -> tuple[int, str, str]:
    proc = subprocess.run(cmd, text=True, capture_output=True)
    return proc.returncode, proc.stdout.strip(), proc.stderr.strip()


def version_key(version: str) -> tuple[int, int, int, int, int]:
    match = re.match(r"^(\d+)\.(\d+)\.(\d+)(?:-([a-z]+)\.(\d+))?$", version)
    if not match:
        return (0, 0, 0, -2, 0)
    major, minor, patch = (int(match.group(i)) for i in range(1, 4))
    label = match.group(4)
    number = int(match.group(5) or 0)
    rank = {"beta": -1, "rc": 0}.get(label, 1)
    return (major, minor, patch, rank, number)


def main() -> int:
    npm = find_executable("npm")
    if not npm:
        print("[FAIL] npm is not available; cannot check Trellis dist-tags")
        return 2

    code, stdout, stderr = run([npm, "view", PACKAGE, "dist-tags", "--json"])
    if code != 0:
        print("[FAIL] npm dist-tag lookup failed")
        if stderr:
            print(stderr)
        return code

    tags = json.loads(stdout)
    latest = tags.get("latest")
    rc = tags.get("rc")
    beta = tags.get("beta")
    candidates = [("latest", latest), ("rc", rc), ("beta", beta)]
    candidates = [(tag, version) for tag, version in candidates if version]
    recommended_tag, recommended_version = max(candidates, key=lambda item: version_key(item[1]))

    trellis_cmd = find_executable("trellis")
    local_version = None
    if trellis_cmd:
        code, local_stdout, _ = run([trellis_cmd, "--version"])
        if code == 0 and local_stdout:
            local_version = local_stdout.splitlines()[0].strip()

    install_target = PACKAGE if recommended_tag == "latest" else f"{PACKAGE}@{recommended_tag}"
    print(f"latest={latest}")
    if rc:
        print(f"rc={rc}")
    if beta:
        print(f"beta={beta}")
    print(f"recommended={recommended_version} ({recommended_tag})")
    print(f"install_command=npm install -g {install_target}")
    if local_version:
        print(f"local={local_version}")
        if version_key(local_version) < version_key(recommended_version):
            print("[ACTION] Update Trellis CLI before initializing/updating a project.")
        else:
            print("[OK] Local Trellis CLI is current enough.")
    else:
        print("[ACTION] Install Trellis CLI before initializing/updating a project.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
