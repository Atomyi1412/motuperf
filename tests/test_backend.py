from __future__ import annotations

from datetime import datetime, timedelta
import unittest

from backend.collectors.ios import (
    IosProcess,
    RealIosCollector,
    app_sort_key,
    classify_process,
    ensure_known_host_apps,
    parse_applist_output,
    parse_memory_mb,
    parse_perf_line,
    process_sort_key,
)
from backend.frame_metrics import estimate_jank_from_fps
from backend.server import parse_bool
from backend.session import SessionManager, normalize_screenshot_interval


class BackendTests(unittest.TestCase):
    def test_session_rejects_mock_mode(self) -> None:
        session = SessionManager()

        with self.assertRaisesRegex(ValueError, "真实采集"):
            session.start("com.tencent.xin", "mock")

    def test_session_exports_csv_header(self) -> None:
        session = SessionManager()
        csv_text = session.export_csv()

        self.assertTrue(
            csv_text.startswith("timestamp,elapsed_sec,fps,jank,big_jank,memory_mb,screenshot_url,source,note")
        )

    def test_screenshot_capture_defaults_to_off_and_clamps_interval(self) -> None:
        session = SessionManager()
        state = session.state()

        self.assertFalse(state.capture_screenshots)
        self.assertEqual(state.screenshot_interval_sec, 10.0)
        self.assertEqual(normalize_screenshot_interval(1), 5.0)
        self.assertEqual(normalize_screenshot_interval(999), 300.0)

    def test_capture_options_can_update_while_running(self) -> None:
        session = SessionManager()
        state = session.update_capture_options(True, 1)

        self.assertTrue(state.capture_screenshots)
        self.assertEqual(state.screenshot_interval_sec, 5.0)

        state = session.update_capture_options(False, 30)
        self.assertFalse(state.capture_screenshots)
        self.assertEqual(state.screenshot_interval_sec, 30.0)

    def test_parse_bool_accepts_frontend_values(self) -> None:
        self.assertTrue(parse_bool(True))
        self.assertTrue(parse_bool("true"))
        self.assertTrue(parse_bool("on"))
        self.assertFalse(parse_bool(False))
        self.assertFalse(parse_bool("false"))

    def test_parse_tidevice_applist_output(self) -> None:
        apps = parse_applist_output(
            "\n".join(
                [
                    "com.tencent.xin 微信 8.0.56",
                    "com.huilai.wxbl 部落 2.10.1",
                    "bad-line-without-version",
                ]
            )
        )

        self.assertEqual(apps[0].bundle_id, "com.tencent.xin")
        self.assertEqual(apps[0].name, "微信")
        self.assertEqual(apps[0].version, "8.0.56")
        self.assertTrue(apps[0].recommended)

    def test_known_wechat_app_is_added_when_applist_misses_it(self) -> None:
        apps = ensure_known_host_apps(parse_applist_output("com.tencent.mqq QQ 8.9.58"))
        apps = sorted(apps, key=app_sort_key)

        self.assertEqual(apps[0].bundle_id, "com.tencent.xin")
        self.assertEqual(apps[0].name, "微信")
        self.assertTrue(apps[0].recommended)

    def test_parse_pyidevice_display_line(self) -> None:
        parsed = parse_perf_line("pyidevice-display", "{'fps': 59, 'jank': 1, 'big_jank': 0}")

        self.assertEqual(parsed, ("fps", {"fps": 59, "jank": 1, "big_jank": 0}))

    def test_parse_pyidevice_memory_line(self) -> None:
        parsed = parse_perf_line("pyidevice-appmonitor", "{'Memory': '812.5 MiB'}")

        self.assertEqual(parsed, ("memory", {"Memory": "812.5 MiB"}))
        self.assertEqual(parse_memory_mb("1.25 GiB"), 1280)

    def test_parse_tidevice_json_line(self) -> None:
        parsed = parse_perf_line("tidevice-perf", 'fps {"value": 58}')

        self.assertEqual(parsed, ("fps", {"value": 58}))

    def test_parse_tidevice_memory_line(self) -> None:
        parsed = parse_perf_line(
            "tidevice-perf",
            'memory {"pid": 1969, "value": 8.469306945800781, "rss_value": 6.53125}',
        )

        self.assertEqual(parsed, ("memory", {"pid": 1969, "value": 8.469306945800781, "rss_value": 6.53125}))
        self.assertEqual(parse_memory_mb(parsed[1]["value"]), 8.469306945800781)

    def test_process_recommendations_prioritize_wechat_and_webcontent(self) -> None:
        wechat = IosProcess(1969, "WeChat", "com.tencent.xin", "微信", True, "")
        webcontent = IosProcess(3315, "com.apple.WebKit.WebContent", "", "", True, "")
        app = IosProcess(999, "FunnyTribe", "com.huilai.wxbl", "部落", False, "")

        self.assertEqual(classify_process("WeChat", "com.tencent.xin")[0], True)
        self.assertEqual(classify_process("com.apple.WebKit.WebContent", "")[0], True)
        self.assertEqual(sorted([app, webcontent, wechat], key=process_sort_key), [wechat, webcontent, app])

    def test_fps_gap_never_fabricates_jank(self) -> None:
        unavailable = estimate_jank_from_fps(42)

        self.assertIsNone(unavailable.jank)
        self.assertIsNone(unavailable.big_jank)
        self.assertIn("有序", unavailable.note)

    def test_fps_only_sample_keeps_jank_and_missing_memory_unavailable(self) -> None:
        collector = RealIosCollector()
        collector._events.put(("fps", {"fps": 55.0}))

        sample = collector.sample(
            datetime.now().astimezone() - timedelta(seconds=1),
            0,
            "com.example.game",
        )
        payload = sample.to_dict()

        self.assertTrue(sample.has_fps)
        self.assertEqual(payload["fps"], 55.0)
        self.assertFalse(sample.has_jank)
        self.assertIsNone(payload["jank"])
        self.assertIsNone(payload["big_jank"])
        self.assertFalse(sample.has_memory)
        self.assertIsNone(payload["memory_mb"])

    def test_zero_fps_is_a_real_available_static_screen_sample(self) -> None:
        collector = RealIosCollector()
        collector._events.put(("fps", {"fps": 0.0}))

        sample = collector.sample(
            datetime.now().astimezone() - timedelta(seconds=1),
            0,
            "com.example.game",
        )

        self.assertTrue(sample.has_fps)
        self.assertEqual(sample.fps, 0.0)
        self.assertFalse(sample.has_jank)


if __name__ == "__main__":
    unittest.main()
