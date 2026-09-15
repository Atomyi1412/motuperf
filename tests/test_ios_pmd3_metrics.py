from __future__ import annotations

import asyncio
import dataclasses
import struct
import sys
import threading
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "csharp_perf_monitor" / "tools"))

import ios_pmd3_metrics as runner  # noqa: E402
from display_metrics import OrderedTimestampAccumulator  # noqa: E402


_KDEBUG_RECORD = struct.Struct("<Q32sQIIQ")


def _kdebug_record(timestamp: int, debug_id: int) -> bytes:
    return _KDEBUG_RECORD.pack(timestamp, bytes(32), 0, debug_id, 0, 0)


def _kdebug_v2_header(thread_count: int = 1) -> bytes:
    size = runner.KDEBUG_V2_HEADER_FIXED_BYTES + thread_count * runner.KDEBUG_V2_THREAD_ENTRY_BYTES
    size = (size + runner.KDEBUG_RECORD_BYTES - 1) // runner.KDEBUG_RECORD_BYTES * runner.KDEBUG_RECORD_BYTES
    header = bytearray(size)
    header[:4] = runner.KDEBUG_V2_MAGIC
    struct.pack_into("<I", header, 4, thread_count)
    return bytes(header)


class IosPymobiledevice3MetricTests(unittest.TestCase):
    def test_integrity_break_revokes_ordered_freshness_until_a_valid_frame_recovers(self) -> None:
        state = runner._SourceState(last_ordered_sample_at=10.0, last_ordered_frame_at=10.5)

        state.mark_ordered_integrity_break()

        self.assertIsNone(state.last_ordered_sample_at)
        self.assertIsNone(state.last_ordered_frame_at)
        self.assertFalse(state.ordered_is_fresh(10.6))

        state.mark_ordered_frame(11.0)
        self.assertTrue(state.ordered_is_fresh(11.1))

    def test_any_recent_ordered_frame_suppresses_overlapping_graphics_fallback(self) -> None:
        state = runner._SourceState()
        state.last_ordered_frame_at = 10.0

        self.assertTrue(state.ordered_is_fresh(11.0))
        self.assertFalse(state.ordered_is_fresh(14.0))

        state.last_ordered_frame_at = None
        state.last_ordered_sample_at = 10.0
        self.assertTrue(state.ordered_is_fresh(11.0))
        self.assertFalse(state.ordered_is_fresh(14.0))

    def test_ordered_payload_is_screen_scoped_and_exact(self) -> None:
        accumulator = OrderedTimestampAccumulator(tick_to_nanoseconds=1_000_000.0, window_ms=30.0)
        accumulator.add_timestamp(0.0)
        accumulator.add_timestamp(10.0)
        accumulator.add_timestamp(20.0)
        window = accumulator.add_timestamp(30.0)

        self.assertIsNotNone(window)
        assert window is not None
        payload = runner._ordered_payload(123, window)

        self.assertEqual(payload["platform"], "ios")
        self.assertEqual(payload["source"], "pymobiledevice3-coreprofile-rsd-display")
        self.assertEqual(payload["scope"], "screen")
        self.assertTrue(payload["ordered_frames"])
        self.assertFalse(payload["approximate"])
        self.assertEqual(payload["frame_source"], "iomfb-swap-on-glass")
        self.assertEqual(payload["display_event"], "IOMFB_V2_SWAP_ON_GLASS")
        self.assertEqual(payload["display_event_id"], "0x31800344")
        self.assertEqual(payload["frame_count"], 3)
        self.assertAlmostEqual(payload["frame_time_mean_ms"], 10.0)

    def test_degraded_ordered_payload_keeps_fps_but_omits_derived_metrics(self) -> None:
        accumulator = OrderedTimestampAccumulator(tick_to_nanoseconds=1_000_000.0, window_ms=30.0)
        accumulator.add_timestamp(0.0)
        accumulator.add_timestamp(10.0)
        accumulator.add_timestamp(5.0)
        accumulator.add_timestamp(20.0)
        accumulator.add_timestamp(30.0)
        accumulator.add_timestamp(40.0)
        window = accumulator.add_timestamp(50.0)

        self.assertIsNotNone(window)
        assert window is not None
        payload = runner._ordered_payload(123, window)

        self.assertTrue(payload["source_degraded"])
        self.assertTrue(payload["approximate"])
        self.assertIn("fps", payload)
        self.assertNotIn("frame_time_ms", payload)
        self.assertNotIn("jank", payload)
        self.assertNotIn("big_jank", payload)
        self.assertNotIn("stutter_percent", payload)

    def test_ordered_payload_carries_device_timeline_metadata(self) -> None:
        accumulator = OrderedTimestampAccumulator(tick_to_nanoseconds=1_000_000.0, window_ms=30.0)
        accumulator.add_timestamp(0.0)
        accumulator.add_timestamp(10.0)
        accumulator.add_timestamp(20.0)
        window = accumulator.add_timestamp(30.0)

        assert window is not None
        payload = runner._ordered_payload(597, window, source_elapsed_sec=3.25, source_sequence=4)

        self.assertEqual(payload["source_elapsed_sec"], 3.25)
        self.assertEqual(payload["source_sequence"], 4)

    def test_ordered_idle_window_reports_zero_without_synthetic_frame_time(self) -> None:
        payload = runner._ordered_zero_payload(
            597,
            window_seconds=1.1,
            source_elapsed_sec=8.2,
            source_sequence=7,
        )

        self.assertEqual(payload["fps"], 0.0)
        self.assertEqual(payload["jank"], 0.0)
        self.assertEqual(payload["big_jank"], 0.0)
        self.assertEqual(payload["window_sec"], 1.1)
        self.assertTrue(payload["no_present_frames"])
        self.assertTrue(payload["ordered_frames"])
        self.assertEqual(payload["source_elapsed_sec"], 8.2)
        self.assertEqual(payload["source_sequence"], 7)
        self.assertNotIn("frame_time_ms", payload)

    def test_graphics_fallback_extracts_nested_fps_without_frame_time(self) -> None:
        event = ("selector", [{"CoreAnimationFramesPerSecond": 59.5}])

        self.assertAlmostEqual(runner._fps_from_graphics_event(event) or 0, 59.5)
        self.assertIsNone(runner._fps_from_graphics_event({"fps": -1}))

    def test_graphics_fallback_is_due_as_soon_as_target_is_confirmed(self) -> None:
        state = runner._SourceState()
        state.set_target_confirmed(True)

        self.assertEqual(runner.GRAPHICS_START_DELAY_SECONDS, 0.0)
        self.assertTrue(
            runner._should_start_graphics_fallback(
                collect_fps=True,
                state=state,
                now=10.0,
                next_retry_at=10.0,
                graphics_task=None,
            )
        )

        state.last_ordered_frame_at = 9.0
        self.assertFalse(
            runner._should_start_graphics_fallback(
                collect_fps=True,
                state=state,
                now=10.0,
                next_retry_at=10.0,
                graphics_task=None,
            )
        )

    def test_kdebug_scanner_extracts_only_ordered_on_glass_timestamps(self) -> None:
        scanner = runner._KdebugV2DisplayScanner()
        chunk = b"".join(
            (
                _kdebug_v2_header(),
                _kdebug_record(100, 0x31800318),
                _kdebug_record(200, runner.IOS_DISPLAY_FRAME_EVENT_ID | 2),
                _kdebug_record(300, runner.IOS_DISPLAY_FRAME_EVENT_ID),
            )
        )

        self.assertEqual(scanner.feed(chunk), [200, 300])

    def test_kdebug_scanner_accepts_magic_split_across_messages(self) -> None:
        scanner = runner._KdebugV2DisplayScanner()
        payload = _kdebug_v2_header() + _kdebug_record(200, runner.IOS_DISPLAY_FRAME_EVENT_ID)

        self.assertEqual(scanner.feed(payload[:2]), [])
        self.assertEqual(scanner.feed(payload[2:]), [200])

    def test_zero_memory_is_unavailable_instead_of_a_real_sample(self) -> None:
        self.assertIsNone(runner._memory_payload(597, 0, 0))
        rss_only = runner._memory_payload(597, 0, 50 * 1024 * 1024)
        self.assertIsNotNone(rss_only)
        assert rss_only is not None
        self.assertEqual(rss_only["metric"], "rss")
        self.assertTrue(rss_only["fallback"])

    def test_kdebug_scanner_preserves_partial_records_across_messages(self) -> None:
        scanner = runner._KdebugV2DisplayScanner()
        first_record = _kdebug_record(100, runner.IOS_DISPLAY_FRAME_EVENT_ID)
        second_record = _kdebug_record(200, runner.IOS_DISPLAY_FRAME_EVENT_ID)

        self.assertEqual(scanner.feed(_kdebug_v2_header() + first_record + second_record[:19]), [100])
        self.assertEqual(scanner.feed(b"bplist00status"), [])
        self.assertEqual(scanner.feed(second_record[19:]), [200])

    def test_kdebug_scanner_rejects_v3_without_fabricating_frames(self) -> None:
        scanner = runner._KdebugV2DisplayScanner()

        with self.assertRaisesRegex(RuntimeError, "unsupported kdebug v3"):
            scanner.feed(runner.KDEBUG_V3_MAGIC + bytes(64))

    def test_modern_transport_uses_rsd_coreprofile_and_ordered_event_id(self) -> None:
        source = (ROOT / "csharp_perf_monitor" / "tools" / "ios_pmd3_metrics.py").read_text(encoding="utf-8")

        self.assertIn("UserspaceRsdTunnel", source)
        self.assertIn("DvtProvider", source)
        self.assertIn("CoreProfileSessionTap", source)
        self.assertEqual(runner.IOS_DISPLAY_FRAME_EVENT_ID, 0x31800344)
        self.assertEqual(runner.IOS_DISPLAY_FRAME_EVENT_NAME, "IOMFB_V2_SWAP_ON_GLASS")
        self.assertIn("KDEBUG_EVENT_ID_MASK", source)
        self.assertIn("get_time_config", source)
        self.assertIn('time_config["numer"]', source)
        self.assertIn('time_config["denom"]', source)
        self.assertIn("iomfb-swap-on-glass", source)
        self.assertIn("if not state.target_confirmed", source)
        self.assertIn("if collect_fps and state.target_confirmed", source)

    def test_core_profile_failure_is_retried_without_fabricating_derived_metrics(self) -> None:
        source = (ROOT / "csharp_perf_monitor" / "tools" / "ios_pmd3_metrics.py").read_text(encoding="utf-8")

        self.assertIn("CORE_PROFILE_RETRY_SECONDS = 5.0", source)
        self.assertIn("CORE_PROFILE_MESSAGE_TIMEOUT_SECONDS = 6.0", source)
        self.assertIn("async def _run_core_profile_with_retries", source)
        self.assertIn("await _run_core_profile(dvt, pid, interval_ms, stop_event, emit, state, imports)", source)
        self.assertIn("state.mark_ordered_integrity_break()", source)
        self.assertIn("自动重试", source)
        self.assertIn("_run_core_profile_with_retries(", source)
        self.assertNotIn("_guard_source(\n                    \"iOS 有序 Display FrameTime 源\"", source)

    def test_core_profile_silent_message_times_out(self) -> None:
        class SilentTap:
            uuid = "silent-tap"

            async def __aenter__(self):
                return self

            async def __aexit__(self, *_args):
                return None

            async def _next_message(self):
                await asyncio.sleep(1.0)

        class FakeCoreProfile:
            def __new__(cls, *_args):
                return SilentTap()

            @staticmethod
            async def get_time_config(_dvt):
                return {"numer": 1, "denom": 1}

        previous_timeout = runner.CORE_PROFILE_MESSAGE_TIMEOUT_SECONDS
        runner.CORE_PROFILE_MESSAGE_TIMEOUT_SECONDS = 0.001
        try:
            with self.assertRaises(asyncio.TimeoutError):
                asyncio.run(
                    runner._run_core_profile(
                        object(),
                        597,
                        1000,
                        threading.Event(),
                        lambda _kind, _payload: None,
                        runner._SourceState(),
                        {"CoreProfileSessionTap": FakeCoreProfile},
                    )
                )
        finally:
            runner.CORE_PROFILE_MESSAGE_TIMEOUT_SECONDS = previous_timeout

    def test_core_profile_uses_lightweight_display_only_tap_config(self) -> None:
        config = runner._display_only_tap_config("tap-uuid")

        self.assertEqual(
            config,
            {
                "tc": [
                    {
                        "kdf2": {runner.IOS_DISPLAY_FRAME_EVENT_ID},
                        "tk": 3,
                        "uuid": "tap-uuid",
                    }
                ],
                "rp": 10,
                "bm": 0,
                "ur": 500,
            },
        )
        self.assertNotIn("csd", config["tc"][0])
        self.assertNotIn("ta", config["tc"][0])


