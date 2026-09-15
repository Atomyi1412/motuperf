from __future__ import annotations

import argparse
import sys
from pathlib import Path


def main() -> None:
    native_root = Path(__file__).resolve().parent
    project_root = native_root.parent
    if str(native_root) not in sys.path:
        sys.path.insert(0, str(native_root))
    if str(project_root) not in sys.path:
        sys.path.insert(1, str(project_root))

    tool_request = parse_tool_request(sys.argv[1:])
    if tool_request:
        name, args = tool_request
        run_tool(name, args)
        return

    parser = argparse.ArgumentParser(description="Native iOS performance monitor")
    parser.add_argument("--check", action="store_true", help="check native UI dependencies and exit")
    args = parser.parse_args()

    if args.check:
        check_dependencies()
        return

    from native_perf_monitor.main import run_app

    run_app()


def parse_tool_request(argv: list[str]) -> tuple[str, list[str]] | None:
    if "--tool" not in argv:
        return None
    index = argv.index("--tool")
    if index + 1 >= len(argv):
        raise SystemExit("--tool needs tidevice or pyidevice")
    name = argv[index + 1]
    if name not in {"tidevice", "pyidevice"}:
        raise SystemExit(f"unsupported bundled tool: {name}")
    return name, argv[index + 2 :]


def run_tool(name: str, args: list[str]) -> None:
    sys.argv = [name, *args]
    if name == "tidevice":
        from tidevice.__main__ import main as tidevice_main

        tidevice_main()
        return
    if name == "pyidevice":
        from ios_device.main import cli

        cli()
        return
    raise SystemExit(f"unsupported tool: {name}")


def check_dependencies() -> None:
    missing: list[str] = []
    for module in ("PyQt5", "pyqtgraph", "numpy", "PIL"):
        try:
            __import__(module)
        except ImportError:
            missing.append(module)
    if missing:
        raise SystemExit("Missing native dependencies: " + ", ".join(missing))
    print("Native dependencies OK")


if __name__ == "__main__":
    main()
