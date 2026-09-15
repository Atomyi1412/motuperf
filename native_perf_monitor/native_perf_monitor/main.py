from __future__ import annotations

import sys

from PyQt5.QtWidgets import QApplication

from native_perf_monitor.ui import MainWindow


def run_app() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName("Native iOS Performance Monitor")
    window = MainWindow()
    window.resize(1440, 940)
    window.show()
    return app.exec_()


if __name__ == "__main__":
    raise SystemExit(run_app())
