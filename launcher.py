from __future__ import annotations

import argparse
import socket
import sys
import threading
import time
import webbrowser

def main() -> None:
    tool_request = parse_tool_request(sys.argv[1:])
    if tool_request:
        name, args = tool_request
        run_tool(name, args)
        return

    parser = argparse.ArgumentParser(description="iOS 性能采集工具")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8770)
    args = parser.parse_args()

    from backend.server import run

    port = choose_port(args.host, args.port)
    url = f"http://{args.host}:{port}"
    thread = threading.Thread(target=run, kwargs={"host": args.host, "port": port}, daemon=True)
    thread.start()
    wait_for_server(args.host, port)
    webbrowser.open(url)
    print(f"iOS 性能采集工具已启动：{url}")
    print("关闭此窗口即可退出工具。")
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        print("正在退出。")


def parse_tool_request(argv: list[str]) -> tuple[str, list[str]] | None:
    if "--tool" not in argv:
        return None
    index = argv.index("--tool")
    if index + 1 >= len(argv):
        raise SystemExit("--tool 需要指定 tidevice 或 pyidevice")
    name = argv[index + 1]
    if name not in {"tidevice", "pyidevice"}:
        raise SystemExit(f"不支持的内置工具：{name}")
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


def choose_port(host: str, preferred: int) -> int:
    for port in range(preferred, preferred + 20):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.settimeout(0.2)
            if sock.connect_ex((host, port)) != 0:
                return port
    raise RuntimeError("没有找到可用端口。")


def wait_for_server(host: str, port: int) -> None:
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.settimeout(0.2)
            if sock.connect_ex((host, port)) == 0:
                return
        time.sleep(0.1)
    raise RuntimeError("本地服务启动超时。")


if __name__ == "__main__":
    main()
