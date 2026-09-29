"""Source-derived RenderService fixtures; these are not real-device recordings."""
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "csharp_perf_monitor" / "tools"))
import harmony_perf_runner as runner

NODE = (42 << 32) + 7
WINDOWS = "ohos-window list-windows --filter foreground --type meta"


def windows(*nodes, pid=42):
    return json.dumps({"type": "result", "status": "success", "data": {
        "windows": [{"metaInfo": {"pid": pid, "surfaceNodeId": node}}
                    for node in nodes]}})


def surfaces(*nodes):
    return "\n".join(f" surface [Game] NodeId[{node}] LayerId[1]:" for node in nodes)


def frames(times, name="Game"):
    rows = [f"{t - 1000}:{t}" for t in reversed(times)]
    return f"-- The recently fps records info of screens:\n surface [{name}]:\n" + "\n".join(rows + ["0:0"] * (384 - len(rows)))


class HarmonyFrameTests(unittest.TestCase):
    def make_source(self, dumps, inventories=None, window_dumps=None):
        hdc = Mock()
        dumps = iter(dumps)
        inventories = iter(inventories) if inventories is not None else None
        window_dumps = iter(window_dumps) if window_dumps is not None else None
        last_inventory = None
        last_window = None

        def shell(command, **kwargs):
            nonlocal last_inventory, last_window
            if command == "hidumper -s RenderService -a surface":
                if inventories is None:
                    return surfaces(NODE)
                try:
                    last_inventory = next(inventories)
                except StopIteration:
                    pass
                return last_inventory
            if command == WINDOWS:
                self.assertLessEqual(kwargs["timeout"], 1.0)
                if window_dumps is None:
                    return windows()
                try:
                    last_window = next(window_dumps)
                except StopIteration:
                    pass
                value = last_window
                if isinstance(value, Exception):
                    raise value
                return value
            self.assertIn(command, [f"hidumper -s RenderService -a 'fps -id {node}'"
                                    for node in (NODE, NODE + 1)])
            value = next(dumps)
            if isinstance(value, Exception):
                raise value
            return value

        hdc.shell.side_effect = shell
        return runner.HarmonyDisplayFrameSource(hdc, 42), hdc

    def test_surface_owner_is_node_pid_not_name_or_first_row(self):
        source, hdc = self.make_source([frames([10**9])], [surfaces((12 << 32) + 1, NODE)])
        self.assertEqual(source.poll(0), [])
        self.assertTrue(any(f"fps -id {NODE}" in call.args[0] for call in hdc.shell.call_args_list))

    def test_multiple_owned_nodes_are_ambiguous_even_with_identical_name(self):
        source, hdc = self.make_source([], [surfaces(NODE, NODE + 1)])
        self.assertEqual(source.poll(0), [])
        self.assertFalse(any("fps -id" in call.args[0] for call in hdc.shell.call_args_list))
        self.assertEqual(source.consume_status()[0], "harmony_frame_surface_ambiguous")

    def test_initial_history_ignored_all_completed_windows_kept(self):
        history = [10**9 + i * 20_000_000 for i in range(60)]
        updated = history + [history[-1] + i * 20_000_000 for i in range(1, 153)]
        source, _ = self.make_source([frames(history), frames(updated), frames(updated)])
        self.assertEqual(source.poll(0), [])
        payloads = source.poll(3.2)
        self.assertEqual(len(payloads), 3)
        for payload in payloads:
            self.assertAlmostEqual(payload["fps"], 50)
            self.assertEqual(payload["frame_count"], 50)
            self.assertEqual(payload["frame_time_max_ms"], 20)
            self.assertTrue(payload["target_verified"])
            self.assertEqual(payload["surface_owner_pid"], 42)
        self.assertEqual([p["source_sequence"] for p in payloads], [1, 2, 3])
        for payload, lag in zip(payloads, [2.02, 1.02, 0.02]):
            self.assertAlmostEqual(payload["source_lag_sec"], lag)
        self.assertEqual(source.poll(4.2), [])  # stale history is not confirmed zero FPS

    def test_jank_uses_shared_actual_present_intervals(self):
        times = [10**9]
        for interval in [20] * 8 + [150] + [20] * 47:
            times.append(times[-1] + interval * 1_000_000)
        source, _ = self.make_source([frames(times[:1]), frames(times)])
        source.poll(0)
        result = source.poll(1.5)[0]
        self.assertEqual(result["jank"], 1)
        self.assertEqual(result["big_jank"], 1)
        self.assertEqual(result["frame_time_max_ms"], 150)
        self.assertEqual(result["jank_time_ms"], 150)

    def test_truncation_wrong_name_padding_corruption_and_reordering_rejected(self):
        valid = frames([10**9, 1_020_000_000])
        for corrupt in (valid.rsplit("\n", 1)[0], frames([10**9], "Other"),
                        valid.replace("0:0", "12:bad", 1),
                        frames([10**9, 900_000_000]),
                        valid.replace("0:0\n0:0", "0:0\n1:2", 1),
                        valid + "\n surface [Other]:\n1:2",
                        frames([2**64 - 1])):
            with self.subTest(corrupt=corrupt[:80]):
                source, _ = self.make_source([corrupt])
                self.assertEqual(source.poll(0), [])
                self.assertEqual(source.consume_status()[0], "harmony_frame_dump_invalid")

    def test_lost_overlap_rebaselines_instead_of_inventing_stall(self):
        a = [10**9 + i * 20_000_000 for i in range(60)]
        b = [20 * 10**9 + i * 20_000_000 for i in range(60)]
        source, _ = self.make_source([frames(a), frames(b)])
        source.poll(0)
        self.assertEqual(source.poll(20), [])
        self.assertEqual(source.consume_status()[0], "harmony_frame_source_gap")

    def test_source_failure_backs_off_and_preserves_no_stale_accumulator(self):
        source, hdc = self.make_source([runner.HdcFailure("denied"), frames([10**9])])
        self.assertEqual(source.poll(0), [])
        calls = hdc.shell.call_count
        self.assertEqual(source.poll(1), [])
        self.assertEqual(hdc.shell.call_count, calls)
        self.assertEqual(source.poll(5), [])

    def test_new_ambiguous_surface_invalidates_previously_bound_node(self):
        source, hdc = self.make_source(
            [frames([10**9])], [surfaces(NODE), surfaces(NODE, NODE + 1)],
            [windows(NODE, NODE + 1)])
        source.poll(0)
        self.assertEqual(source.poll(1), [])
        self.assertEqual(sum("fps -id" in call.args[0] for call in hdc.shell.call_args_list), 1)
        self.assertEqual(source.consume_status()[0], "harmony_frame_surface_ambiguous")

    def test_exact_foreground_window_resolves_multiple_owned_surfaces(self):
        times = [10**9 + i * 20_000_000 for i in range(102)]
        source, hdc = self.make_source(
            [frames(times[:1]), frames(times)],
            [surfaces(NODE, NODE + 1)] * 2,
            [windows(NODE + 1)] * 4)
        self.assertEqual(source.poll(0), [])
        result = source.poll(2.1)
        self.assertEqual(len(result), 2)
        self.assertTrue(all(p["fps"] == 50 and p["surface_node_id"] == NODE + 1
                            and p["surface_owner_pid"] == 42 for p in result))
        self.assertFalse(any(f"fps -id {NODE}'" in call.args[0] for call in hdc.shell.call_args_list))

    def test_foreground_window_switch_starts_new_sequence_without_old_history(self):
        times = [10**9 + i * 20_000_000 for i in range(52)]
        later = [20 * 10**9 + i * 25_000_000 for i in range(42)]
        source, _ = self.make_source(
            [frames(times[:1]), frames(times), frames(later[:1]), frames(later)],
            [surfaces(NODE, NODE + 1)] * 4,
            [windows(NODE)] * 4 + [windows(NODE + 1)] * 4)
        source.poll(0)
        before = source.poll(1.1)[0]
        self.assertEqual(source.poll(2), [])
        self.assertEqual(source.poll(3.1), [])  # new window's first dump is its baseline
        after = source.poll(4.1)[0]
        self.assertEqual(after["fps"], 40)
        self.assertEqual(after["frame_time_max_ms"], 25)
        self.assertEqual(after["jank"], 0)
        self.assertNotEqual(before["frame_source"], after["frame_source"])

    def test_window_evidence_cannot_choose_by_focus_name_or_partial_intersection(self):
        for data in (windows(NODE, NODE + 1), windows(NODE, NODE + 2),
                     windows(NODE + 2), windows(NODE, pid=41),
                     windows((41 << 32) + 7), windows(float(NODE)), windows(str(NODE)),
                     windows(True), windows(NODE).replace('"success"', '"error"'),
                     '{"type":"result","status":"success","data":[]}',
                     '{"type":"result","status":"success","data":{"windows":[null]}}',
                     windows(NODE).replace("}]}}", "}, null]}}"),
                     "permission denied", windows(NODE)[:-1]):
            with self.subTest(data=data):
                source, hdc = self.make_source([], [surfaces(NODE, NODE + 1)], [data])
                self.assertEqual(source.poll(0), [])
                self.assertFalse(any("fps -id" in call.args[0] for call in hdc.shell.call_args_list))

    def test_foreground_query_denied_is_optional_and_retried_after_backoff(self):
        times = [10**9 + i * 20_000_000 for i in range(52)]
        source, hdc = self.make_source(
            [frames(times[:1]), frames(times)], [surfaces(NODE, NODE + 1)] * 3,
            [runner.HdcFailure("permission denied")] + [windows(NODE)] * 4)
        self.assertEqual(source.poll(0), [])
        calls = hdc.shell.call_count
        self.assertEqual(source.poll(1), [])
        self.assertEqual(hdc.shell.call_count, calls)
        self.assertEqual(source.poll(5), [])
        self.assertEqual(source.poll(6.1)[0]["fps"], 50)

    def test_foreground_change_during_dump_discards_batch_and_rebaselines(self):
        times = [10**9 + i * 20_000_000 for i in range(52)]
        source, _ = self.make_source(
            [frames(times[:1]), frames(times), frames(times)],
            [surfaces(NODE, NODE + 1)] * 3,
            [windows(NODE), windows(NODE), windows(NODE + 1), windows(NODE + 1),
             windows(NODE + 1), windows(NODE + 1)])
        self.assertEqual(source.poll(0), [])
        self.assertEqual(source.poll(1.1), [])
        self.assertEqual(source.consume_status()[0], "harmony_frame_window_changed")
        self.assertEqual(source.poll(7), [])

    def test_single_surface_change_to_multiple_surfaces_during_dump_discards_batch(self):
        times = [10**9 + i * 20_000_000 for i in range(52)]
        source, _ = self.make_source(
            [frames(times[:1]), frames(times)],
            [surfaces(NODE), surfaces(NODE), surfaces(NODE, NODE + 1)],
            [windows(NODE + 1)])
        self.assertEqual(source.poll(0), [])
        self.assertEqual(source.poll(1.1), [])
        self.assertEqual(source.consume_status()[0], "harmony_frame_window_changed")

    def test_single_surface_does_not_require_window_service(self):
        source, hdc = self.make_source([frames([10**9])])
        self.assertEqual(source.poll(0), [])
        self.assertFalse(any(call.args[0] == WINDOWS for call in hdc.shell.call_args_list))

    def test_initial_gap_before_capture_is_not_reported_as_jank(self):
        times = [10**9] + [100 * 10**9 + i * 20_000_000 for i in range(51)]
        source, _ = self.make_source([frames(times[:1]), frames(times)])
        source.poll(0)
        result = source.poll(1.1)[0]
        self.assertEqual(result["frame_time_max_ms"], 20)
        self.assertEqual(result["jank"], 0)

    def test_source_elapsed_uses_only_render_service_timestamps(self):
        times = [10**9 + i * 20_000_000 for i in range(102)]
        source, _ = self.make_source([frames(times[:1]), frames(times[:52]), frames(times)])
        source.poll(1000.0)
        result = source.poll(1001.0)[0]
        self.assertAlmostEqual(result["source_elapsed_sec"], 1.0, places=6)
        later = source.poll(1010.0)[0]
        self.assertAlmostEqual(later["source_elapsed_sec"], 2.0, places=6)

    def test_source_elapsed_restarts_after_ring_gap(self):
        first = [10**9 + i * 20_000_000 for i in range(52)]
        second = [20 * 10**9 + i * 20_000_000 for i in range(52)]
        source, _ = self.make_source([frames(first[:1]), frames(first), frames(second[:1]), frames(second)])
        source.poll(0.0)
        first_result = source.poll(1.0)[0]
        self.assertAlmostEqual(first_result["source_elapsed_sec"], 1.0, places=6)
        self.assertEqual(source.poll(2.0), [])
        second_result = source.poll(3.0)[0]
        self.assertAlmostEqual(second_result["source_elapsed_sec"], 1.0, places=6)
        self.assertNotEqual(first_result["frame_source"], second_result["frame_source"])

    def test_even_short_history_gap_is_excluded_from_first_window(self):
        times = [10**9] + [1_150_000_000 + i * 20_000_000 for i in range(51)]
        source, _ = self.make_source([frames(times[:1]), frames(times)])
        source.poll(0)
        result = source.poll(2)[0]
        self.assertEqual(result["frame_time_max_ms"], 20)
        self.assertEqual(result["frame_count"], 50)
        self.assertAlmostEqual(result["window_sec"], 1)
        self.assertAlmostEqual(result["source_elapsed_sec"], 1)

    def test_cleared_ring_starts_new_generation(self):
        times = [10**9 + i * 20_000_000 for i in range(52)]
        source, _ = self.make_source([frames(times[:1]), frames(times), frames([]),
                                      frames(times[:1]), frames(times)])
        source.poll(0)
        before = source.poll(2)[0]
        self.assertEqual(source.poll(3), [])
        self.assertEqual(source.poll(4), [])
        after = source.poll(6)[0]
        self.assertNotEqual(before["frame_source"], after["frame_source"])
        self.assertEqual(after["source_sequence"], before["source_sequence"] + 1)


if __name__ == "__main__":
    unittest.main()
