from __future__ import annotations

import argparse
import sys
import unittest
from dataclasses import replace
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "csharp_perf_monitor" / "tools"))
import harmony_perf_runner as runner


def stat(start: int = 100, ticks: int = 30) -> str:
    fields = ["S"] + ["0"] * 19
    fields[11], fields[12], fields[19] = str(ticks), "0", str(start)
    return "42 (game (main)) " + " ".join(fields)


def snapshot(start: int = 100, ticks: int = 30, cpu: str = "cpu0 100 0 0 100\ncpu1 100 0 0 100", pss: str = "Pss: 2048 kB", name: str = "com.example.game") -> str:
    return (f"__MOTUPERF_STAT__\n{stat(start, ticks)}\n__MOTUPERF_NAME__\n{name}\x00--arg\n"
            f"__MOTUPERF_CPU__\n{cpu}\n__MOTUPERF_PSS__\n{pss}\n__MOTUPERF_STATUS__\nVmRSS: 4096 kB\n")


def options(**overrides) -> argparse.Namespace:
    return argparse.Namespace(**({"pid": 42, "target_name": "com.example.game", "target_start_time_ticks": 100,
                                 "interval": 1, "no_cpu": False, "no_memory": False, "no_fps": False,
                                 "no_temperature": True, "no_thermal_state": False} | overrides))


