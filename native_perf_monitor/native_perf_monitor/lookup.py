from __future__ import annotations

import threading
from collections.abc import Callable
from typing import Any

from PyQt5.QtCore import QObject, pyqtSignal

from backend.collectors.ios import list_ios_apps, list_ios_devices, list_ios_processes


class LookupService(QObject):
    devices_loaded = pyqtSignal(object)
    apps_loaded = pyqtSignal(object)
    processes_loaded = pyqtSignal(object)
    failed = pyqtSignal(str, bool)

    def refresh_devices(self) -> None:
        self._run(lambda: [device.to_dict() for device in list_ios_devices()], self.devices_loaded.emit, False)

    def refresh_apps(self, udid: str) -> None:
        self._run(lambda: [app.to_dict() for app in list_ios_apps(udid)], self.apps_loaded.emit, False)

    def refresh_processes(self, udid: str) -> None:
        self._run(lambda: [process.to_dict() for process in list_ios_processes(udid)], self.processes_loaded.emit, False)

    def _run(self, fn: Callable[[], list[dict[str, Any]]], emit: Callable[[object], None], modal: bool) -> None:
        def target() -> None:
            try:
                emit(fn())
            except Exception as exc:  # noqa: BLE001 - surface device-tool failures in the UI.
                self.failed.emit(str(exc), modal)

        threading.Thread(target=target, daemon=True).start()
