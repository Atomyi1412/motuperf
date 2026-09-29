from __future__ import annotations

import argparse
import sys
import unittest
from dataclasses import replace
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "csharp_perf_monitor" / "tools"))
import harmony_perf_runner as runner


def stat(start: int = 100, ticks: int = 30, comm: str = "game (main)") -> str:
    fields = ["S"] + ["0"] * 19
    fields[11], fields[12], fields[19] = str(ticks), "0", str(start)
    return f"42 ({comm}) " + " ".join(fields)


def snapshot(start: int = 100, ticks: int = 30, cpu: str = "cpu0 100 0 0 100\ncpu1 100 0 0 100", pss: str = "Pss: 2048 kB", name: str = "com.example.game", comm: str = "game (main)", uid: str = "") -> str:
    status = ("Uid: " + uid + "\n") if uid else ""
    return (f"__MOTUPERF_STAT__\n{stat(start, ticks, comm)}\n__MOTUPERF_NAME__\n{name}\x00--arg\n"
            f"__MOTUPERF_CPU__\n{cpu}\n__MOTUPERF_PSS__\n{pss}\n__MOTUPERF_STATUS__\n"
            f"{status}VmRSS: 4096 kB\n")


def options(**overrides) -> argparse.Namespace:
    return argparse.Namespace(**({"pid": 42, "target_name": "com.example.game", "target_start_time_ticks": 100,
                                 "target_bundle_id": "com.example.game", "target_user_id": 100, "serial": "harmony-1",
                                 "target_name_is_comm": False,
                                 "interval": 1, "no_cpu": False, "no_memory": False, "no_fps": True,
                                 "no_temperature": True, "no_thermal_state": False} | overrides))


