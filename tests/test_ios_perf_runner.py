from __future__ import annotations

import importlib.util
import subprocess
import sys
import threading
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RUNNER_PATH = ROOT / "csharp_perf_monitor" / "tools" / "pid_perf_runner.py"
sys.path.insert(0, str(RUNNER_PATH.parent))

spec = importlib.util.spec_from_file_location("pid_perf_runner", RUNNER_PATH)
runner = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(runner)


class IosPerfRunnerTests(unittest.TestCase):
    def test_application_rebind_requires_unique_bundle_bound_main_process(self) -> None:
        processes = [
            {"pid": 2584, "name": "WeChat", "bundleIdentifier": "com.tencent.xin", "isApplication": True},
            {"pid": 2585, "name": "com.apple.WebKit.WebContent", "bundleIdentifier": "com.tencent.xin", "isApplication": False},
        ]

        candidate = runner.find_application_rebind_candidate(
            processes, 5233, "WeChat", "com.tencent.xin"
        )

        self.assertIsNotNone(candidate)
        self.assertEqual(candidate["pid"], 2584)
        self.assertIsNone(
            runner.find_application_rebind_candidate(
                processes, 5233, "com.apple.WebKit.WebContent", "com.tencent.xin"
            )
        )

    def test_application_rebind_rejects_ambiguous_or_non_application_candidates(self) -> None:
        candidates = [
            {"pid": 2584, "name": "Game", "bundleIdentifier": "com.example.game", "isApplication": True},
            {"pid": 2586, "name": "Game", "bundleIdentifier": "com.example.game", "isApplication": True},
        ]

        self.assertIsNone(
            runner.find_application_rebind_candidate(
                candidates, 100, "Game", "com.example.game"
            )
        )
        self.assertIsNone(
            runner.find_application_rebind_candidate(
                [dict(candidates[0], isApplication=False)], 100, "Game", "com.example.game"
            )
        )
        self.assertIsNone(
            runner.find_application_rebind_candidate(
                [dict(candidates[0], pid=0)], 100, "Game", "com.example.game"
            )
        )
        self.assertIsNone(
            runner.find_application_rebind_candidate(
                [dict(candidates[0], pid="not-a-pid")], 100, "Game", "com.example.game"
            )
        )

    def test_modern_startup_does_not_preload_legacy_ios_device_runtime(self) -> None:
        script = (
            "import sys; "
            f"sys.path.insert(0, {str(RUNNER_PATH.parent)!r}); "
            "import pid_perf_runner as runner; "
            "print(runner.InstrumentsBase is None and runner.InstrumentsService is None)"
        )
        completed = subprocess.run(
            [sys.executable, "-c", script],
            cwd=ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=10,
            check=True,
        )

        self.assertEqual(completed.stdout.strip(), "True")

    def test_legacy_ordered_source_uses_on_glass_not_swap_program(self) -> None:
        self.assertEqual(runner.IOS_DISPLAY_FRAME_EVENT, (0x31, 0x80, 0xD1))
        self.assertEqual(runner.IOS_DISPLAY_FRAME_EVENT_ID, 0x31800344)
        self.assertEqual(runner.IOS_DISPLAY_FRAME_EVENT_NAME, "IOMFB_V2_SWAP_ON_GLASS")

    def test_legacy_ordered_source_freshness_expires_and_allows_fps_only_fallback(self) -> None:
        self.assertFalse(runner.ordered_source_is_fresh(None, now=10.0))
        self.assertTrue(runner.ordered_source_is_fresh(7.0, now=10.0))
        self.assertFalse(runner.ordered_source_is_fresh(6.0, now=10.0))
        self.assertTrue(runner.ordered_source_is_fresh(None, 7.0, now=10.0))

        source = RUNNER_PATH.read_text(encoding="utf-8")
        self.assertIn("last_ordered_frame_at = received_at", source)
        self.assertIn("ordered_source_is_fresh(last_ordered_frame_at, last_ordered_sample_at)", source)
        self.assertIn("ordered_accumulator.last_event_breaks_sequence", source)
        self.assertIn("last_ordered_sample_at = None", source)

    def test_legacy_ordered_source_uses_real_zero_windows_for_static_screens(self) -> None:
        source = RUNNER_PATH.read_text(encoding="utf-8")

        self.assertIn("IdleFrameWindowTracker", source)
        self.assertIn("ordered_zero_fps_payload(idle_window_seconds)", source)
        self.assertIn("ordered_accumulator.reset_sequence()", source)

    def test_legacy_ordered_source_carries_device_timeline_metadata(self) -> None:
        source = RUNNER_PATH.read_text(encoding="utf-8")

        self.assertIn('"source_elapsed_sec": source_elapsed_sec', source)
        self.assertIn('"source_sequence": source_sequence', source)
        self.assertIn("source_origin_timestamp", source)

    def test_legacy_sysmontap_short_rows_preserve_missing_values(self) -> None:
        values = runner.sysmontap_process_values(
            ["pid", "name", "cpuUsage", "physFootprint", "memResidentSize"],
            [1225, "WeChat", 38.5],
        )

        self.assertEqual(values["cpuUsage"], 38.5)
        self.assertNotIn("physFootprint", values)
        self.assertNotIn("memResidentSize", values)

    def test_target_process_name_validation_rejects_pid_reuse(self) -> None:
        self.assertTrue(runner.process_name_matches("ldt_global", "ldt_global"))
        self.assertTrue(runner.process_name_matches("", "SpringBoard"))
        self.assertFalse(runner.process_name_matches("ldt_global", ""))
        self.assertFalse(runner.process_name_matches("ldt_global", "SpringBoard"))

    def test_legacy_rebind_never_treats_pid_zero_or_invalid_values_as_target(self) -> None:
        self.assertFalse(runner.is_valid_pending_rebind_pid(0, 261, 0))
        self.assertFalse(runner.is_valid_pending_rebind_pid(0, 261, -1))
        self.assertFalse(runner.is_valid_pending_rebind_pid(0, 261, 0))
        self.assertFalse(runner.is_valid_pending_rebind_pid(261, 261, 261))
        self.assertTrue(runner.is_valid_pending_rebind_pid(262, 261, 262))

    def test_legacy_sysmontap_target_lookup_distinguishes_missing_pid(self) -> None:
        attributes = ["pid", "name", "cpuUsage", "physFootprint", "memResidentSize"]
        rows = [{"Processes": {"597": [597, "ldt_global", 12.5]}}]

        values = runner.find_sysmontap_process(rows, 597, attributes)

        self.assertIsNotNone(values)
        assert values is not None
        self.assertEqual(values["name"], "ldt_global")
        self.assertIsNone(runner.find_sysmontap_process(rows, 598, attributes))

    def test_legacy_target_loss_resets_ordered_sequence(self) -> None:
        source = RUNNER_PATH.read_text(encoding="utf-8")
        start = source.index("def set_target_confirmation")
        end = source.index("def on_sysmontap_message", start)
        transition = source[start:end]

        self.assertIn("ordered_accumulator.reset_sequence()", transition)
        self.assertIn("last_ordered_sample_at = None", transition)
        self.assertIn("set_target_confirmation(False)", source)

    def test_legacy_target_confirmation_tolerates_one_transient_missing_snapshot(self) -> None:
        source = RUNNER_PATH.read_text(encoding="utf-8")

        self.assertIn("TARGET_CONFIRMATION_GRACE_SECONDS = 2.0", source)
        self.assertIn(
            "time.monotonic() - last_target_seen_at >= TARGET_CONFIRMATION_GRACE_SECONDS",
            source,
        )
        self.assertIn("target_rebound", source)
        self.assertIn("find_application_rebind_candidate", source)

    def test_legacy_target_fatal_stops_sources_before_notifying_consumers(self) -> None:
        source = RUNNER_PATH.read_text(encoding="utf-8")
        mismatch_start = source.index("if not process_name_matches(target_name, observed_name):")
        mismatch_end = source.index("last_target_seen_at = time.monotonic()", mismatch_start)
        mismatch = source[mismatch_start:mismatch_end]
        fatal_emit = 'emit(\n                        "fatal"'
        self.assertLess(mismatch.index("set_target_confirmation(False)"), mismatch.index(fatal_emit))
        self.assertLess(mismatch.index("stop_event.set()"), mismatch.index(fatal_emit))

        missing_start = source.index("and not target_error_emitted")
        missing_end = source.index("idle_window_seconds = None", missing_start)
        missing = source[missing_start:missing_end]
        self.assertLess(missing.index("set_target_confirmation(False)"), missing.index(fatal_emit))
        self.assertLess(missing.index("stop_event.set()"), missing.index(fatal_emit))

    def test_ordered_display_metrics_keep_stable_60fps_clean(self) -> None:
        metrics = runner.ordered_display_metrics([16.67] * 60)

        self.assertIsNotNone(metrics)
        fps, jank, big_jank, frame_time_ms = metrics
        self.assertAlmostEqual(fps, 59.99, places=1)
        self.assertEqual(jank, 0)
        self.assertEqual(big_jank, 0)
        self.assertAlmostEqual(frame_time_ms, 16.67, places=2)

    def test_ordered_display_metrics_count_real_spikes_against_previous_three(self) -> None:
        metrics = runner.ordered_display_metrics([90.0, 130.0], [16.0, 16.0, 16.0])

        self.assertIsNotNone(metrics)
        _fps, jank, big_jank, frame_time_ms = metrics
        self.assertEqual(jank, 2)
        self.assertEqual(big_jank, 1)
        self.assertAlmostEqual(frame_time_ms, 130.0)

    def test_ordered_display_metrics_do_not_call_uniform_low_fps_jank(self) -> None:
        metrics = runner.ordered_display_metrics([33.3, 33.3, 33.3, 33.3, 33.3])

        self.assertIsNotNone(metrics)
        _fps, jank, big_jank, _frame_time_ms = metrics
        self.assertEqual(jank, 0)
        self.assertEqual(big_jank, 0)

    def test_ordered_display_metrics_preserve_a_missed_refresh(self) -> None:
        metrics = runner.ordered_display_metrics([16.67, 16.67, 33.34])

        self.assertIsNotNone(metrics)
        fps, _jank, _big_jank, frame_time_ms = metrics
        self.assertAlmostEqual(fps, 45.0, places=1)
        self.assertAlmostEqual(frame_time_ms, 33.34, places=2)

    def test_missing_sysmontap_values_remain_unavailable(self) -> None:
        self.assertIsNone(runner.non_negative_number(None))
        self.assertIsNone(runner.non_negative_number(float("nan")))
        self.assertIsNone(runner.process_memory_payload(1225, None, None))
        self.assertIsNone(runner.process_memory_payload(1225, 0, 0))

    def test_ios_memory_prefers_footprint_and_labels_rss_fallback(self) -> None:
        footprint = runner.process_memory_payload(1225, 200 * 1024 * 1024, 120 * 1024 * 1024)
        rss_only = runner.process_memory_payload(1225, None, 120 * 1024 * 1024)
        zero_footprint_rss = runner.process_memory_payload(1225, 0, 120 * 1024 * 1024)

        self.assertIsNotNone(footprint)
        self.assertEqual(footprint["metric"], "physical_footprint")
        self.assertEqual(footprint["value"], 200.0)
        self.assertEqual(footprint["rss_value"], 120.0)
        self.assertFalse(footprint["fallback"])
        self.assertEqual(footprint["platform"], "ios")
        self.assertEqual(footprint["scope"], "process")
        self.assertIsNotNone(rss_only)
        self.assertEqual(rss_only["metric"], "rss")
        self.assertEqual(rss_only["value"], 120.0)
        self.assertTrue(rss_only["fallback"])
        self.assertEqual(zero_footprint_rss["metric"], "rss")

    def test_legacy_collector_failure_is_fatal_instead_of_silent_waiting(self) -> None:
        class FailingInstruments:
            def __init__(self, **_kwargs):
                pass

            def __enter__(self):
                raise RuntimeError("legacy DVT unavailable")

            def __exit__(self, *_args):
                return None

        emitted = []
        original_base = runner.InstrumentsBase
        original_emit = runner.emit
        runner.InstrumentsBase = FailingInstruments
        runner.emit = lambda kind, payload: emitted.append((kind, payload))
        stop_event = threading.Event()
        try:
            runner.run_legacy_metrics("test-udid", 1225, 1000, True, stop_event, "WeChat")
        finally:
            runner.InstrumentsBase = original_base
            runner.emit = original_emit

        fatal = [payload for kind, payload in emitted if kind == "fatal"]
        self.assertTrue(stop_event.is_set())
        self.assertEqual(len(fatal), 1)
        self.assertEqual(fatal[0]["code"], "ios_legacy_metrics_failed")
        self.assertIn("legacy DVT unavailable", fatal[0]["message"])

    def test_modern_collector_start_failure_is_fatal(self) -> None:
        import ios_pmd3_metrics

        emitted = []
        original_modern = ios_pmd3_metrics.run_metrics
        original_emit = runner.emit
        ios_pmd3_metrics.run_metrics = lambda *_args, **_kwargs: (_ for _ in ()).throw(
            RuntimeError("RSD unavailable")
        )
        runner.emit = lambda kind, payload: emitted.append((kind, payload))
        stop_event = threading.Event()
        try:
            runner.run_metrics("test-udid", 597, 1000, True, stop_event, "26.5", "ldt_global")
        finally:
            ios_pmd3_metrics.run_metrics = original_modern
            runner.emit = original_emit

        fatal = [payload for kind, payload in emitted if kind == "fatal"]
        self.assertTrue(stop_event.is_set())
        self.assertEqual(len(fatal), 1)
        self.assertEqual(fatal[0]["code"], "ios_modern_metrics_failed")
        self.assertIn("RSD unavailable", fatal[0]["message"])


if __name__ == "__main__":
    unittest.main()
