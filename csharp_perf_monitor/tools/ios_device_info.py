from __future__ import annotations

import argparse
import asyncio
import json
import sys
from typing import Any


def _number(value: Any) -> int:
    if value is None or isinstance(value, bool):
        return 0
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def _resolution(values: dict[str, Any]) -> str:
    width = _number(values.get("ScreenWidth"))
    height = _number(values.get("ScreenHeight"))
    if width <= 0 or height <= 0:
        return ""
    # Keep the physical long edge first, matching the device-information tools.
    return f"{max(width, height)}x{min(width, height)}"


async def read_device_info(udid: str) -> dict[str, Any]:
    from pymobiledevice3.irecv_devices import IRECV_DEVICES
    from pymobiledevice3.lockdown import create_using_usbmux

    lockdown = await create_using_usbmux(serial=udid, autopair=False)
    try:
        values = await lockdown.get_value()
        if not isinstance(values, dict):
            values = {}
        itunes = await lockdown.get_value(domain="com.apple.mobile.iTunes")
        if not isinstance(itunes, dict):
            itunes = {}

        product_type = str(values.get("ProductType") or "")
        market_name = ""
        for device in IRECV_DEVICES:
            if device.product_type == product_type:
                market_name = str(device.display_name or "")
                break

        return {
            "device_name": str(values.get("DeviceName") or ""),
            "market_name": market_name,
            "product_type": product_type,
            "hardware_platform": str(values.get("HardwarePlatform") or ""),
            "cpu_architecture": str(values.get("CPUArchitecture") or ""),
            "resolution": _resolution(itunes),
            "screen_width": _number(itunes.get("ScreenWidth")),
            "screen_height": _number(itunes.get("ScreenHeight")),
            "screen_scale_factor": itunes.get("ScreenScaleFactor"),
        }
    finally:
        await lockdown.close()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--udid", required=True)
    args = parser.parse_args()
    try:
        result = asyncio.run(read_device_info(args.udid))
    except Exception as exc:
        print(f"iOS device details unavailable: {exc}", file=sys.stderr, flush=True)
        return 1
    # ASCII JSON keeps Chinese device names intact through every Windows process pipe.
    print(json.dumps(result, ensure_ascii=True, separators=(",", ":")), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