class HarmonyRunnerTests(unittest.TestCase):
    def test_cpu_preserves_multicore_raw_and_computes_normalized_and_device_cores(self):
        before = runner.parse_snapshot(snapshot(ticks=30), 42)
        after = runner.parse_snapshot(snapshot(ticks=180, cpu="cpu0 180 0 0 120\ncpu1 170 0 0 130"), 42)
        result = runner.cpu_payload(before, after)
        self.assertEqual(result["value"], 150)
        self.assertEqual(result["normalized_value"], 75)
        self.assertEqual(result["core_values"], [80, 70])
        self.assertEqual(result["core_scope"], "device")
        self.assertEqual(before.memory_mb, 2)
        self.assertEqual(before.memory_source, "hdc-proc-smaps-rollup")

    def test_pid_reuse_counter_reset_and_hotplug_require_new_baseline(self):
        before = runner.parse_snapshot(snapshot(), 42)
        for after in (replace(before, start_ticks=101), replace(before, process_ticks=1),
                      replace(before, cores=before.cores[:1]), before):
            with self.subTest(after=after):
                self.assertIsNone(runner.cpu_payload(before, after))

    def test_unreadable_cpu_does_not_discard_identity_or_memory(self):
        result = runner.parse_snapshot(snapshot(cpu="cpu0 permission denied", pss="Permission denied"), 42)
        self.assertEqual(result.start_ticks, 100)
        self.assertEqual(result.cores, ())
        self.assertEqual(result.memory_mb, 4)
        self.assertEqual(result.memory_metric, "rss")
        self.assertIsNone(runner.parse_snapshot(snapshot().replace(stat(), "Permission denied"), 42))

    def test_empty_cmdline_uses_proc_stat_comm_for_identity(self):
        result = runner.parse_snapshot(snapshot().replace("com.example.game\x00--arg", ""), 42)
        self.assertEqual(result.name, "game (main)")
        self.assertEqual(result.start_ticks, 100)

    def test_system_process_identity_matches_proc_stat_comm(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(cpu="", pss="", name="servicemanager"), stat()]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(target_name="servicemanager", no_cpu=True, no_memory=True,
                                       no_fps=True, no_thermal_state=True), hdc)
        self.assertIn(("target", {"pid": 42, "confirmed": True, "platform": "harmony"}), events)

    def test_hidumper_uses_pid_row_not_device_total_and_requires_known_headers(self):
        text = "Total: 92.00%; User Space: 90.00%\nPID Total Usage User Space Kernel Space\n42 125.00% 120.00% 5.00%\n"
        self.assertEqual(runner.parse_hidumper_cpu(text, 42), 125)
        self.assertIsNone(runner.parse_hidumper_cpu(text, 43))
        self.assertIsNone(runner.parse_hidumper_cpu("42 100.0% other data", 42))
        memory = "Pss Shared Shared Private Private Swap\nTotal Clean Dirty Clean Dirty Total\n( kB ) ( kB )\nTotal 92998 99204 23452 56504 2176 27188\n"
        self.assertEqual(runner.parse_hidumper_pss_mb(memory), 92998 / 1024)
        self.assertIsNone(runner.parse_hidumper_pss_mb("Total 92998 99204\n"))

    def test_identity_recheck_after_hidumper_discards_replacement_process_metrics(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(cpu="", pss=""),
                                "PID Total Usage User Space\n42 125.00% 120.00%\n",
                                "Pss Shared Shared Private\n(kB)\nTotal 8192 0 0 0\n", stat(start=101)]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))):
            self.assertEqual(runner.collect(options(), hdc), 2)
        self.assertFalse(any(kind in ("cpu", "memory", "fps", "temperature") for kind, _ in events))
        self.assertEqual(events[-1][1]["code"], "harmony_target_changed")
        self.assertEqual(hdc.shell.call_args.args[0], "cat /proc/42/stat")

    def test_hidumper_provenance_and_unsupported_metrics_remain_missing(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(cpu="", pss=""),
                                "PID Total Usage User Space\n42 125.00% 120.00%\n",
                                "Pss Shared Shared Private\n(kB)\nTotal 8192 0 0 0\n", stat()]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(), hdc)
        metrics = dict(events)
        self.assertEqual(metrics["memory"]["source"], "hdc-hidumper-mem")
        self.assertEqual(metrics["memory"]["value"], 8)
        self.assertEqual(metrics["cpu"]["value"], 125)
        self.assertNotIn("normalized_value", metrics["cpu"])
        self.assertNotIn("fps", metrics)
        self.assertNotIn("thermal_state", metrics)

    def test_transport_gaps_reset_cpu_baseline_and_end_after_three_failures(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(), stat(), runner.HdcFailure("disconnected"),
                                snapshot(ticks=180), stat(ticks=180),
                                runner.HdcFailure("disconnected"), runner.HdcFailure("disconnected"), runner.HdcFailure("disconnected")]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep"):
            self.assertEqual(runner.collect(options(), hdc), 2)
        self.assertFalse(any(kind == "cpu" for kind, _ in events))
        self.assertEqual(sum(kind == "memory" for kind, _ in events), 2)
        self.assertEqual(events[-1][1]["code"], "harmony_target_unavailable")

    def test_disabled_metrics_only_probe_identity(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(), stat()]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(no_cpu=True, no_memory=True, no_fps=True, no_thermal_state=True), hdc)
        self.assertEqual([kind for kind, _ in events], ["target"])
        self.assertNotIn("smaps", hdc.shell.call_args_list[0].args[0])

    def test_hdc_failure_marker_overrides_zero_exit_code(self):
        hdc = runner.Hdc("C:/SDK/hdc.exe", "serial1")
        result = runner.subprocess.CompletedProcess([], 0, "[Fail]Device not founded or connected.", "")
        with patch.object(runner.subprocess, "run", return_value=result) as run:
            with self.assertRaises(runner.HdcFailure):
                hdc.shell("cat /proc/42/stat")
        self.assertEqual(run.call_args.args[0][:4], ["C:/SDK/hdc.exe", "-t", "serial1", "shell"])

    def test_hdc_failure_marker_in_stderr_overrides_zero_exit_code(self):
        hdc = runner.Hdc("C:/SDK/hdc.exe", "serial1")
        result = runner.subprocess.CompletedProcess([], 0, "", "[Fail]Device not founded or connected.")
        with patch.object(runner.subprocess, "run", return_value=result):
            with self.assertRaises(runner.HdcFailure) as failure:
                hdc.shell("cat /proc/42/stat")
        self.assertIn("Device not founded", str(failure.exception))

    def test_sensor_temperature_has_explicit_millidegree_units_and_names(self):
        self.assertEqual(runner.parse_temperatures("thermal_zone0|soc|42500\nthermal_zone1|battery|33000\n"),
                         {"soc (thermal_zone0)": 42.5, "battery (thermal_zone1)": 33})
        self.assertEqual(runner.parse_temperatures("thermal_zone0|soc|Permission denied\nthermal_zone1|soc|999999\n"), {})

    def test_temperature_command_keeps_each_sensor_on_its_own_line(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(), stat(),
                                 "thermal_zone0|soc|42500\nthermal_zone1|battery|33000\n"]
        with patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(no_cpu=True, no_memory=True, no_fps=True, no_thermal_state=True,
                                       no_temperature=False), hdc)
        temperature_command = hdc.shell.call_args_list[2].args[0]
        self.assertIn("printf '\\n'; done", temperature_command)
        self.assertEqual(runner.parse_temperatures("thermal_zone0|soc|42500thermal_zone1|battery|33000\n"), {})

    def test_full_fake_hdc_collection_emits_real_process_metrics_temperature_and_missing_frames(self):
        hdc = Mock()
        hdc.shell.side_effect = [
            snapshot(ticks=30),
            stat(ticks=30),
            "thermal_zone0|soc|42500\n",
            snapshot(ticks=180, cpu="cpu0 180 0 0 120\ncpu1 170 0 0 130"),
            stat(ticks=180),
        ]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "monotonic", return_value=0), \
             patch.object(runner.time, "sleep", side_effect=[None, KeyboardInterrupt]):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(no_temperature=False), hdc)

        metrics = {}
        for kind, data in events:
            metrics.setdefault(kind, []).append(data)
        self.assertEqual(metrics["target"][-1]["platform"], "harmony")
        self.assertEqual(metrics["cpu"][-1]["value"], 150)
        self.assertEqual(metrics["cpu"][-1]["normalized_value"], 75)
        self.assertEqual(metrics["memory"][-1]["metric"], "pss")
        self.assertEqual(metrics["temperature"][-1]["values"]["soc (thermal_zone0)"], 42.5)
        self.assertTrue(any(data["code"] == "harmony_frame_source_unavailable" for kind, data in events if kind == "status"))
        self.assertFalse(any(kind == "fps" for kind, _ in events))
        self.assertFalse(any(kind == "thermal_state" for kind, _ in events))
