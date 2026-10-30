from __future__ import annotations

import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "csharp_perf_monitor" / "tools"))

import android_frame_probe as probe  # noqa: E402


class AndroidFrameProbeTests(unittest.TestCase):
    def test_device_selection_requires_unambiguous_online_target(self) -> None:
        devices = "List of devices attached\nserial-a\tdevice product:test\nserial-b\tdevice product:test\n"

        self.assertEqual(probe.select_online_device(devices, "serial-b").serial, "serial-b")
        self.assertEqual(probe.select_online_device(devices, "").error_code, "android_device_selection_required")
        self.assertEqual(probe.select_online_device(devices, "serial-c").error_code, "android_requested_device_not_connected")
        self.assertEqual(probe.select_online_device("List of devices attached\n", "").error_code, "android_device_not_connected")

    def test_device_cpu_ticks_and_utilization_preserve_idle_semantics(self) -> None:
        before = probe.parse_device_cpu_ticks("cpu  100 0 50 850 0 0 0 0 0 0\n")
        after = probe.parse_device_cpu_ticks("cpu  200 0 100 1700 0 0 0 0 0 0\n")

        self.assertIsNotNone(before)
        self.assertIsNotNone(after)
        self.assertEqual(probe.cpu_utilization_percent(before, after), 15.0)

    def test_battery_parser_handles_capacity_current_temperature_and_counter(self) -> None:
        battery = probe.parse_battery_snapshot(
            "AC powered: false\n"
            "USB powered: true\n"
            "status: 2\n"
            "health: 2\n"
            "level: 87\n"
            "temperature: 315\n"
            "voltage: 4102\n"
            "current now: -120000\n"
            "charge counter: 3456789\n"
        )

        self.assertFalse(battery["ac_powered"])
        self.assertTrue(battery["usb_powered"])
        self.assertEqual(battery["level_percent"], 87)
        self.assertEqual(battery["temperature_c"], 31.5)
        self.assertEqual(battery["current_now_ua"], -120000)
        self.assertEqual(battery["charge_counter_uah"], 3456789)

    def test_frame_metrics_use_actual_present_timestamp_column(self) -> None:
        before = "16666667\n1000000000 1020000000 1010000000\n"
        after = (
            "16666667\n"
            "1000000000 1020000000 1010000000\n"
            "2000000000 1034000000 1030000000\n"
            "2000000000 1050000000 1040000000\n"
        )

        metrics = probe.frame_metrics_from_latency(before, after, target_verified=True)

        self.assertTrue(metrics["available"])
        self.assertEqual(metrics["frame_count"], 2)
        self.assertAlmostEqual(metrics["frame_time_ms"], 16.0)
        self.assertAlmostEqual(metrics["fps"], 66.6666666667)

    def test_collect_report_marks_trace_and_overhead_without_fabricating_frame_parse(self) -> None:
        def fake_shell(_executable, _serial, command, timeout=0):
            if command.startswith("getprop ro.build.version.sdk"):
                stdout = "35\n"
            elif command.startswith("getprop ro.product.cpu.abi"):
                stdout = "arm64-v8a\n"
            elif command.startswith("atrace -t"):
                stdout = "SurfaceFlinger-42 [001] ... frame-1234\n"
            elif "SurfaceFlinger --latency" in command:
                stdout = (
                    "16666667\n"
                    "1000000000 1020000000 1010000000\n"
                    "2000000000 1034000000 1030000000\n"
                )
            else:
                stdout = ""
            return type("Result", (), {"returncode": 0, "stdout": stdout, "stderr": ""})()

        cpu_samples = iter(
            [
                probe.DeviceCpuTicks(1_000, 800),
                probe.DeviceCpuTicks(1_200, 900),
                probe.DeviceCpuTicks(1_200, 900),
                probe.DeviceCpuTicks(1_600, 1_000),
            ]
        )
        battery_samples = iter(
            [
                {"level_percent": 90, "charge_counter_uah": 4_000_000},
                {"level_percent": 89, "charge_counter_uah": 3_980_000},
            ]
        )
        originals = (
            probe.shell,
            probe.trace_capabilities,
            probe.resolve_surface,
            probe.sample_cpu,
            probe.sample_battery,
            probe.time.sleep,
        )
        try:
            probe.shell = fake_shell  # type: ignore[assignment]
            probe.trace_capabilities = lambda *_: {"trace_permission": "granted", "gfx_category_available": True}  # type: ignore[assignment]
            probe.resolve_surface = lambda *_: {
                "selected_layer": "SurfaceView[com.example.game]#1",
                "target_verified": True,
                "owner_pid": 42,
                "owner_uid": 10123,
                "candidate_count": 1,
                "ownership_confidence": "verified",
            }  # type: ignore[assignment]
            probe.sample_cpu = lambda *_args: next(cpu_samples)  # type: ignore[assignment]
            probe.sample_battery = lambda *_args: next(battery_samples)  # type: ignore[assignment]
            probe.time.sleep = lambda _seconds: None  # type: ignore[assignment]

            report = probe.collect_report("adb", "", 42, "com.example.game", 1.0)
        finally:
            (
                probe.shell,
                probe.trace_capabilities,
                probe.resolve_surface,
                probe.sample_cpu,
                probe.sample_battery,
                probe.time.sleep,
            ) = originals

        self.assertEqual(report["android_sdk"], "35")
        self.assertEqual(report["abi"], "arm64-v8a")
        self.assertEqual(report["trace_capture"]["status"], "unavailable")
        self.assertFalse(report["is_device_side_trace_validated"])
        self.assertFalse(report["trace_capture"]["target_specific_frame_events_parsed"])
        self.assertEqual(report["frame_metrics_source"], "perfetto_actual_surface_frame_timeline")
        self.assertEqual(report["battery"]["level_delta_percent"], -1)
        self.assertEqual(report["battery"]["charge_counter_delta_uah"], -20_000)
        self.assertIsNotNone(report["cpu"]["delta_percentage_points"])


if __name__ == "__main__":
    unittest.main()
