from __future__ import annotations

import math
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "csharp_perf_monitor" / "tools"))

import display_metrics as metrics  # noqa: E402


class DisplayMetricsTests(unittest.TestCase):
    def test_accumulator_reports_whether_latest_timestamp_advanced_a_valid_sequence(self) -> None:
        accumulator = metrics.OrderedTimestampAccumulator(
            tick_to_nanoseconds=1_000_000.0,
            window_ms=1_000.0,
            max_interval_ms=100.0,
        )

        self.assertIsNone(accumulator.add_timestamp(100.0))
        self.assertTrue(accumulator.last_event_accepted)
        self.assertFalse(accumulator.last_event_breaks_sequence)

        self.assertIsNone(accumulator.add_timestamp(100.0))
        self.assertFalse(accumulator.last_event_accepted)
        self.assertFalse(accumulator.last_event_breaks_sequence)

        self.assertIsNone(accumulator.add_timestamp(90.0))
        self.assertFalse(accumulator.last_event_accepted)
        self.assertTrue(accumulator.last_event_breaks_sequence)

        self.assertIsNone(accumulator.add_timestamp(200.0))
        self.assertTrue(accumulator.last_event_accepted)
        self.assertFalse(accumulator.last_event_breaks_sequence)

        self.assertIsNone(accumulator.add_timestamp(500.0))
        self.assertFalse(accumulator.last_event_accepted)
        self.assertTrue(accumulator.last_event_breaks_sequence)

    def test_timestamp_accumulator_preserves_order_and_reports_source_degradation(self) -> None:
        accumulator = metrics.OrderedTimestampAccumulator(
            tick_to_nanoseconds=1_000_000.0,
            window_ms=40.0,
        )

        self.assertIsNone(accumulator.add_timestamp(100.0))
        self.assertIsNone(accumulator.add_timestamp(110.0))
        self.assertIsNone(accumulator.add_timestamp(110.0))
        self.assertIsNone(accumulator.add_timestamp(105.0))
        self.assertIsNone(accumulator.add_timestamp(120.0))
        self.assertIsNone(accumulator.add_timestamp(140.0))
        self.assertIsNone(accumulator.add_timestamp(150.0))
        window = accumulator.add_timestamp(160.0)

        self.assertIsNotNone(window)
        assert window is not None
        self.assertEqual(window.metrics.frame_count, 3)
        self.assertEqual(window.duplicate_timestamps, 1)
        self.assertEqual(window.out_of_order_timestamps, 1)
        self.assertEqual(window.invalid_intervals, 0)
        self.assertTrue(window.source_degraded)
        self.assertFalse(window.derived_metrics_available)

        self.assertIsNone(accumulator.add_timestamp(170.0))
        self.assertIsNone(accumulator.add_timestamp(180.0))
        self.assertIsNone(accumulator.add_timestamp(190.0))
        recovered = accumulator.add_timestamp(200.0)

        self.assertIsNotNone(recovered)
        assert recovered is not None
        self.assertTrue(recovered.derived_metrics_available)
        self.assertFalse(recovered.source_degraded)

    def test_out_of_order_timestamp_drops_the_old_anchor_before_recovery(self) -> None:
        accumulator = metrics.OrderedTimestampAccumulator(
            tick_to_nanoseconds=1_000_000.0,
            window_ms=16.0,
        )

        self.assertIsNone(accumulator.add_timestamp(1_000.0))
        self.assertIsNone(accumulator.add_timestamp(900.0))
        self.assertIsNone(accumulator.add_timestamp(1_016.0))
        window = accumulator.add_timestamp(1_032.0)

        self.assertIsNotNone(window)
        assert window is not None
        self.assertEqual(window.metrics.frame_count, 1)
        self.assertAlmostEqual(window.metrics.frame_time_max_ms, 16.0)
        self.assertEqual(window.out_of_order_timestamps, 1)
        self.assertFalse(window.derived_metrics_available)

    def test_timestamp_accumulator_rebases_after_an_invalid_transport_gap(self) -> None:
        accumulator = metrics.OrderedTimestampAccumulator(
            tick_to_nanoseconds=1_000_000.0,
            window_ms=20.0,
            max_interval_ms=100.0,
        )

        self.assertIsNone(accumulator.add_timestamp(0.0))
        self.assertIsNone(accumulator.add_timestamp(500.0))
        self.assertIsNone(accumulator.add_timestamp(510.0))
        window = accumulator.add_timestamp(520.0)

        self.assertIsNotNone(window)
        assert window is not None
        self.assertEqual(window.metrics.frame_count, 2)
        self.assertEqual(window.invalid_intervals, 1)
        self.assertTrue(window.source_degraded)
        self.assertFalse(window.derived_metrics_available)

    def test_unparseable_timestamp_drops_the_old_anchor_before_recovery(self) -> None:
        accumulator = metrics.OrderedTimestampAccumulator(
            tick_to_nanoseconds=1_000_000.0,
            window_ms=20.0,
            max_interval_ms=1_000.0,
        )

        self.assertIsNone(accumulator.add_timestamp(0.0))
        self.assertIsNone(accumulator.add_timestamp(10.0))
        self.assertIsNone(accumulator.add_timestamp(object()))
        self.assertIsNone(accumulator.add_timestamp(100.0))
        self.assertIsNone(accumulator.add_timestamp(110.0))
        window = accumulator.add_timestamp(120.0)

        self.assertIsNotNone(window)
        assert window is not None
        self.assertEqual(window.metrics.frame_count, 2)
        self.assertAlmostEqual(window.metrics.frame_time_max_ms, 10.0)
        self.assertEqual(window.invalid_intervals, 1)
        self.assertFalse(window.derived_metrics_available)

    def test_non_finite_timestamp_drops_the_old_anchor_before_recovery(self) -> None:
        accumulator = metrics.OrderedTimestampAccumulator(
            tick_to_nanoseconds=1_000_000.0,
            window_ms=20.0,
            max_interval_ms=1_000.0,
        )

        self.assertIsNone(accumulator.add_timestamp(0.0))
        self.assertIsNone(accumulator.add_timestamp(10.0))
        self.assertIsNone(accumulator.add_timestamp(math.nan))
        self.assertIsNone(accumulator.add_timestamp(100.0))
        self.assertIsNone(accumulator.add_timestamp(110.0))
        window = accumulator.add_timestamp(120.0)

        self.assertIsNotNone(window)
        assert window is not None
        self.assertAlmostEqual(window.metrics.frame_time_max_ms, 10.0)
        self.assertEqual(window.invalid_intervals, 1)

    def test_idle_tracker_emits_one_real_zero_window_per_interval(self) -> None:
        tracker = metrics.IdleFrameWindowTracker(1.0)
        tracker.mark_frame(10.0)

        self.assertIsNone(tracker.take_due_window(10.9))
        self.assertAlmostEqual(tracker.take_due_window(11.0) or 0, 1.0)
        self.assertIsNone(tracker.take_due_window(11.5))
        self.assertAlmostEqual(tracker.take_due_window(12.1) or 0, 1.1)

        tracker.mark_frame(12.2)
        self.assertIsNone(tracker.take_due_window(12.9))
        self.assertAlmostEqual(tracker.take_due_window(13.2) or 0, 1.0)

    def test_accumulator_reset_drops_idle_gap_and_previous_jank_history(self) -> None:
        accumulator = metrics.OrderedTimestampAccumulator(
            tick_to_nanoseconds=1_000_000.0,
            window_ms=40.0,
        )
        accumulator.add_timestamp(0.0)
        accumulator.add_timestamp(16.0)
        accumulator.add_timestamp(32.0)
        accumulator.reset_sequence()

        accumulator.add_timestamp(5_000.0)
        accumulator.add_timestamp(5_016.0)
        accumulator.add_timestamp(5_032.0)
        window = accumulator.add_timestamp(5_048.0)

        self.assertIsNotNone(window)
        assert window is not None
        self.assertEqual(window.metrics.jank, 0)
        self.assertEqual(window.metrics.big_jank, 0)
        self.assertLess(window.metrics.frame_time_max_ms, 20.0)

    def test_ordered_zero_window_has_no_synthetic_frame_time(self) -> None:
        payload = metrics.ordered_zero_fps_payload(1.25)

        self.assertEqual(payload["fps"], 0.0)
        self.assertEqual(payload["frame_count"], 0)
        self.assertEqual(payload["window_sec"], 1.25)
        self.assertEqual(payload["jank"], 0.0)
        self.assertTrue(payload["ordered_frames"])
        self.assertNotIn("frame_time_ms", payload)

    def test_big_jank_is_also_counted_as_jank_and_contributes_to_stutter(self) -> None:
        result = metrics.ordered_frame_metrics([90.0, 130.0], [16.0, 16.0, 16.0])

        self.assertIsNotNone(result)
        assert result is not None
        self.assertEqual(result.jank, 2)
        self.assertEqual(result.big_jank, 1)
        self.assertAlmostEqual(result.jank_time_ms, 220.0)
        self.assertAlmostEqual(result.stutter_percent, 100.0)

    def test_uniform_low_fps_is_not_jank(self) -> None:
        result = metrics.ordered_frame_metrics([33.3] * 30)

        self.assertIsNotNone(result)
        assert result is not None
        self.assertEqual(result.jank, 0)
        self.assertEqual(result.big_jank, 0)
        self.assertEqual(result.jank_time_ms, 0)
        self.assertEqual(result.stutter_percent, 0)

    def test_ordered_metrics_expose_mean_p95_max_and_observation_duration(self) -> None:
        result = metrics.ordered_frame_metrics([10.0, 20.0, 30.0, 40.0])

        self.assertIsNotNone(result)
        assert result is not None
        self.assertAlmostEqual(result.fps, 40.0)
        self.assertAlmostEqual(result.frame_time_mean_ms, 25.0)
        self.assertAlmostEqual(result.frame_time_p95_ms, 40.0)
        self.assertAlmostEqual(result.frame_time_max_ms, 40.0)
        self.assertAlmostEqual(result.observation_ms, 100.0)
        self.assertEqual(result.frame_count, 4)

    def test_one_second_accumulator_emits_exact_60hz_and_120hz_windows(self) -> None:
        for refresh_rate in (60, 120):
            accumulator = metrics.OrderedTimestampAccumulator(
                tick_to_nanoseconds=1.0,
                window_ms=1_000.0,
            )
            self.assertIsNone(accumulator.add_timestamp(0))
            window = None
            for frame in range(1, refresh_rate + 1):
                timestamp_ns = round(frame * 1_000_000_000 / refresh_rate)
                window = accumulator.add_timestamp(timestamp_ns)
                if frame < refresh_rate:
                    self.assertIsNone(window)

            self.assertIsNotNone(window)
            assert window is not None
            self.assertEqual(window.metrics.frame_count, refresh_rate)
            self.assertAlmostEqual(window.metrics.observation_ms, 1_000.0, places=6)
            self.assertAlmostEqual(window.metrics.fps, float(refresh_rate), places=6)

    def test_window_keeps_a_crossing_long_frame_whole_and_reports_real_duration(self) -> None:
        accumulator = metrics.OrderedTimestampAccumulator(
            tick_to_nanoseconds=1_000_000.0,
            window_ms=1_000.0,
        )
        timestamp_ms = 0.0
        self.assertIsNone(accumulator.add_timestamp(timestamp_ms))
        for _ in range(59):
            timestamp_ms += 1_000.0 / 60.0
            self.assertIsNone(accumulator.add_timestamp(timestamp_ms))
        timestamp_ms += 100.0
        window = accumulator.add_timestamp(timestamp_ms)

        self.assertIsNotNone(window)
        assert window is not None
        self.assertEqual(window.metrics.frame_count, 60)
        self.assertAlmostEqual(window.metrics.frame_time_max_ms, 100.0, places=6)
        self.assertAlmostEqual(window.metrics.observation_ms, 1_083.3333333333333, places=6)
        self.assertAlmostEqual(window.metrics.fps, 60_000.0 / 1_083.3333333333333, places=6)

    def test_invalid_or_empty_interval_sequence_is_unavailable(self) -> None:
        self.assertIsNone(metrics.ordered_frame_metrics([]))
        self.assertIsNone(metrics.ordered_frame_metrics([0.0, -1.0, math.nan, math.inf]))
        self.assertIsNone(metrics.ordered_frame_metrics([1.0] * 1000))


if __name__ == "__main__":
    unittest.main()