class HarmonyRunnerTests(unittest.TestCase):
    def test_provenance_preserves_explicit_app_index_and_defaults_unknown(self):
        self.assertEqual(runner.provenance(options())["app_index"], -1)
        self.assertEqual(runner.provenance(options(target_app_index=2))["app_index"], 2)

    def test_hdc_failure_classification_preserves_actionable_reason(self):
        cases = (
            ("[Fail]Device not founded or connected.", "harmony_device_disconnected"),
            ("permission denied while reading /proc", "harmony_hdc_permission_denied"),
            ("unknown command: hidumper", "harmony_hdc_command_unsupported"),
            ("unexpected transport error", "harmony_hdc_command_failed"),
        )
        for detail, code in cases:
            with self.subTest(detail=detail):
                actual, hint = runner.classify_hdc_failure(detail)
                self.assertEqual(actual, code)
                self.assertTrue(hint)

    def test_hdc_timeout_and_launch_failures_keep_distinct_codes(self):
        hdc = runner.Hdc("C:/SDK/hdc.exe", "serial1")
        with patch.object(runner.subprocess, "run", side_effect=runner.subprocess.TimeoutExpired("hdc", 1)):
            with self.assertRaises(runner.HdcFailure) as failure:
                hdc.shell("cat /proc/42/stat", timeout=1)
        self.assertEqual(failure.exception.code, "harmony_hdc_timeout")
        with patch.object(runner.subprocess, "run", side_effect=OSError("missing hdc")):
            with self.assertRaises(runner.HdcFailure) as failure:
                hdc.shell("cat /proc/42/stat")
        self.assertEqual(failure.exception.code, "harmony_hdc_launch_failed")

    def test_selected_comm_can_collect_later_full_cmdline_then_bind_exact_name(self):
        hdc = Mock()
        short = "com.example.gam"
        hdc.shell.side_effect = [
            snapshot(name="/system/bin/com.example.game:renderer", comm=short, uid="20010123"), stat(),
            snapshot(name="", comm=short, ticks=50, uid="20010123",
                     cpu="cpu0 110 0 0 110\ncpu1 110 0 0 110"), stat(ticks=50),
            snapshot(name="com.example.game:worker", comm=short, ticks=70, uid="20010123"),
        ]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep"):
            self.assertEqual(runner.collect(options(target_name=short, target_name_is_comm=True), hdc), 2)
        self.assertEqual(sum(kind == "memory" for kind, _ in events), 2)
        self.assertEqual([data["value"] for kind, data in events if kind == "cpu"], [100])
        self.assertEqual(events[-1][1]["code"], "harmony_target_changed")

    def test_comm_selection_never_guesses_instance_or_shortens_arbitrary_names(self):
        short = "com.example.gam"
        for marked, start, comm, uid in (
            (False, 100, short, "20010123"),
            (True, 0, short, "20010123"),
            (True, 101, short, "20010123"),
            (True, 100, "unrelated", "20010123"),
            (True, 100, short, "20210123"),
        ):
            with self.subTest(marked=marked, start=start, comm=comm, uid=uid):
                hdc = Mock()
                hdc.shell.side_effect = [snapshot(name="com.example.game:renderer", comm=comm, uid=uid)]
                events = []
                with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))):
                    self.assertEqual(runner.collect(options(target_name=short, target_name_is_comm=marked,
                                                           target_start_time_ticks=start), hdc), 2)
                self.assertFalse(any(kind in ("memory", "cpu", "fps") for kind, _ in events))

    def test_name_source_switch_does_not_discard_real_cpu_delta(self):
        first = runner.parse_snapshot(snapshot(name="/system/bin/com.example.game:renderer", comm="com.example.gam"), 42)
        second = runner.parse_snapshot(snapshot(name="", comm="com.example.gam", ticks=50,
                                               cpu="cpu0 110 0 0 110\ncpu1 110 0 0 110"), 42)
        payload = runner.cpu_payload(first, second)
        self.assertIsNotNone(payload)
        self.assertEqual(payload["value"], 100)
        self.assertIsNone(runner.cpu_payload(first, replace(second, start_ticks=101)))
        self.assertIsNone(runner.cpu_payload(first, replace(second, name="com.example.gax")))

    def test_comm_alias_limit_is_utf8_bytes(self):
        for name, expected in (("abc\u754c\u9762\u6e38\u620f", True), ("\u754c\u9762abcdefghijklm", False)):
            with self.subTest(name=name):
                current = runner.parse_snapshot(snapshot(name="", comm=name), 42)
                self.assertEqual(runner.target_name_matches(current, name + "worker", bound_start=100), expected)

    def test_comm_can_bind_start_before_full_name_is_readable(self):
        short = "com.example.gam"
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(name="", comm=short), stat(),
                                 snapshot(name="com.example.game:renderer", comm=short), stat()]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=[None, KeyboardInterrupt]):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(target_name=short, target_name_is_comm=True, target_start_time_ticks=0), hdc)
        self.assertEqual(sum(kind == "memory" for kind, _ in events), 2)
        self.assertFalse(any(kind == "fatal" for kind, _ in events))

    def test_bound_full_name_survives_failed_probe_without_accepting_sibling(self):
        short = "com.example.gam"
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(name="com.example.game:renderer", comm=short), stat(),
                                 runner.HdcFailure("temporary"),
                                 snapshot(name="com.example.game:worker", comm=short)]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep"):
            self.assertEqual(runner.collect(options(target_name=short, target_name_is_comm=True), hdc), 2)
        self.assertEqual(sum(kind == "memory" for kind, _ in events), 1)
        self.assertEqual(events[-1][1]["code"], "harmony_target_changed")

    def test_cli_preserves_explicit_comm_flag_and_defaults_to_full_name(self):
        for flag in ([], ["--target-name-is-comm"]):
            with self.subTest(flag=flag), patch.object(sys, "argv", ["runner", "--hdc", "hdc", "--serial", "test",
                     "--pid", "42", "--target-name", "com.example.gam", *flag]), \
                 patch.object(runner, "Hdc"), patch.object(runner, "collect", return_value=0) as collect:
                self.assertEqual(runner.main(), 0)
                self.assertEqual(collect.call_args.args[0].target_name_is_comm, bool(flag))

    def test_frame_collection_is_verified_after_poll_and_optional_failure_keeps_memory(self):
        node = (42 << 32) + 7
        times = [10**9 + i * 20_000_000 for i in range(52)]

        def dump(values):
            rows = [f"{t - 1000}:{t}" for t in reversed(values)]
            return " surface [Game]:\n" + "\n".join(rows + ["0:0"] * (384 - len(rows)))

        for reused, probe_failed in ((False, False), (True, False), (False, True)):
            with self.subTest(reused=reused, probe_failed=probe_failed):
                hdc = Mock()
                inventory = f" surface [Game] NodeId[{node}] LayerId[1]:"
                frame_result = runner.HdcFailure("denied") if probe_failed else dump(times)
                hdc.shell.side_effect = [
                    snapshot(uid="20010123"), inventory, dump(times[:1]), inventory, stat(),
                    snapshot(uid="20010123"), inventory,
                    frame_result,
                    *([] if probe_failed else [inventory]),
                    stat(start=101 if reused else 100),
                ]
                events = []
                with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
                     patch.object(runner.time, "sleep", side_effect=[None, KeyboardInterrupt]):
                    if reused:
                        self.assertEqual(runner.collect(options(no_fps=False), hdc), 2)
                    else:
                        with self.assertRaises(KeyboardInterrupt):
                            runner.collect(options(no_fps=False), hdc)
                fps = [data for kind, data in events if kind == "fps"]
                memory = [data for kind, data in events if kind == "memory"]
                self.assertEqual(len(memory), 1 if reused else 2)
                if reused or probe_failed:
                    self.assertEqual(fps, [])
                else:
                    self.assertEqual(len(fps), 1)
                    self.assertEqual(fps[0]["fps"], 50)
                    self.assertEqual(fps[0]["platform"], "harmony")
                    self.assertEqual(fps[0]["pid"], 42)
                    self.assertEqual(fps[0]["user_id"], 100)
                    self.assertEqual(fps[0]["frame_count"], 50)
                    self.assertEqual(fps[0]["frame_time_max_ms"], 20)
                    self.assertTrue(fps[0]["target_verified"])

    def test_absolute_executable_path_matches_selected_service_and_collects(self):
        hdc = Mock()
        hdc.shell.side_effect = [
            snapshot(name="/system/bin/servicemanager", uid="1000"), stat(),
            snapshot(name="/system/bin/servicemanager", uid="1000", ticks=50,
                     cpu="cpu0 110 0 0 110\ncpu1 110 0 0 110"), stat(ticks=50),
        ]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=[None, KeyboardInterrupt]):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(target_name="servicemanager", target_bundle_id="", target_user_id=-1), hdc)
        self.assertEqual(sum(kind == "memory" for kind, _ in events), 2)
        self.assertEqual(next(data["value"] for kind, data in events if kind == "cpu"), 100)
        self.assertFalse(any(kind == "fatal" for kind, _ in events))

    def test_executable_name_matching_keeps_suffixes_and_kernel_name_structure(self):
        for name, comm, expected, matches in (
            ("/system/bin/com.example.game:worker", "", "com.example.game:worker", True),
            ("/system/bin/com.example.game:worker", "", "com.example.game", False),
            ("/system/bin/servicemanager2", "", "servicemanager", False),
            ("", "kworker/0:1", "kworker/0:1", True),
            ("", "kworker/0:1", "0:1", False),
        ):
            with self.subTest(name=name, comm=comm, expected=expected):
                current = runner.parse_snapshot(snapshot(name=name, comm=comm), 42)
                self.assertEqual(runner.target_name_matches(current, expected), matches)

    def test_executable_basename_match_does_not_allow_reused_pid(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(name="/system/bin/servicemanager", start=101)]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))):
            self.assertEqual(runner.collect(options(target_name="servicemanager", target_user_id=-1), hdc), 2)
        self.assertEqual(events[-1][1]["code"], "harmony_target_changed")
        self.assertIn("重启", events[-1][1]["message"])
        self.assertFalse(any(kind in ("cpu", "memory", "temperature") for kind, _ in events))

    def test_official_native_uid_range_does_not_stop_the_selected_account(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(uid="20010123"), stat()]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(), hdc)
        self.assertEqual(runner.parse_snapshot(snapshot(uid="20010123"), 42).user_id, 100)
        self.assertTrue(any(kind == "memory" for kind, _ in events))
        self.assertFalse(any(kind == "fatal" for kind, _ in events))

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

    def test_empty_cmdline_and_comm_keep_verified_process_snapshot(self):
        result = runner.parse_snapshot(snapshot(name="", comm="", uid="20010123"), 42)
        self.assertIsNotNone(result)
        self.assertEqual(result.name, "")
        self.assertEqual(result.name_source, "unknown")
        self.assertEqual(result.start_ticks, 100)
        self.assertEqual(result.user_id, 100)

    def test_bundle_pid_target_collects_when_process_name_is_unreadable(self):
        hdc = Mock()
        hdc.shell.side_effect = [
            snapshot(name="", comm="", uid="20010123"), stat(comm=""),
            snapshot(name="", comm="", ticks=50, uid="20010123",
                     cpu="cpu0 110 0 0 110\ncpu1 110 0 0 110"), stat(ticks=50, comm=""),
        ]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=[None, KeyboardInterrupt]):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(target_name=""), hdc)
        self.assertEqual(sum(kind == "memory" for kind, _ in events), 2)
        self.assertEqual(sum(kind == "cpu" for kind, _ in events), 1)
        self.assertEqual(next(data["value"] for kind, data in events if kind == "cpu"), 100)
        self.assertTrue(any(kind == "target" and data["confirmed"] for kind, data in events))
        self.assertFalse(any(kind == "fatal" for kind, _ in events))

    def test_empty_selected_name_still_binds_pid_start_and_user_identity(self):
        for start, uid, final_start, expected_samples in (
            (100, "20010123", 100, 1),
            (101, "20010123", 101, 0),
            (100, "20210123", 100, 0),
            (100, "20010123", 101, 0),
        ):
            with self.subTest(start=start, uid=uid, final_start=final_start):
                hdc = Mock()
                hdc.shell.side_effect = [snapshot(name="", start=start, uid=uid), stat(start=final_start)]
                events = []
                with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
                     patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
                    if expected_samples:
                        with self.assertRaises(KeyboardInterrupt):
                            runner.collect(options(target_name=""), hdc)
                    else:
                        self.assertEqual(runner.collect(options(target_name=""), hdc), 2)
                self.assertEqual(sum(kind == "memory" for kind, _ in events), expected_samples)
                self.assertEqual(any(kind == "fatal" for kind, _ in events), not expected_samples)

    def test_bundle_evidence_does_not_require_matching_executable_name(self):
        for selected_name in ("", "game_renderer"):
            with self.subTest(selected_name=selected_name):
                hdc = Mock()
                hdc.shell.side_effect = [
                    snapshot(name="/system/bin/game_renderer", uid="20010123"), stat(),
                    snapshot(name="/system/bin/game_renderer", uid="20010123", ticks=50,
                             cpu="cpu0 110 0 0 110\ncpu1 110 0 0 110"), stat(ticks=50),
                    snapshot(name="/system/bin/other_renderer", uid="20010123"),
                ]
                events = []
                with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
                     patch.object(runner.time, "sleep"):
                    self.assertEqual(runner.collect(options(target_name=selected_name), hdc), 2)
                self.assertEqual(sum(kind == "memory" for kind, _ in events), 2)
                cpu = next(data for kind, data in events if kind == "cpu")
                self.assertEqual(cpu["value"], 100)
                self.assertEqual(cpu["bundle_id"], "com.example.game")
                self.assertEqual(events[-1][1]["code"], "harmony_target_changed")

    def test_proc_status_maps_harmony_profile_uid(self):
        owner = runner.parse_snapshot(snapshot(uid="12345"), 42)
        work = runner.parse_snapshot(snapshot(uid="20010123"), 42)
        system = runner.parse_snapshot(snapshot(uid="2000"), 42)
        self.assertEqual(owner.user_id, 0)
        self.assertEqual(work.user_id, 100)
        self.assertIsNone(system.user_id)

    def test_collection_stops_when_process_moves_to_another_harmony_user(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(uid="20010123"), stat(), snapshot(uid="12345")]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))):
            self.assertEqual(runner.collect(options(), hdc), 2)
        self.assertEqual(events[-1][1]["code"], "harmony_target_changed")
        self.assertFalse(any(kind == "cpu" for kind, _ in events))

    def test_system_process_identity_matches_proc_stat_comm(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(cpu="", pss="", name="servicemanager"), stat()]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(target_name="servicemanager", no_cpu=True, no_memory=True,
                                       no_fps=True, no_thermal_state=True), hdc)
        target = next(data for kind, data in events if kind == "target" and data["confirmed"])
        self.assertEqual(target["platform"], "harmony")
        self.assertEqual(target["device_serial"], "harmony-1")
        self.assertEqual(target["user_id"], 100)
        self.assertEqual(target["bundle_id"], "com.example.game")

    def test_long_comm_name_can_match_selected_truncated_process(self):
        current = runner.parse_snapshot(snapshot(name="", comm="com.example.foo"), 42)

        self.assertEqual(current.name_source, "comm")
        self.assertTrue(runner.target_name_matches(current, "com.example.foo:worker", bound_start=100))
        self.assertFalse(runner.target_name_matches(current, "com.example.foo:worker", bound_start=0))
        self.assertFalse(runner.target_name_matches(current, "com.example.foo:worker", bound_start=101))
        self.assertFalse(runner.target_name_matches(current, "com.example.fop:worker", bound_start=100))
        self.assertFalse(runner.target_name_matches(
            replace(current, name_source="cmdline"), "com.example.foo:worker", bound_start=100))

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

    def test_transient_transport_gap_emits_recovery_and_drops_cross_gap_cpu(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(), stat(), runner.HdcFailure("temporary", "harmony_hdc_timeout", "检查设备响应"),
                                  snapshot(ticks=180), stat(ticks=180)]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=[None, None, KeyboardInterrupt]):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(), hdc)
        recovery = next(data for kind, data in events if kind == "status" and data["code"] == "harmony_probe_recovered")
        self.assertEqual(recovery["recovered_after"], 1)
        self.assertFalse(any(kind == "cpu" for kind, _ in events))
        self.assertEqual(sum(kind == "memory" for kind, _ in events), 2)

    def test_mixed_failure_reasons_still_stop_on_third_attempt(self):
        hdc = Mock()
        reasons = ["harmony_hdc_timeout", "harmony_hdc_permission_denied", "harmony_device_disconnected"]
        hdc.shell.side_effect = [runner.HdcFailure("detail", code, "检查连接") for code in reasons]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep"):
            self.assertEqual(runner.collect(options(no_thermal_state=True), hdc), 2)
        status = [data for kind, data in events if kind == "status"]
        self.assertEqual([data["code"] for data in status], reasons)
        self.assertEqual([data["attempt"] for data in status], [1, 2, 3])
        self.assertTrue(all(data["max_attempts"] == 3 for data in status))
        self.assertEqual(events[-1][1]["code"], "harmony_target_unavailable")
        self.assertEqual(events[-1][1]["reason_code"], reasons[-1])
        self.assertEqual(events[-1][1]["attempts"], 3)
        self.assertEqual(hdc.shell.call_count, 3)
        self.assertFalse(any(kind in ("cpu", "memory", "fps", "temperature") for kind, _ in events))

    def test_unreadable_identity_is_not_classified_as_process_exit(self):
        for replies in ([""] * 3, [snapshot(), "", snapshot(), "", snapshot(), ""]):
            with self.subTest(final_check=len(replies) == 6):
                hdc = Mock()
                hdc.shell.side_effect = replies
                events = []
                with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
                     patch.object(runner.time, "sleep"):
                    self.assertEqual(runner.collect(options(), hdc), 2)
                self.assertEqual(events[-1][1]["reason_code"], "harmony_process_identity_unavailable")
                self.assertNotEqual(events[-1][1]["code"], "harmony_target_changed")
                self.assertFalse(any(kind in ("cpu", "memory", "fps", "temperature") for kind, _ in events))

    def test_recovery_requires_final_start_clock_and_never_accepts_changed_target(self):
        for tail in ([snapshot(start=101)], [snapshot(), stat(start=101)], [snapshot(), runner.HdcFailure("lost again")]):
            with self.subTest(tail=tail):
                hdc = Mock()
                hdc.shell.side_effect = [runner.HdcFailure("timeout"), *tail, runner.HdcFailure("lost")]
                events = []
                with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
                     patch.object(runner.time, "sleep"):
                    self.assertEqual(runner.collect(options(), hdc), 2)
                self.assertFalse(any(data.get("code") == "harmony_probe_recovered" for _, data in events))
                self.assertFalse(any(kind in ("cpu", "memory", "fps") for kind, _ in events))

    def test_transport_reason_survives_both_output_streams_and_detail_is_bounded(self):
        for stderr in (False, True):
            with self.subTest(stderr=stderr):
                detail = "[Fail]Device not founded or connected." + "x" * 300
                result = runner.subprocess.CompletedProcess([], 0, "" if stderr else detail, detail if stderr else "")
                with patch.object(runner.subprocess, "run", return_value=result):
                    with self.assertRaises(runner.HdcFailure) as failure:
                        runner.Hdc("hdc", "serial").shell("cat /proc/42/stat")
                self.assertEqual(failure.exception.code, "harmony_device_disconnected")
                self.assertLessEqual(len(str(failure.exception)), 240)
                self.assertIn("USB", failure.exception.hint)
        self.assertTrue(str(runner.HdcFailure("   ")).strip())

    def test_disabled_metrics_only_probe_identity(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(), stat()]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(no_cpu=True, no_memory=True, no_fps=True, no_thermal_state=True), hdc)
        self.assertEqual([kind for kind, _ in events], ["status", "target"])
        self.assertEqual(events[0][1]["code"], "harmony_user_identity_unavailable")
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

    def test_thermal_manager_temperature_parser_keeps_official_units(self):
        self.assertEqual(
            runner.parse_harmony_thermal_temperatures(
                "Type: soc\nTemperature: 42500\nType: battery\nTemperature: 33000\n"
            ),
            {"soc": 42.5, "battery": 33},
        )
        self.assertEqual(
            runner.parse_harmony_thermal_temperatures(
                "Type: soc\nTemperature: Permission denied\nType: shell\nTemperature: 999999\n"
            ),
            {},
        )

    def test_thermal_manager_temperature_fallback_emits_source(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(), stat(), "", "Type: soc\nTemperature: 42500\n"]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(no_temperature=False), hdc)
        temperature = next(data for kind, data in events if kind == "temperature")
        self.assertEqual(temperature["values"], {"soc (Thermal Manager)": 42.5})
        self.assertEqual(temperature["source"], "hdc-hidumper-3303-temperature")
        self.assertEqual(hdc.shell.call_args_list[3].args[0], "hidumper -s 3303 -a -t")

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
                runner.collect(options(no_temperature=False, no_thermal_state=False), hdc)

        metrics = {}
        for kind, data in events:
            metrics.setdefault(kind, []).append(data)
        self.assertEqual(metrics["target"][-1]["platform"], "harmony")
        self.assertEqual(metrics["cpu"][-1]["value"], 150)
        self.assertEqual(metrics["cpu"][-1]["normalized_value"], 75)
        self.assertEqual(metrics["memory"][-1]["metric"], "pss")
        self.assertEqual(metrics["temperature"][-1]["values"]["soc (thermal_zone0)"], 42.5)
        self.assertTrue(any(data["code"] == "harmony_thermal_state_unavailable" for kind, data in events if kind == "status"))
        self.assertFalse(any(kind == "fps" for kind, _ in events))
        self.assertFalse(any(kind == "thermal_state" for kind, _ in events))

    def test_metric_events_keep_harmony_target_provenance(self):
        hdc = Mock()
        hdc.shell.side_effect = [
            snapshot(ticks=30),
            stat(ticks=30),
            snapshot(ticks=180, cpu="cpu0 180 0 0 120\ncpu1 170 0 0 130"),
            stat(ticks=180),
        ]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=[None, KeyboardInterrupt]):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(), hdc)

        for kind in ("cpu", "memory"):
            payload = next(data for event_kind, data in events if event_kind == kind)
            self.assertEqual(payload["platform"], "harmony")
            self.assertEqual(payload["device_serial"], "harmony-1")
            self.assertEqual(payload["bundle_id"], "com.example.game")
            self.assertEqual(payload["user_id"], 100)
            self.assertEqual(payload["target_start_time_ticks"], 100)
