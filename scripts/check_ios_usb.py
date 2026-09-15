from __future__ import annotations

import shutil
import subprocess


COMMANDS = ["pyidevice", "tidevice", "pymobiledevice3", "idevice_id", "ideviceinfo"]


def run(command: list[str]) -> tuple[int, str]:
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=10, errors="replace")
    except OSError as exc:
        return 127, str(exc)
    except subprocess.TimeoutExpired:
        return 124, "command timed out"
    output = (result.stdout or "") + (result.stderr or "")
    return result.returncode, output.strip()


def main() -> int:
    print("iOS USB dependency check")
    print("========================")
    missing = []
    for command in COMMANDS:
        path = shutil.which(command)
        if path:
            print(f"[OK] {command}: {path}")
        else:
            print(f"[MISS] {command}")
            missing.append(command)

    if shutil.which("tidevice"):
        code, output = run(["tidevice", "list"])
        print("\n`tidevice list`")
        print(f"exit={code}")
        print(output or "<no output>")

    if shutil.which("pyidevice"):
        code, output = run(["pyidevice", "devices"])
        print("\n`pyidevice devices`")
        print(f"exit={code}")
        print(output or "<no output>")

    if "pyidevice" in missing and "tidevice" in missing:
        print("\n[ACTION] Install optional dependencies:")
        print("python -m pip install -r requirements-ios.txt")
        return 1

    print("\n[NOTE] If no device is listed, unlock the iPhone, trust this computer, and reconnect USB.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