class IosPymobiledevice3SysmonTests(unittest.IsolatedAsyncioTestCase):
    async def test_graphics_fallback_discards_the_unprimed_first_event(self) -> None:
        class FakeGraphics:
            async def __aenter__(self):
                return self

            async def __aexit__(self, *_args):
                return None

            async def __aiter__(self):
                yield {"CoreAnimationFramesPerSecond": 0.0}
                yield {"CoreAnimationFramesPerSecond": 59.0}

        state = runner._SourceState()
        state.set_target_confirmed(True)
        emitted = []

        await runner._run_graphics_fallback(
            object(),
            393,
            threading.Event(),
            lambda kind, payload: emitted.append((kind, payload)),
            state,
            {"Graphics": lambda _dvt: FakeGraphics()},
        )

        self.assertEqual(len(emitted), 1)
        self.assertEqual(emitted[0][0], "fps")
        self.assertEqual(emitted[0][1]["fps"], 59.0)

    async def test_sysmontap_uses_recent_cached_device_schema(self) -> None:
        class FakeSysmontap:
            create_calls = 0

            def __init__(self, _dvt, process_attributes, system_attributes, interval_ms):
                self.process_attributes = process_attributes
                self.system_attributes = system_attributes
                self.interval_ms = interval_ms

            @classmethod
            async def create(cls, _dvt, interval):
                cls.create_calls += 1
                raise AssertionError("cached schema should avoid the full device query")

        original_load = runner.load_sysmon_schema
        runner.load_sysmon_schema = lambda _udid: (["pid", "name"], ["vmPressure"])
        try:
            sysmon = await runner._create_sysmontap(
                object(),
                1000,
                {"Sysmontap": FakeSysmontap},
                "ipad-a",
            )
        finally:
            runner.load_sysmon_schema = original_load

        self.assertEqual(sysmon.process_attributes, ["pid", "name"])
        self.assertEqual(sysmon.system_attributes, ["vmPressure"])
        self.assertEqual(sysmon.interval_ms, 1000)
        self.assertEqual(FakeSysmontap.create_calls, 0)

    async def test_sysmontap_falls_back_to_full_schema_query_without_cache(self) -> None:
        expected = object()

        class FakeSysmontap:
            @classmethod
            async def create(cls, _dvt, interval):
                self.assertEqual(interval, 1000)
                return expected

        original_load = runner.load_sysmon_schema
        runner.load_sysmon_schema = lambda _udid: None
        try:
            actual = await runner._create_sysmontap(
                object(),
                1000,
                {"Sysmontap": FakeSysmontap},
                "ipad-a",
            )
        finally:
            runner.load_sysmon_schema = original_load

        self.assertIs(actual, expected)

    def test_raw_sysmon_snapshot_decodes_only_target_and_expected_owner(self) -> None:
        attributes = ["pid", "name", "cpuUsage", "physFootprint"]
        processes = {index: object() for index in range(1000, 1500)}
        processes[393] = [393, "ldt_global", 42.5, 128 * 1024 * 1024]
        processes[500] = [500, "WeChat", 3.0, 256 * 1024 * 1024]

        selected = runner._selected_processes_from_sysmontap_row(
            {"Processes": processes},
            attributes,
            {393, 500},
        )

        self.assertEqual(set(selected), {393, 500})
        self.assertEqual(selected[393]["name"], "ldt_global")
        self.assertEqual(selected[500]["name"], "WeChat")

    def test_same_name_reused_process_is_rejected_by_start_identity(self) -> None:
        error = runner._target_identity_error(
            {
                "pid": 597,
                "name": "com.apple.WebKit.WebContent",
                "startAbsTime": 200,
                "coalitionID": 80,
            },
            {},
            597,
            "com.apple.WebKit.WebContent",
            100,
            80,
            0,
            "",
        )

        self.assertIsNotNone(error)
        assert error is not None
        self.assertEqual(error[0], "target_process_identity_changed")

    def test_owned_webcontent_identity_requires_matching_owner_coalition(self) -> None:
        target = {
            "pid": 597,
            "name": "com.apple.WebKit.WebContent",
            "startAbsTime": 100,
            "coalitionID": 80,
        }
        owner = {"pid": 500, "name": "WeChat", "coalitionID": 81}

        error = runner._target_identity_error(
            target,
            {597: target, 500: owner},
            597,
            "com.apple.WebKit.WebContent",
            100,
            80,
            500,
            "WeChat",
        )

        self.assertIsNotNone(error)
        assert error is not None
        self.assertEqual(error[0], "target_process_owner_changed")

    async def test_sysmontap_confirms_owned_webcontent_before_metrics(self) -> None:
        @dataclasses.dataclass
        class ProcessAttributes:
            pid: int
            name: str
            startAbsTime: int
            coalitionID: int
            cpuUsage: float | None = None
            physFootprint: int | None = None
            memResidentSize: int | None = None

        class FakeSysmontap:
            process_attributes_cls = ProcessAttributes

            @classmethod
            async def create(cls, _dvt: object, interval: int) -> "FakeSysmontap":
                return cls()

            async def __aenter__(self) -> "FakeSysmontap":
                return self

            async def __aexit__(self, *_args: object) -> None:
                return None

            async def iter_processes(self):
                yield [
                    {
                        "pid": 500,
                        "name": "WeChat",
                        "startAbsTime": 90,
                        "coalitionID": 80,
                    },
                    {
                        "pid": 597,
                        "name": "com.apple.WebKit.WebContent",
                        "startAbsTime": 100,
                        "coalitionID": 80,
                        "cpuUsage": 8.5,
                        "physFootprint": 50 * 1024 * 1024,
                        "memResidentSize": 60 * 1024 * 1024,
                    },
                ]

        emitted: list[tuple[str, dict[str, object]]] = []
        state = runner._SourceState()
        await runner._run_sysmontap(
            object(),
            597,
            1000,
            threading.Event(),
            lambda kind, payload: emitted.append((kind, payload)),
            {"Sysmontap": FakeSysmontap},
            expected_name="com.apple.WebKit.WebContent",
            expected_start_abs_time=100,
            expected_coalition_id=80,
            expected_owner_pid=500,
            expected_owner_name="WeChat",
            state=state,
        )

        self.assertEqual([kind for kind, _ in emitted], ["target", "cpu", "memory"])
        self.assertTrue(emitted[0][1]["confirmed"])
        self.assertTrue(state.target_confirmed)

    async def test_sysmontap_preserves_confirmation_for_one_transient_missing_snapshot(self) -> None:
        @dataclasses.dataclass
        class ProcessAttributes:
            pid: int
            name: str
            cpuUsage: float | None = None
            physFootprint: int | None = None
            memResidentSize: int | None = None

        class FakeSysmontap:
            process_attributes_cls = ProcessAttributes

            @classmethod
            async def create(cls, _dvt: object, interval: int) -> "FakeSysmontap":
                return cls()

            async def __aenter__(self) -> "FakeSysmontap":
                return self

            async def __aexit__(self, *_args: object) -> None:
                return None

            async def iter_processes(self):
                yield [
                    {
                        "pid": 597,
                        "name": "ldt_global",
                        "cpuUsage": 8.5,
                        "physFootprint": 50 * 1024 * 1024,
                    }
                ]
                yield [{"pid": 999, "name": "OtherApp"}]

        emitted: list[tuple[str, dict[str, object]]] = []
        state = runner._SourceState()
        await runner._run_sysmontap(
            object(),
            597,
            1000,
            threading.Event(),
            lambda kind, payload: emitted.append((kind, payload)),
            {"Sysmontap": FakeSysmontap},
            expected_name="ldt_global",
            target_timeout_seconds=100.0,
            state=state,
        )

        self.assertEqual([kind for kind, _ in emitted], ["target", "cpu", "memory"])
        self.assertTrue(state.target_confirmed)
        self.assertEqual(state.target_generation, 1)

    async def test_sysmontap_rebinds_unique_application_pid_without_fabricating_gap_metrics(self) -> None:
        @dataclasses.dataclass
        class ProcessAttributes:
            pid: int
            name: str
            startAbsTime: int
            coalitionID: int
            cpuUsage: float | None = None
            physFootprint: int | None = None
            memResidentSize: int | None = None

        class FakeSysmontap:
            process_attributes_cls = ProcessAttributes

            @classmethod
            async def create(cls, _dvt: object, interval: int) -> "FakeSysmontap":
                return cls()

            async def __aenter__(self) -> "FakeSysmontap":
                return self

            async def __aexit__(self, *_args: object) -> None:
                return None

            async def iter_processes(self):
                yield [{
                    "pid": 5233,
                    "name": "WeChat",
                    "startAbsTime": 10,
                    "coalitionID": 20,
                    "cpuUsage": 5.0,
                    "physFootprint": 50 * 1024 * 1024,
                }]
                yield [{"pid": 999, "name": "OtherApp"}]
                yield [{
                    "pid": 2584,
                    "name": "WeChat",
                    "startAbsTime": 30,
                    "coalitionID": 40,
                    "cpuUsage": 7.5,
                    "physFootprint": 70 * 1024 * 1024,
                }]

        class FakeDeviceInfo:
            def __init__(self, _dvt: object):
                pass

            async def __aenter__(self) -> "FakeDeviceInfo":
                return self

            async def __aexit__(self, *_args: object) -> None:
                return None

            async def proclist(self):
                return [{
                    "pid": 2584,
                    "name": "WeChat",
                    "bundleIdentifier": "com.tencent.xin",
                    "isApplication": True,
                }]

        emitted: list[tuple[str, dict[str, object]]] = []
        state = runner._SourceState()
        original_grace = runner.TARGET_CONFIRMATION_GRACE_SECONDS
        original_scan = runner.TARGET_REBIND_SCAN_SECONDS
        runner.TARGET_CONFIRMATION_GRACE_SECONDS = 0.0
        runner.TARGET_REBIND_SCAN_SECONDS = 0.0
        try:
            await runner._run_sysmontap(
                object(),
                5233,
                1000,
                threading.Event(),
                lambda kind, payload: emitted.append((kind, payload)),
                {"Sysmontap": FakeSysmontap, "DeviceInfo": FakeDeviceInfo},
                expected_name="WeChat",
                expected_bundle_id="com.tencent.xin",
                expected_start_abs_time=10,
                expected_coalition_id=20,
                state=state,
            )
        finally:
            runner.TARGET_CONFIRMATION_GRACE_SECONDS = original_grace
            runner.TARGET_REBIND_SCAN_SECONDS = original_scan

        rebound = [payload for kind, payload in emitted if kind == "target_rebound"]
        cpu = [payload for kind, payload in emitted if kind == "cpu"]
        memory = [payload for kind, payload in emitted if kind == "memory"]
        self.assertEqual(len(rebound), 1)
        self.assertEqual(rebound[0]["old_pid"], 5233)
        self.assertEqual(rebound[0]["new_pid"], 2584)
        self.assertEqual([payload["pid"] for payload in cpu], [5233, 2584])
        self.assertEqual([payload["pid"] for payload in memory], [5233, 2584])
        self.assertNotIn("fatal", [kind for kind, _ in emitted])

    async def test_sysmontap_revokes_confirmation_after_missing_grace_period(self) -> None:
        @dataclasses.dataclass
        class ProcessAttributes:
            pid: int
            name: str

        clock = [100.0]

        class FakeSysmontap:
            process_attributes_cls = ProcessAttributes

            @classmethod
            async def create(cls, _dvt: object, interval: int) -> "FakeSysmontap":
                return cls()

            async def __aenter__(self) -> "FakeSysmontap":
                return self

            async def __aexit__(self, *_args: object) -> None:
                return None

            async def iter_processes(self):
                yield [{"pid": 597, "name": "ldt_global"}]
                clock[0] += runner.TARGET_CONFIRMATION_GRACE_SECONDS + 0.1
                yield [{"pid": 999, "name": "OtherApp"}]

        emitted: list[tuple[str, dict[str, object]]] = []
        state = runner._SourceState()
        original_monotonic = runner.time.monotonic
        try:
            runner.time.monotonic = lambda: clock[0]  # type: ignore[method-assign]
            await runner._run_sysmontap(
                object(),
                597,
                1000,
                threading.Event(),
                lambda kind, payload: emitted.append((kind, payload)),
                {"Sysmontap": FakeSysmontap},
                expected_name="ldt_global",
                target_timeout_seconds=100.0,
                state=state,
                collect_cpu=False,
                collect_memory=False,
            )
        finally:
            runner.time.monotonic = original_monotonic  # type: ignore[method-assign]

        self.assertEqual([kind for kind, _ in emitted], ["target", "target"])
        self.assertTrue(emitted[0][1]["confirmed"])
        self.assertFalse(emitted[1][1]["confirmed"])
        self.assertFalse(state.target_confirmed)
        self.assertEqual(state.target_generation, 2)

    async def test_required_sysmontap_failure_stops_the_entire_capture(self) -> None:
        class FakeTunnel:
            def __init__(self, **_kwargs: object) -> None:
                pass

            async def __aenter__(self) -> object:
                return object()

            async def __aexit__(self, *_args: object) -> None:
                return None

        class FakeDvtProvider:
            def __init__(self, _rsd: object) -> None:
                pass

            async def __aenter__(self) -> object:
                return object()

            async def __aexit__(self, *_args: object) -> None:
                return None

        class FailingSysmontap:
            @classmethod
            async def create(cls, _dvt: object, interval: int) -> "FailingSysmontap":
                raise RuntimeError("sysmontap handshake failed")

        emitted: list[tuple[str, dict[str, object]]] = []
        fatal_stop_states: list[bool] = []
        stop_event = threading.Event()

        def emit(kind: str, payload: dict[str, object]) -> None:
            if kind == "fatal":
                fatal_stop_states.append(stop_event.is_set())
            emitted.append((kind, payload))

        original_imports = runner._pmd3_imports
        runner._pmd3_imports = lambda: {
            "UserspaceRsdTunnel": FakeTunnel,
            "DvtProvider": FakeDvtProvider,
            "Sysmontap": FailingSysmontap,
        }
        try:
            await asyncio.wait_for(
                runner._run_metrics_async(
                    "test-udid",
                    597,
                    1000,
                    False,
                    stop_event,
                    emit,
                    "ldt_global",
                ),
                timeout=0.5,
            )
        finally:
            runner._pmd3_imports = original_imports

        fatal = [payload for kind, payload in emitted if kind == "fatal"]
        status_messages = [str(payload["message"]) for kind, payload in emitted if kind == "status"]
        self.assertTrue(stop_event.is_set())
        self.assertTrue(status_messages[0].startswith("正在建立 iOS 17+ RSD"))
        self.assertEqual(len(fatal), 1)
        self.assertEqual(fatal_stop_states, [True])
        self.assertEqual(fatal[0]["code"], "process_metric_source_failed")
        self.assertIn("sysmontap handshake failed", str(fatal[0]["message"]))

    async def test_sysmontap_uses_full_device_schema_factory(self) -> None:
        @dataclasses.dataclass
        class ProcessAttributes:
            pid: int
            name: str
            cpuUsage: float
            physFootprint: int
            memResidentSize: int

        class FakeSysmontap:
            process_attributes_cls = ProcessAttributes
            create_calls: list[tuple[object, int]] = []

            @classmethod
            async def create(cls, dvt: object, interval: int) -> "FakeSysmontap":
                cls.create_calls.append((dvt, interval))
                return cls()

            async def __aenter__(self) -> "FakeSysmontap":
                return self

            async def __aexit__(self, *_args: object) -> None:
                return None

            async def iter_processes(self):
                yield [
                    {
                        "pid": 597,
                        "name": "ldt_global",
                        "cpuUsage": 11.25,
                        "physFootprint": 100 * 1024 * 1024,
                        "memResidentSize": 114 * 1024 * 1024,
                    }
                ]

        dvt = object()
        emitted: list[tuple[str, dict[str, object]]] = []
        state = runner._SourceState()
        await runner._run_sysmontap(
            dvt,
            597,
            1000,
            threading.Event(),
            lambda kind, payload: emitted.append((kind, payload)),
            {"Sysmontap": FakeSysmontap},
            expected_name="ldt_global",
            state=state,
        )

        self.assertEqual(FakeSysmontap.create_calls, [(dvt, 1000)])
        self.assertEqual([kind for kind, _ in emitted], ["target", "cpu", "memory"])
        self.assertAlmostEqual(float(emitted[1][1]["value"]), 11.25)
        self.assertAlmostEqual(float(emitted[2][1]["value"]), 100.0)
        self.assertTrue(state.target_confirmed)

    async def test_sysmontap_can_validate_identity_without_emitting_disabled_metrics(self) -> None:
        @dataclasses.dataclass
        class ProcessAttributes:
            pid: int
            name: str
            cpuUsage: float
            physFootprint: int
            memResidentSize: int

        class FakeSysmontap:
            process_attributes_cls = ProcessAttributes

            @classmethod
            async def create(cls, _dvt: object, interval: int) -> "FakeSysmontap":
                return cls()

            async def __aenter__(self) -> "FakeSysmontap":
                return self

            async def __aexit__(self, *_args: object) -> None:
                return None

            async def iter_processes(self):
                yield [{
                    "pid": 597,
                    "name": "ldt_global",
                    "cpuUsage": 11.25,
                    "physFootprint": 100 * 1024 * 1024,
                    "memResidentSize": 114 * 1024 * 1024,
                }]

        emitted: list[tuple[str, dict[str, object]]] = []
        state = runner._SourceState()
        await runner._run_sysmontap(
            object(),
            597,
            1000,
            threading.Event(),
            lambda kind, payload: emitted.append((kind, payload)),
            {"Sysmontap": FakeSysmontap},
            expected_name="ldt_global",
            state=state,
            collect_cpu=False,
            collect_memory=False,
        )

        self.assertEqual([kind for kind, _ in emitted], ["target"])
        self.assertTrue(emitted[0][1]["confirmed"])
        self.assertTrue(state.target_confirmed)

    async def test_sysmontap_rejects_reused_pid_with_different_process_name(self) -> None:
        @dataclasses.dataclass
        class ProcessAttributes:
            pid: int
            name: str

        class FakeSysmontap:
            process_attributes_cls = ProcessAttributes

            @classmethod
            async def create(cls, _dvt: object, interval: int) -> "FakeSysmontap":
                return cls()

            async def __aenter__(self) -> "FakeSysmontap":
                return self

            async def __aexit__(self, *_args: object) -> None:
                return None

            async def iter_processes(self):
                yield [{"pid": 597, "name": "SpringBoard"}]

        emitted: list[tuple[str, dict[str, object]]] = []
        fatal_state: list[tuple[bool, bool]] = []
        stop_event = threading.Event()
        state = runner._SourceState()
        state.set_target_confirmed(True)

        def emit(kind: str, payload: dict[str, object]) -> None:
            if kind == "fatal":
                fatal_state.append((state.target_confirmed, stop_event.is_set()))
            emitted.append((kind, payload))

        await runner._run_sysmontap(
            object(),
            597,
            1000,
            stop_event,
            emit,
            {"Sysmontap": FakeSysmontap},
            expected_name="ldt_global",
            state=state,
        )

        self.assertTrue(stop_event.is_set())
        self.assertEqual([kind for kind, _ in emitted], ["target", "fatal"])
        self.assertFalse(emitted[0][1]["confirmed"])
        self.assertEqual(emitted[1][1]["code"], "target_process_mismatch")
        self.assertEqual(fatal_state, [(False, True)])
        self.assertFalse(state.target_confirmed)

    async def test_sysmontap_stops_when_selected_pid_is_missing(self) -> None:
        @dataclasses.dataclass
        class ProcessAttributes:
            pid: int
            name: str

        class FakeSysmontap:
            process_attributes_cls = ProcessAttributes

            @classmethod
            async def create(cls, _dvt: object, interval: int) -> "FakeSysmontap":
                return cls()

            async def __aenter__(self) -> "FakeSysmontap":
                return self

            async def __aexit__(self, *_args: object) -> None:
                return None

            async def iter_processes(self):
                yield [{"pid": 999, "name": "OtherApp"}]

        emitted: list[tuple[str, dict[str, object]]] = []
        fatal_state: list[tuple[bool, bool]] = []
        stop_event = threading.Event()
        state = runner._SourceState()
        state.set_target_confirmed(True)

        def emit(kind: str, payload: dict[str, object]) -> None:
            if kind == "fatal":
                fatal_state.append((state.target_confirmed, stop_event.is_set()))
            emitted.append((kind, payload))

        await runner._run_sysmontap(
            object(),
            597,
            1000,
            stop_event,
            emit,
            {"Sysmontap": FakeSysmontap},
            expected_name="ldt_global",
            target_timeout_seconds=0.0,
            state=state,
        )

        self.assertTrue(stop_event.is_set())
        self.assertEqual([kind for kind, _ in emitted], ["target", "fatal"])
        self.assertFalse(emitted[0][1]["confirmed"])
        self.assertEqual(emitted[1][1]["code"], "target_process_missing")
        self.assertEqual(fatal_state, [(False, True)])
        self.assertFalse(state.target_confirmed)


if __name__ == "__main__":
    unittest.main()
