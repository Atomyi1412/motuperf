from __future__ import annotations

import csv
import math
from pathlib import Path
from typing import Any

import numpy as np
import pyqtgraph as pg
from PyQt5.QtCore import Qt, QTimer
from PyQt5.QtGui import QPixmap
from PyQt5.QtWidgets import (
    QCheckBox,
    QComboBox,
    QFileDialog,
    QFormLayout,
    QFrame,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QListWidget,
    QListWidgetItem,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QSpinBox,
    QSplitter,
    QVBoxLayout,
    QWidget,
)

from native_perf_monitor.lookup import LookupService
from native_perf_monitor.models import CaptureConfig, NativeSample, NativeScreenshot
from native_perf_monitor.runtime import data_dir
from native_perf_monitor.session import NativeSession


MAX_PLOT_POINTS = 2400
WECHAT_BUNDLE = "com.tencent.xin"


class MainWindow(QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        pg.setConfigOptions(antialias=False, background="#141821", foreground="#d6dbe6")
        self.setWindowTitle("iOS 性能采集工具 - 原生版")

        self.samples: list[NativeSample] = []
        self.screenshots: list[NativeScreenshot] = []
        self.devices: list[dict[str, Any]] = []
        self.apps: list[dict[str, Any]] = []
        self.processes: list[dict[str, Any]] = []
        self.selected_time: float | None = None
        self.follow_latest = True
        self.dirty = False

        self.lookup = LookupService()
        self.session = NativeSession(data_dir())
        self._build_ui()
        self._connect_signals()

        self.render_timer = QTimer(self)
        self.render_timer.setInterval(500)
        self.render_timer.timeout.connect(self._render_if_dirty)
        self.render_timer.start()

        self._set_status("准备就绪。")
        self.refresh_devices()

    def _build_ui(self) -> None:
        central = QWidget(self)
        root = QVBoxLayout(central)
        root.setContentsMargins(10, 10, 10, 10)
        root.setSpacing(8)

        header = QHBoxLayout()
        self.status_label = QLabel("准备就绪")
        self.status_label.setObjectName("statusLabel")
        self.run_badge = QLabel("未采集")
        self.run_badge.setObjectName("badge")
        header.addWidget(self.status_label, 1)
        header.addWidget(self.run_badge)
        root.addLayout(header)

        splitter = QSplitter(Qt.Horizontal)
        splitter.addWidget(self._build_control_panel())
        splitter.addWidget(self._build_chart_panel())
        splitter.setStretchFactor(0, 0)
        splitter.setStretchFactor(1, 1)
        root.addWidget(splitter, 1)
        self.setCentralWidget(central)
        self._apply_styles()

    def _build_control_panel(self) -> QWidget:
        panel = QFrame()
        panel.setObjectName("sidePanel")
        panel.setMinimumWidth(330)
        panel.setMaximumWidth(390)
        layout = QVBoxLayout(panel)
        layout.setSpacing(10)

        form = QFormLayout()
        self.device_combo = QComboBox()
        self.refresh_devices_btn = QPushButton("刷新设备")
        device_row = QHBoxLayout()
        device_row.addWidget(self.device_combo, 1)
        device_row.addWidget(self.refresh_devices_btn)
        form.addRow("设备", device_row)

        self.app_search = QLineEdit()
        self.app_search.setPlaceholderText("搜索应用名称 / Bundle")
        self.app_combo = QComboBox()
        self.refresh_apps_btn = QPushButton("刷新应用")
        app_row = QVBoxLayout()
        app_top = QHBoxLayout()
        app_top.addWidget(self.app_combo, 1)
        app_top.addWidget(self.refresh_apps_btn)
        app_row.addWidget(self.app_search)
        app_row.addLayout(app_top)
        form.addRow("应用", app_row)

        self.bundle_input = QLineEdit(WECHAT_BUNDLE)
        form.addRow("Bundle", self.bundle_input)

        self.process_search = QLineEdit()
        self.process_search.setPlaceholderText("搜索进程名 / pid / Bundle")
        self.process_combo = QComboBox()
        self.refresh_processes_btn = QPushButton("刷新进程")
        process_row = QVBoxLayout()
        process_top = QHBoxLayout()
        process_top.addWidget(self.process_combo, 1)
        process_top.addWidget(self.refresh_processes_btn)
        process_row.addWidget(self.process_search)
        process_row.addLayout(process_top)
        form.addRow("进程", process_row)
        layout.addLayout(form)

        self.process_hint = QLabel("")
        self.process_hint.setWordWrap(True)
        self.process_hint.setObjectName("hint")
        layout.addWidget(self.process_hint)

        capture_form = QFormLayout()
        self.screenshot_check = QCheckBox("记录实时截图")
        self.screenshot_interval = QSpinBox()
        self.screenshot_interval.setRange(3, 300)
        self.screenshot_interval.setValue(3)
        self.screenshot_interval.setSuffix(" 秒")
        capture_form.addRow("", self.screenshot_check)
        capture_form.addRow("截图间隔", self.screenshot_interval)
        layout.addLayout(capture_form)

        actions = QGridLayout()
        self.start_btn = QPushButton("开始")
        self.stop_btn = QPushButton("停止")
        self.clear_btn = QPushButton("清空")
        self.export_btn = QPushButton("导出 CSV")
        self.follow_btn = QPushButton("跟随最新")
        actions.addWidget(self.start_btn, 0, 0)
        actions.addWidget(self.stop_btn, 0, 1)
        actions.addWidget(self.clear_btn, 1, 0)
        actions.addWidget(self.export_btn, 1, 1)
        actions.addWidget(self.follow_btn, 2, 0, 1, 2)
        layout.addLayout(actions)

        metrics = QGridLayout()
        self.fps_value = QLabel("--")
        self.jank_value = QLabel("--")
        self.big_jank_value = QLabel("--")
        self.memory_value = QLabel("--")
        self.duration_value = QLabel("0s")
        for widget in (self.fps_value, self.jank_value, self.big_jank_value, self.memory_value, self.duration_value):
            widget.setObjectName("metricValue")
        metrics.addWidget(QLabel("FPS"), 0, 0)
        metrics.addWidget(self.fps_value, 0, 1)
        metrics.addWidget(QLabel("Jank"), 1, 0)
        metrics.addWidget(self.jank_value, 1, 1)
        metrics.addWidget(QLabel("BigJank"), 2, 0)
        metrics.addWidget(self.big_jank_value, 2, 1)
        metrics.addWidget(QLabel("内存"), 3, 0)
        metrics.addWidget(self.memory_value, 3, 1)
        metrics.addWidget(QLabel("时长"), 4, 0)
        metrics.addWidget(self.duration_value, 4, 1)
        layout.addLayout(metrics)

        self.screenshot_preview = QLabel("截图预览")
        self.screenshot_preview.setObjectName("screenshotPreview")
        self.screenshot_preview.setAlignment(Qt.AlignCenter)
        self.screenshot_preview.setMinimumHeight(260)
        layout.addWidget(self.screenshot_preview)

        self.screenshot_list = QListWidget()
        self.screenshot_list.setMaximumHeight(130)
        layout.addWidget(self.screenshot_list)
        layout.addStretch(1)
        return panel

    def _build_chart_panel(self) -> QWidget:
        panel = QWidget()
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(8)

        self.fps_plot = pg.PlotWidget(title="FPS / Jank / BigJank")
        self.memory_plot = pg.PlotWidget(title="Memory")
        self.screenshot_plot = pg.PlotWidget(title="Screenshots")
        for plot in (self.fps_plot, self.memory_plot, self.screenshot_plot):
            plot.showGrid(x=True, y=True, alpha=0.22)
            plot.setMouseEnabled(x=True, y=False)
            plot.setMenuEnabled(False)
            plot.getAxis("bottom").setLabel("时间", units="s")
        self.memory_plot.setXLink(self.fps_plot)
        self.screenshot_plot.setXLink(self.fps_plot)
        self.fps_plot.getAxis("left").setLabel("FPS / 次")
        self.memory_plot.getAxis("left").setLabel("MB")
        self.screenshot_plot.getAxis("left").setTicks([[(1, "截图")]])
        self.screenshot_plot.setYRange(0, 2, padding=0)

        self.fps_curve = self.fps_plot.plot(pen=pg.mkPen("#ff6b8f", width=2), name="FPS")
        self.jank_curve = self.fps_plot.plot(pen=pg.mkPen("#4fb7ff", width=1.5), name="Jank")
        self.big_jank_curve = self.fps_plot.plot(pen=pg.mkPen("#9ad46f", width=1.5), name="BigJank")
        self.memory_curve = self.memory_plot.plot(pen=pg.mkPen("#ffd166", width=2), name="Memory")
        self.screenshot_scatter = pg.ScatterPlotItem(size=10, brush=pg.mkBrush("#4fb7ff"), pen=pg.mkPen("#d6efff"))
        self.screenshot_plot.addItem(self.screenshot_scatter)

        self.cursor_lines = [
            pg.InfiniteLine(angle=90, movable=False, pen=pg.mkPen("#ffffff", width=1, style=Qt.DashLine)),
            pg.InfiniteLine(angle=90, movable=False, pen=pg.mkPen("#ffffff", width=1, style=Qt.DashLine)),
            pg.InfiniteLine(angle=90, movable=False, pen=pg.mkPen("#ffffff", width=1, style=Qt.DashLine)),
        ]
        for plot, line in zip((self.fps_plot, self.memory_plot, self.screenshot_plot), self.cursor_lines):
            plot.addItem(line)
            line.hide()
            plot.scene().sigMouseClicked.connect(self._on_plot_clicked)

        layout.addWidget(self.fps_plot, 4)
        layout.addWidget(self.memory_plot, 3)
        layout.addWidget(self.screenshot_plot, 2)
        return panel

    def _connect_signals(self) -> None:
        self.lookup.devices_loaded.connect(self._on_devices_loaded)
        self.lookup.apps_loaded.connect(self._on_apps_loaded)
        self.lookup.processes_loaded.connect(self._on_processes_loaded)
        self.lookup.failed.connect(self._show_error)

        self.session.sample_ready.connect(self._on_sample)
        self.session.screenshot_ready.connect(self._on_screenshot)
        self.session.message.connect(self._set_status)
        self.session.error.connect(self._show_error)
        self.session.running_changed.connect(self._on_running_changed)

        self.refresh_devices_btn.clicked.connect(self.refresh_devices)
        self.refresh_apps_btn.clicked.connect(self.refresh_apps)
        self.refresh_processes_btn.clicked.connect(self.refresh_processes)
        self.device_combo.currentIndexChanged.connect(self._on_device_changed)
        self.app_combo.currentIndexChanged.connect(self._on_app_changed)
        self.process_combo.currentIndexChanged.connect(self._on_process_changed)
        self.app_search.textChanged.connect(self._render_apps)
        self.process_search.textChanged.connect(self._render_processes)
        self.screenshot_check.stateChanged.connect(self._sync_capture_options)
        self.screenshot_interval.valueChanged.connect(self._sync_capture_options)
        self.start_btn.clicked.connect(self.start_capture)
        self.stop_btn.clicked.connect(self.session.stop)
        self.clear_btn.clicked.connect(self.clear_samples)
        self.export_btn.clicked.connect(self.export_csv)
        self.follow_btn.clicked.connect(self.follow_latest_sample)
        self.screenshot_list.currentRowChanged.connect(self._on_screenshot_row)

    def refresh_devices(self) -> None:
        self.refresh_devices_btn.setEnabled(False)
        self._set_status("正在刷新设备...")
        self.lookup.refresh_devices()

    def refresh_apps(self) -> None:
        self.refresh_apps_btn.setEnabled(False)
        self.lookup.refresh_apps(self._selected_udid())

    def refresh_processes(self) -> None:
        self.refresh_processes_btn.setEnabled(False)
        self.lookup.refresh_processes(self._selected_udid())

    def start_capture(self) -> None:
        config = CaptureConfig(
            udid=self._selected_udid(),
            bundle_id=self.bundle_input.text().strip() or WECHAT_BUNDLE,
            target_pid=self._selected_process_pid(),
            target_name=self._selected_process_name(),
            capture_screenshots=self.screenshot_check.isChecked(),
            screenshot_interval_sec=float(self.screenshot_interval.value()),
        )
        self.clear_samples()
        self.session.start(config)

    def clear_samples(self) -> None:
        self.samples.clear()
        self.screenshots.clear()
        self.screenshot_list.clear()
        self.selected_time = None
        self.follow_latest = True
        self.dirty = True
        self._update_preview(None)

    def follow_latest_sample(self) -> None:
        self.follow_latest = True
        if self.samples:
            self.selected_time = self.samples[-1].elapsed_sec
        self.dirty = True

    def export_csv(self) -> None:
        if not self.samples:
            self._show_error("当前没有可导出的采样数据。")
            return
        path, _ = QFileDialog.getSaveFileName(self, "导出 CSV", "ios-performance-native.csv", "CSV (*.csv)")
        if not path:
            return
        with open(path, "w", encoding="utf-8-sig", newline="") as handle:
            writer = csv.DictWriter(
                handle,
                fieldnames=[
                    "timestamp",
                    "elapsed_sec",
                    "fps",
                    "jank",
                    "big_jank",
                    "memory_mb",
                    "screenshot_path",
                    "source",
                    "note",
                ],
            )
            writer.writeheader()
            for sample in self.samples:
                screenshot = self._nearest_screenshot(sample.elapsed_sec, max_distance=1.6)
                writer.writerow(
                    {
                        "timestamp": sample.timestamp,
                        "elapsed_sec": round(sample.elapsed_sec, 2),
                        "fps": round(sample.fps, 2) if sample.fps is not None else "",
                        "jank": sample.jank if sample.jank is not None else "",
                        "big_jank": sample.big_jank if sample.big_jank is not None else "",
                        "memory_mb": round(sample.memory_mb, 2) if sample.memory_mb is not None else "",
                        "screenshot_path": str(screenshot.path) if screenshot else "",
                        "source": sample.source,
                        "note": sample.note,
                    }
                )
        self._set_status(f"已导出：{path}")

    def _on_devices_loaded(self, devices: object) -> None:
        self.refresh_devices_btn.setEnabled(True)
        self.devices = list(devices) if isinstance(devices, list) else []
        current = self.device_combo.currentData()
        self.device_combo.blockSignals(True)
        self.device_combo.clear()
        for device in self.devices:
            self.device_combo.addItem(format_device(device), device.get("udid", ""))
        if self.devices:
            index = find_combo_data(self.device_combo, current)
            if index < 0:
                index = 0
            self.device_combo.setCurrentIndex(index)
        self.device_combo.blockSignals(False)
        self.refresh_apps()
        self.refresh_processes()
        self._set_status(f"检测到 {len(self.devices)} 台设备。")

    def _on_apps_loaded(self, apps: object) -> None:
        self.refresh_apps_btn.setEnabled(True)
        self.apps = ensure_wechat_app(list(apps) if isinstance(apps, list) else [])
        self._render_apps()

    def _on_processes_loaded(self, processes: object) -> None:
        self.refresh_processes_btn.setEnabled(True)
        self.processes = list(processes) if isinstance(processes, list) else []
        self._render_processes()

    def _render_apps(self) -> None:
        current = self.app_combo.currentData() or self.bundle_input.text().strip()
        filtered = filter_items(self.apps, self.app_search.text(), ["name", "bundle_id", "version", "reason"])
        self.app_combo.blockSignals(True)
        self.app_combo.clear()
        for app in filtered:
            self.app_combo.addItem(format_app(app), app.get("bundle_id", ""))
        index = find_combo_data(self.app_combo, current)
        if index < 0:
            index = find_combo_data(self.app_combo, WECHAT_BUNDLE)
        if index < 0 and self.app_combo.count():
            index = 0
        if index >= 0:
            self.app_combo.setCurrentIndex(index)
        self.app_combo.blockSignals(False)
        self._on_app_changed()

    def _render_processes(self) -> None:
        current = self.process_combo.currentData()
        filtered = filter_items(self.processes, self.process_search.text(), ["pid", "name", "bundle_id", "display_name", "reason"])
        self.process_combo.blockSignals(True)
        self.process_combo.clear()
        for process in filtered:
            self.process_combo.addItem(format_process(process), process.get("pid"))
        index = find_combo_data(self.process_combo, current)
        if index < 0:
            index = choose_process_index(self.process_combo, filtered, self.bundle_input.text().strip())
        if index >= 0:
            self.process_combo.setCurrentIndex(index)
        self.process_combo.blockSignals(False)
        self._on_process_changed()

    def _on_device_changed(self) -> None:
        self.refresh_apps()
        self.refresh_processes()

    def _on_app_changed(self) -> None:
        bundle = self.app_combo.currentData()
        if bundle:
            self.bundle_input.setText(str(bundle))

    def _on_process_changed(self) -> None:
        process = self._selected_process()
        if not process:
            self.process_hint.setText("")
            return
        if process.get("bundle_id"):
            self.bundle_input.setText(str(process["bundle_id"]))
        parts = [f"当前进程：pid {process.get('pid')} · {process.get('name') or process.get('display_name') or 'unknown'}"]
        if process.get("bundle_id"):
            parts.append(f"Bundle {process['bundle_id']}")
        if process.get("reason"):
            parts.append(str(process["reason"]))
        self.process_hint.setText(" · ".join(parts))

    def _sync_capture_options(self) -> None:
        self.session.update_capture_options(self.screenshot_check.isChecked(), float(self.screenshot_interval.value()))

    def _on_sample(self, sample: object) -> None:
        if isinstance(sample, NativeSample):
            self.samples.append(sample)
            if self.follow_latest:
                self.selected_time = sample.elapsed_sec
            self.dirty = True

    def _on_screenshot(self, screenshot: object) -> None:
        if not isinstance(screenshot, NativeScreenshot):
            return
        self.screenshots.append(screenshot)
        item = QListWidgetItem(f"{format_elapsed(screenshot.elapsed_sec)} · {screenshot.path.name}")
        item.setData(Qt.UserRole, screenshot)
        self.screenshot_list.addItem(item)
        if self.follow_latest:
            self._update_preview(screenshot)
        self.dirty = True

    def _on_screenshot_row(self, row: int) -> None:
        item = self.screenshot_list.item(row)
        if not item:
            return
        screenshot = item.data(Qt.UserRole)
        if isinstance(screenshot, NativeScreenshot):
            self.select_time(screenshot.elapsed_sec)

    def _on_running_changed(self, running: bool) -> None:
        self.start_btn.setEnabled(not running)
        self.stop_btn.setEnabled(running)
        self.run_badge.setText("采集中" if running else "未采集")
        self.run_badge.setProperty("running", running)
        self.run_badge.style().unpolish(self.run_badge)
        self.run_badge.style().polish(self.run_badge)

    def _on_plot_clicked(self, event: object) -> None:
        scene_pos = event.scenePos()
        for plot in (self.fps_plot, self.memory_plot, self.screenshot_plot):
            if plot.sceneBoundingRect().contains(scene_pos):
                mouse_point = plot.plotItem.vb.mapSceneToView(scene_pos)
                self.select_time(float(mouse_point.x()))
                break

    def select_time(self, elapsed_sec: float) -> None:
        if not self.samples:
            return
        nearest = nearest_sample(self.samples, elapsed_sec)
        if not nearest:
            return
        self.selected_time = nearest.elapsed_sec
        self.follow_latest = False
        self.dirty = True

    def _render_if_dirty(self) -> None:
        if not self.dirty:
            return
        self.dirty = False
        self._update_plots()
        self._update_metrics()
        self._update_cursors()
        if self.follow_latest and self.samples:
            self._update_preview(self._nearest_screenshot(self.samples[-1].elapsed_sec, max_distance=None))
        elif self.selected_time is not None:
            self._update_preview(self._nearest_screenshot(self.selected_time, max_distance=None))

    def _update_plots(self) -> None:
        if not self.samples:
            self.fps_curve.setData([], [])
            self.jank_curve.setData([], [])
            self.big_jank_curve.setData([], [])
            self.memory_curve.setData([], [])
            self.screenshot_scatter.setData([], [])
            return

        x = np.array([sample.elapsed_sec for sample in self.samples], dtype=float)
        fps = np.array([sample.fps if sample.fps is not None else np.nan for sample in self.samples], dtype=float)
        jank = np.array([sample.jank if sample.jank is not None else np.nan for sample in self.samples], dtype=float)
        big_jank = np.array([sample.big_jank if sample.big_jank is not None else np.nan for sample in self.samples], dtype=float)
        memory = np.array([sample.memory_mb if sample.memory_mb is not None else np.nan for sample in self.samples], dtype=float)
        plot_x, indices = decimated_indices(x, MAX_PLOT_POINTS)

        self.fps_curve.setData(plot_x, fps[indices], connect="finite")
        self.jank_curve.setData(plot_x, jank[indices], connect="finite")
        self.big_jank_curve.setData(plot_x, big_jank[indices], connect="finite")
        self.memory_curve.setData(plot_x, memory[indices], connect="finite")
        finite_fps = fps[np.isfinite(fps)]
        if finite_fps.size:
            self.fps_plot.setYRange(0, max(60.0, float(np.max(finite_fps)) + 5), padding=0.04)
        finite_memory = memory[np.isfinite(memory)]
        if finite_memory.size:
            self.memory_plot.setYRange(
                max(0.0, float(np.min(finite_memory)) - 30),
                float(np.max(finite_memory)) + 50,
                padding=0.04,
            )

        shot_x = [shot.elapsed_sec for shot in self.screenshots]
        self.screenshot_scatter.setData(shot_x, [1] * len(shot_x))

        if self.follow_latest:
            latest = max(1.0, self.samples[-1].elapsed_sec)
            self.fps_plot.setXRange(0, max(60.0, latest), padding=0)

    def _update_metrics(self) -> None:
        sample = None
        if self.selected_time is not None:
            sample = nearest_sample(self.samples, self.selected_time)
        if sample is None and self.samples:
            sample = self.samples[-1]
        if sample is None:
            self.fps_value.setText("--")
            self.jank_value.setText("--")
            self.big_jank_value.setText("--")
            self.memory_value.setText("--")
            self.duration_value.setText("0s")
            return
        self.fps_value.setText(f"{sample.fps:.1f}" if sample.fps is not None else "--")
        self.jank_value.setText(str(sample.jank) if sample.jank is not None else "--")
        self.big_jank_value.setText(str(sample.big_jank) if sample.big_jank is not None else "--")
        self.memory_value.setText(f"{sample.memory_mb:.0f} MB" if sample.memory_mb is not None else "--")
        self.duration_value.setText(format_elapsed(sample.elapsed_sec))

    def _update_cursors(self) -> None:
        if self.selected_time is None:
            for line in self.cursor_lines:
                line.hide()
            return
        for line in self.cursor_lines:
            line.setValue(self.selected_time)
            line.show()

    def _update_preview(self, screenshot: NativeScreenshot | None) -> None:
        if screenshot is None or not screenshot.path.exists():
            self.screenshot_preview.setText("等待截图")
            self.screenshot_preview.setPixmap(QPixmap())
            return
        pixmap = QPixmap(str(screenshot.path))
        if pixmap.isNull():
            self.screenshot_preview.setText("截图无法读取")
            return
        scaled = pixmap.scaled(self.screenshot_preview.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation)
        self.screenshot_preview.setPixmap(scaled)

    def _nearest_screenshot(self, elapsed_sec: float, max_distance: float | None) -> NativeScreenshot | None:
        if not self.screenshots:
            return None
        nearest = min(self.screenshots, key=lambda shot: abs(shot.elapsed_sec - elapsed_sec))
        if max_distance is not None and abs(nearest.elapsed_sec - elapsed_sec) > max_distance:
            return None
        return nearest

    def _selected_udid(self) -> str:
        return str(self.device_combo.currentData() or "")

    def _selected_process(self) -> dict[str, Any] | None:
        pid = self.process_combo.currentData()
        for process in self.processes:
            if process.get("pid") == pid:
                return process
        return None

    def _selected_process_pid(self) -> int | None:
        process = self._selected_process()
        if not process:
            return None
        try:
            return int(process.get("pid"))
        except (TypeError, ValueError):
            return None

    def _selected_process_name(self) -> str:
        process = self._selected_process()
        if not process:
            return ""
        return str(process.get("name") or process.get("display_name") or "")

    def _set_status(self, message: str) -> None:
        self.status_label.setText(message)

    def _show_error(self, message: str, modal: bool = True) -> None:
        self._set_status(message)
        if modal:
            QMessageBox.warning(self, "提示", message)

    def resizeEvent(self, event: object) -> None:
        super().resizeEvent(event)
        if self.selected_time is not None:
            self._update_preview(self._nearest_screenshot(self.selected_time, max_distance=None))

    def closeEvent(self, event: object) -> None:
        self.session.stop()
        super().closeEvent(event)

    def _apply_styles(self) -> None:
        self.setStyleSheet(
            """
            QMainWindow, QWidget { background: #10131a; color: #e8ecf3; font-family: "Microsoft YaHei", "Segoe UI"; }
            #sidePanel { background: #171b25; border: 1px solid #2a3140; border-radius: 6px; }
            QLabel#statusLabel { color: #cbd3df; font-size: 14px; }
            QLabel#badge { background: #2a3140; border-radius: 5px; padding: 6px 10px; color: #cbd3df; }
            QLabel#badge[running="true"] { background: #0c6b55; color: #ffffff; }
            QLabel#hint { color: #99a5b5; line-height: 1.3; }
            QLabel#metricValue { color: #ffffff; font-size: 22px; font-weight: 600; }
            QLabel#screenshotPreview { background: #0b0e14; border: 1px solid #2a3140; border-radius: 6px; color: #7f8b9d; }
            QLineEdit, QComboBox, QSpinBox, QListWidget {
                background: #0f131b; border: 1px solid #30394a; border-radius: 4px; padding: 5px; color: #e8ecf3;
            }
            QPushButton {
                background: #263246; border: 1px solid #3a465b; border-radius: 4px; padding: 7px 10px; color: #f2f5fa;
            }
            QPushButton:hover { background: #31415c; }
            QPushButton:disabled { color: #707b8c; background: #1b202b; }
            QCheckBox { spacing: 8px; }
            """
        )


def ensure_wechat_app(apps: list[dict[str, Any]]) -> list[dict[str, Any]]:
    if any(app.get("bundle_id") == WECHAT_BUNDLE for app in apps):
        return apps
    return [
        {
            "bundle_id": WECHAT_BUNDLE,
            "name": "微信",
            "version": "",
            "recommended": True,
            "reason": "微信小游戏宿主应用",
        },
        *apps,
    ]


def filter_items(items: list[dict[str, Any]], keyword: str, fields: list[str]) -> list[dict[str, Any]]:
    terms = [term.lower() for term in keyword.strip().split() if term.strip()]
    if not terms:
        return items
    result = []
    for item in items:
        haystack = " ".join(str(item.get(field, "")) for field in fields).lower()
        if all(term in haystack for term in terms):
            result.append(item)
    return result


def format_device(device: dict[str, Any]) -> str:
    name = device.get("market_name") or device.get("name") or "iOS Device"
    version = f"iOS {device.get('product_version')}" if device.get("product_version") else "iOS"
    suffix = str(device.get("udid") or "")[-6:]
    mark = "推荐 · " if device.get("recommended") else ""
    return f"{mark}{name} · {version} · {device.get('conn_type') or 'usb'} · {suffix}"


def format_app(app: dict[str, Any]) -> str:
    name = app.get("name") or app.get("bundle_id") or "unknown"
    version = f" · {app.get('version')}" if app.get("version") else ""
    mark = "推荐 · " if app.get("recommended") else ""
    return f"{mark}{name} · {app.get('bundle_id')}{version}"


def format_process(process: dict[str, Any]) -> str:
    name = process.get("display_name") or process.get("name") or "unknown"
    bundle = process.get("bundle_id") or process.get("name") or ""
    mark = "推荐" if process.get("recommended") else "进程"
    return f"{mark} · pid {process.get('pid')} · {name} · {bundle}"


def find_combo_data(combo: QComboBox, value: object) -> int:
    if value in {None, ""}:
        return -1
    for index in range(combo.count()):
        if combo.itemData(index) == value:
            return index
    return -1


def choose_process_index(combo: QComboBox, processes: list[dict[str, Any]], bundle_id: str) -> int:
    priorities = [
        lambda item: item.get("bundle_id") == bundle_id and bundle_id,
        lambda item: item.get("bundle_id") == WECHAT_BUNDLE,
        lambda item: item.get("name") == "WeChat",
        lambda item: item.get("name") == "com.apple.WebKit.WebContent",
    ]
    for predicate in priorities:
        for index, process in enumerate(processes):
            if predicate(process):
                return index
    return 0 if combo.count() else -1


def decimated_indices(x: np.ndarray, max_points: int) -> tuple[np.ndarray, np.ndarray]:
    count = len(x)
    if count <= max_points:
        indices = np.arange(count)
        return x, indices
    step = max(1, math.ceil(count / max_points))
    indices = np.arange(0, count, step)
    if indices[-1] != count - 1:
        indices = np.append(indices, count - 1)
    return x[indices], indices


def nearest_sample(samples: list[NativeSample], elapsed_sec: float) -> NativeSample | None:
    if not samples:
        return None
    return min(samples, key=lambda sample: abs(sample.elapsed_sec - elapsed_sec))


def format_elapsed(seconds: float) -> str:
    safe = max(0.0, float(seconds or 0.0))
    minutes = int(safe // 60)
    secs = int(safe % 60)
    if minutes:
        return f"{minutes}:{secs:02d}"
    return f"{safe:.1f}s"
