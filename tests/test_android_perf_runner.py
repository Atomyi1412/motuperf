from __future__ import annotations

import sys
import threading
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "csharp_perf_monitor" / "tools"))

import android_perf_runner as runner  # noqa: E402


class AndroidPerfRunnerTests(unittest.TestCase):
    def test_adb_uses_the_explicit_packaged_executable(self) -> None:
        observed: list[list[str]] = []
        original_run = runner.subprocess.run
        original_adb = runner._ADB_EXECUTABLE
        try:
            runner.subprocess.run = lambda command, **_kwargs: (  # type: ignore[assignment]
                observed.append(command)
                or runner.subprocess.CompletedProcess(command, 0, stdout="", stderr="")
            )
            runner.set_adb_executable(r"C:\Program Files\MoTuPerf\runtime\android\adb.exe")
            result = runner.adb("device-1", ["devices"])
        finally:
            runner.subprocess.run = original_run  # type: ignore[assignment]
            runner.set_adb_executable(original_adb)

        self.assertEqual(result.returncode, 0)
        self.assertEqual(
            observed[0],
            [r"C:\Program Files\MoTuPerf\runtime\android\adb.exe", "-s", "device-1", "devices"],
        )

    def test_initial_unknown_identity_retries_before_starting_metric_sources(self) -> None:
        events: list[str] = []

        class FakeThread:
            def __init__(self, target=None, args=(), daemon=None):
                self.args = args

            def start(self) -> None:
                events.append("start:" + str(self.args[0]))

            def join(self, timeout=None) -> None:
                return None

        states = iter([None, None, True, False])
        original_thread = runner.threading.Thread
        original_alive = runner.ensure_pid_alive
        original_sleep = runner.time.sleep
        original_emit = runner.emit
        try:
            runner.threading.Thread = FakeThread  # type: ignore[assignment]

            def probe(*_args):
                state = next(states)
                events.append("probe:" + str(state))
                return state

            runner.ensure_pid_alive = probe  # type: ignore[assignment]
            runner.time.sleep = lambda _seconds: None  # type: ignore[assignment]
            runner.emit = lambda *_: None  # type: ignore[assignment]

            exit_code = runner.main(
                ["--pid", "42", "--target-start-time-ticks", "12345", "--interval", "0"]
            )
        finally:
            runner.threading.Thread = original_thread  # type: ignore[assignment]
            runner.ensure_pid_alive = original_alive  # type: ignore[assignment]
            runner.time.sleep = original_sleep  # type: ignore[assignment]
            runner.emit = original_emit  # type: ignore[assignment]

        self.assertEqual(exit_code, 2)
        self.assertEqual(
            events,
            [
                "probe:None",
                "probe:None",
                "probe:True",
                "start:Android FPS",
                "start:Android PID CPU",
                "start:Android PID 内存",
                "start:Android device thermal metrics",
                "probe:False",
            ],
        )

    def test_initial_unknown_identity_timeout_is_not_reported_as_missing_process(self) -> None:
        emitted: list[tuple[str, dict[str, object]]] = []
        original_alive = runner.ensure_pid_alive
        original_timeout = runner.TARGET_IDENTITY_TIMEOUT_SECONDS
        original_emit = runner.emit
        try:
            runner.ensure_pid_alive = lambda *_: None  # type: ignore[assignment]
            runner.TARGET_IDENTITY_TIMEOUT_SECONDS = 0
            runner.emit = lambda kind, payload: emitted.append((kind, payload))  # type: ignore[assignment]

            exit_code = runner.main(
                ["--pid", "42", "--target-start-time-ticks", "12345"]
            )
        finally:
            runner.ensure_pid_alive = original_alive  # type: ignore[assignment]
            runner.TARGET_IDENTITY_TIMEOUT_SECONDS = original_timeout
            runner.emit = original_emit  # type: ignore[assignment]

        self.assertEqual(exit_code, 2)
        self.assertEqual([kind for kind, _ in emitted], ["status", "fatal"])
        self.assertEqual(emitted[-1][1]["code"], "android_target_identity_unverified")
        self.assertIn("连续无法确认", str(emitted[-1][1]["message"]))

    def test_target_confirmation_is_emitted_before_android_metric_workers_start(self) -> None:
        events: list[str] = []

        class FakeThread:
            def __init__(self, target=None, args=(), daemon=None):
                self.args = args

            def start(self) -> None:
                events.append("start:" + str(self.args[0]))

            def join(self, timeout=None) -> None:
                return None

        states = iter([True, False])
        original_thread = runner.threading.Thread
        original_alive = runner.ensure_pid_alive
        original_emit = runner.emit
        try:
            runner.threading.Thread = FakeThread  # type: ignore[assignment]
            runner.ensure_pid_alive = lambda *_: next(states)  # type: ignore[assignment]
            runner.emit = lambda kind, _payload: events.append("emit:" + kind)  # type: ignore[assignment]

            exit_code = runner.main(
                ["--pid", "42", "--target-start-time-ticks", "12345", "--interval", "0"]
            )
        finally:
            runner.threading.Thread = original_thread  # type: ignore[assignment]
            runner.ensure_pid_alive = original_alive  # type: ignore[assignment]
            runner.emit = original_emit  # type: ignore[assignment]

        self.assertEqual(exit_code, 2)
        target_index = events.index("emit:target")
        self.assertLess(target_index, events.index("start:Android FPS"))
        self.assertLess(target_index, events.index("start:Android PID CPU"))
        self.assertLess(target_index, events.index("start:Android PID 内存"))
        self.assertLess(target_index, events.index("start:Android device thermal metrics"))

    def test_disabled_metric_sources_do_not_start_worker_threads(self) -> None:
        started: list[str] = []

        class FakeThread:
            def __init__(self, target=None, args=(), daemon=None):
                self.args = args

            def start(self) -> None:
                started.append(str(self.args[0]))

            def join(self, timeout=None) -> None:
                return None

        original_thread = runner.threading.Thread
        original_alive = runner.ensure_pid_alive
        original_start_time = runner.read_process_start_time
        original_emit = runner.emit
        try:
            runner.threading.Thread = FakeThread  # type: ignore[assignment]
            liveness = iter([True, False])
            runner.ensure_pid_alive = lambda *_: next(liveness)  # type: ignore[assignment]
            runner.read_process_start_time = lambda *_: 12345  # type: ignore[assignment]
            runner.emit = lambda *_: None  # type: ignore[assignment]

            exit_code = runner.main([
                "--pid", "42", "--interval", "0", "--no-fps", "--no-memory",
                "--no-temperature", "--no-thermal-state",
            ])
        finally:
            runner.threading.Thread = original_thread  # type: ignore[assignment]
            runner.ensure_pid_alive = original_alive  # type: ignore[assignment]
            runner.read_process_start_time = original_start_time  # type: ignore[assignment]
            runner.emit = original_emit  # type: ignore[assignment]

        self.assertEqual(exit_code, 2)
        self.assertEqual(started, ["Android PID CPU"])

    def test_thermal_worker_starts_when_either_temperature_or_status_is_enabled(self) -> None:
        class FakeThread:
            def __init__(self, target=None, args=(), daemon=None):
                self.args = args

            def start(self) -> None:
                started.append(str(self.args[0]))

            def join(self, timeout=None) -> None:
                return None

        for disabled_flag in ("--no-temperature", "--no-thermal-state"):
            with self.subTest(disabled_flag=disabled_flag):
                started: list[str] = []
                original_thread = runner.threading.Thread
                original_alive = runner.ensure_pid_alive
                original_emit = runner.emit
                try:
                    runner.threading.Thread = FakeThread  # type: ignore[assignment]
                    liveness = iter([True, False])
                    runner.ensure_pid_alive = lambda *_: next(liveness)  # type: ignore[assignment]
                    runner.emit = lambda *_: None  # type: ignore[assignment]
                    exit_code = runner.main([
                        "--pid", "42", "--target-start-time-ticks", "12345",
                        "--no-fps", "--no-cpu", "--no-memory", disabled_flag,
                    ])
                finally:
                    runner.threading.Thread = original_thread  # type: ignore[assignment]
                    runner.ensure_pid_alive = original_alive  # type: ignore[assignment]
                    runner.emit = original_emit  # type: ignore[assignment]

                self.assertEqual(exit_code, 2)
                self.assertEqual(started, ["Android device thermal metrics"])

    def test_device_temperature_prefers_thermalservice_and_falls_back_to_battery(self) -> None:
        original_adb = runner.adb
        calls: list[list[str]] = []

        def fake_adb(_serial, args, timeout=0):
            calls.append(args)
            if args[-1] == "thermalservice":
                return type(
                    "Result",
                    (),
                    {
                        "returncode": 0,
                        "stdout": (
                            "Temperature{mValue=41.5, mType=0, mName=cpu0}\n"
                            "Temperature{mValue=46.25, mType=0, mName=cpu7}\n"
                            "Temperature{mValue=39.0, mType=1, mName=gpu}\n"
                        ),
                    },
                )()
            return type("Result", (), {"returncode": 0, "stdout": "temperature: 328\n"})()

        try:
            runner.adb = fake_adb  # type: ignore[assignment]
            values, source = runner.read_device_temperatures("serial")
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertEqual(values, {"CPU": 46.25, "GPU": 39.0, "Battery": 32.8})
        self.assertEqual(source, "adb-dumpsys-thermalservice+adb-dumpsys-battery")
        self.assertEqual(calls[0][-1], "thermalservice")
        self.assertEqual(calls[1][-1], "battery")

    def test_device_thermal_query_reuses_thermalservice_for_temperature_and_status(self) -> None:
        original_adb = runner.adb
        calls: list[list[str]] = []

        def fake_adb(_serial, args, timeout=0):
            calls.append(args)
            return type(
                "Result",
                (),
                {
                    "returncode": 0,
                    "stdout": (
                        "Thermal Status: 4\n"
                        "Temperature{mValue=41.5, mType=0, mName=cpu0, mStatus=0}\n"
                        "Temperature{mValue=33.8, mType=2, mName=battery, mStatus=0}\n"
                    ),
                },
            )()

        try:
            runner.adb = fake_adb  # type: ignore[assignment]
            values, source, thermal_status = runner.read_device_thermal_metrics(
                "serial", collect_temperature=True
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertEqual(values, {"CPU": 41.5, "Battery": 33.8})
        self.assertEqual(source, "adb-dumpsys-thermalservice")
        self.assertEqual(thermal_status, (4, "critical"))
        self.assertEqual(calls, [["shell", "dumpsys", "thermalservice"]])

    def test_thermal_status_does_not_query_battery_when_temperature_is_disabled(self) -> None:
        original_adb = runner.adb
        calls: list[list[str]] = []

        def fake_adb(_serial, args, timeout=0):
            calls.append(args)
            return type("Result", (), {"returncode": 0, "stdout": "Thermal Status: 2\n"})()

        try:
            runner.adb = fake_adb  # type: ignore[assignment]
            values, source, thermal_status = runner.read_device_thermal_metrics(
                "serial", collect_temperature=False
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertEqual(values, {})
        self.assertEqual(source, "")
        self.assertEqual(thermal_status, (2, "moderate"))
        self.assertEqual(calls, [["shell", "dumpsys", "thermalservice"]])

    def test_thermal_worker_can_emit_status_without_temperature(self) -> None:
        emitted: list[tuple[str, dict[str, object]]] = []

        class OneCycleStop:
            stopped = False

            def is_set(self) -> bool:
                return self.stopped

            def wait(self, _timeout: float) -> bool:
                self.stopped = True
                return True

        original_read = runner.read_device_thermal_metrics
        original_emit = runner.emit
        try:
            runner.read_device_thermal_metrics = lambda *_args, **_kwargs: (  # type: ignore[assignment]
                {},
                "",
                (3, "severe"),
            )
            runner.emit = lambda kind, payload: emitted.append((kind, payload))  # type: ignore[assignment]
            runner.temperature_loop(
                "Android device thermal metrics",
                "serial",
                1.0,
                OneCycleStop(),  # type: ignore[arg-type]
                collect_temperature=False,
                collect_thermal_state=True,
            )
        finally:
            runner.read_device_thermal_metrics = original_read  # type: ignore[assignment]
            runner.emit = original_emit  # type: ignore[assignment]

        self.assertEqual([kind for kind, _ in emitted], ["thermal_state"])
        self.assertEqual(
            emitted[0][1],
            {
                "platform": "android",
                "value": 3,
                "state": "severe",
                "source": "adb-dumpsys-thermalservice",
                "scope": "device",
                "semantics": "android_power_manager_thermal_status",
            },
        )

    def test_invalid_pid_does_not_start_any_metric_source(self) -> None:
        started: list[str] = []

        class FakeThread:
            def __init__(self, target=None, args=(), daemon=None):
                self.args = args

            def start(self) -> None:
                started.append(str(self.args[0]))

            def join(self, timeout=None) -> None:
                return None

        original_thread = runner.threading.Thread
        original_alive = runner.ensure_pid_alive
        original_emit = runner.emit
        try:
            runner.threading.Thread = FakeThread  # type: ignore[assignment]
            runner.ensure_pid_alive = lambda *_: False  # type: ignore[assignment]
            runner.emit = lambda *_: None  # type: ignore[assignment]

            exit_code = runner.main(["--pid", "42"])
        finally:
            runner.threading.Thread = original_thread  # type: ignore[assignment]
            runner.ensure_pid_alive = original_alive  # type: ignore[assignment]
            runner.emit = original_emit  # type: ignore[assignment]

        self.assertEqual(exit_code, 2)
        self.assertEqual(started, [])

    def test_guarded_metric_loop_reports_metric_degradation_without_stopping_all_sources(self) -> None:
        emitted: list[tuple[str, dict[str, object]]] = []
        stop_event = threading.Event()
        failure_event = threading.Event()
        failure_lock = threading.Lock()
        original_emit = runner.emit
        runner.emit = lambda kind, payload: emitted.append((kind, payload))  # type: ignore[assignment]

        def failing_source() -> None:
            raise RuntimeError("surface parser crashed")

        try:
            runner.guarded_metric_loop(
                "Android FPS",
                failing_source,
                (),
                stop_event,
                failure_event,
                failure_lock,
            )
        finally:
            runner.emit = original_emit  # type: ignore[assignment]

        self.assertFalse(stop_event.is_set())
        self.assertFalse(failure_event.is_set())
        self.assertEqual([kind for kind, _ in emitted], ["status"])
        self.assertNotIn("已停止", str(emitted[0][1]["message"]))
        self.assertIn("surface parser crashed", str(emitted[0][1]["message"]))

    def test_guarded_metric_loop_does_not_report_normal_requested_stop(self) -> None:
        emitted: list[tuple[str, dict[str, object]]] = []
        stop_event = threading.Event()
        failure_event = threading.Event()
        original_emit = runner.emit
        runner.emit = lambda kind, payload: emitted.append((kind, payload))  # type: ignore[assignment]

        def stopped_source() -> None:
            stop_event.set()

        try:
            runner.guarded_metric_loop(
                "Android CPU",
                stopped_source,
                (),
                stop_event,
                failure_event,
                threading.Lock(),
            )
        finally:
            runner.emit = original_emit  # type: ignore[assignment]

        self.assertFalse(failure_event.is_set())
        self.assertEqual(emitted, [])

    def test_adb_timeout_is_a_missing_sample_not_a_dead_fps_thread(self) -> None:
        original_run = runner.subprocess.run
        try:
            runner.subprocess.run = lambda *_args, **_kwargs: (_ for _ in ()).throw(  # type: ignore[assignment]
                runner.subprocess.TimeoutExpired(["adb"], 1.0)
            )
            result = runner.adb("serial", ["shell", "dumpsys", "SurfaceFlinger"], timeout=1.0)
        finally:
            runner.subprocess.run = original_run  # type: ignore[assignment]

        self.assertEqual(result.returncode, 124)
        self.assertIn("timed out", result.stderr.lower())

    def test_pid_liveness_distinguishes_dead_pid_from_transient_adb_failure(self) -> None:
        responses = iter(
            [
                type("Result", (), {"returncode": 124, "stdout": "", "stderr": "adb timed out"})(),
                type("Result", (), {"returncode": 1, "stdout": "cat: /proc/42/stat: No such file or directory", "stderr": ""})(),
                type("Result", (), {"returncode": 0, "stdout": "42 (game) S ...", "stderr": ""})(),
            ]
        )
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: next(responses)  # type: ignore[assignment]
            self.assertIsNone(runner.ensure_pid_alive("", 42))
            self.assertFalse(runner.ensure_pid_alive("", 42))
            self.assertTrue(runner.ensure_pid_alive("", 42))
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

    def test_pid_identity_quotes_the_sh_script_as_one_adb_argument(self) -> None:
        captured: list[str] = []
        fields = ["S"] + ["0"] * 18 + ["123456"] + ["0"] * 4
        identity_output = "com.example.game\0\n42 (game) " + " ".join(fields) + "\n"
        original_adb = runner.adb
        try:
            def fake_adb(_serial, args, timeout=0):
                captured.extend(args)
                return type(
                    "Result",
                    (),
                    {"returncode": 0, "stdout": identity_output, "stderr": ""},
                )()

            runner.adb = fake_adb  # type: ignore[assignment]
            self.assertTrue(runner.ensure_pid_alive("serial", 42, "com.example.game", 123456))
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        command = "cat /proc/42/cmdline; printf '\\n'; cat /proc/42/stat"
        self.assertEqual(captured, ["shell", "sh", "-c", runner.shlex.quote(command)])

    def test_unknown_pid_identity_keeps_the_last_adb_diagnostic(self) -> None:
        diagnostics: list[str] = []
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: type(  # type: ignore[assignment]
                "Result",
                (),
                {"returncode": 124, "stdout": "", "stderr": "adb timed out after 3.0s"},
            )()

            state = runner.ensure_pid_alive("serial", 42, "com.example.game", 100, diagnostics)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNone(state)
        self.assertEqual(len(diagnostics), 1)
        self.assertIn("identity-probe exit=124", diagnostics[0])
        self.assertIn("timed out after 3.0s", diagnostics[0])

    def test_pid_liveness_rejects_a_reused_pid_with_different_cmdline(self) -> None:
        responses = iter(
            [
                type("Result", (), {"returncode": 0, "stdout": "com.example.game\0", "stderr": ""})(),
                type("Result", (), {"returncode": 0, "stdout": "com.other.app\0", "stderr": ""})(),
            ]
        )
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: next(responses)  # type: ignore[assignment]
            self.assertTrue(runner.ensure_pid_alive("", 42, "com.example.game"))
            self.assertFalse(runner.ensure_pid_alive("", 42, "com.example.game"))
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

    def test_proc_start_time_parser_handles_nested_parentheses(self) -> None:
        fields = ["S"] + ["0"] * 18 + ["123456"] + ["0"] * 4
        stat = "42 (game (render) thread) " + " ".join(fields)

        self.assertEqual(runner.parse_proc_start_time(stat), 123456)

    def test_pid_identity_rejects_same_name_with_changed_start_time(self) -> None:
        def identity_output(start_time: int) -> str:
            fields = ["S"] + ["0"] * 18 + [str(start_time)] + ["0"] * 4
            return "com.example.game\0\n42 (game) " + " ".join(fields) + "\n"

        responses = iter(
            [
                type("Result", (), {"returncode": 0, "stdout": identity_output(100), "stderr": ""})(),
                type("Result", (), {"returncode": 0, "stdout": identity_output(200), "stderr": ""})(),
            ]
        )
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: next(responses)  # type: ignore[assignment]
            self.assertTrue(runner.ensure_pid_alive("", 42, "com.example.game", 100))
            self.assertFalse(runner.ensure_pid_alive("", 42, "com.example.game", 100))
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

    def test_pid_identity_uses_ps_when_cmdline_is_permission_denied(self) -> None:
        responses = iter(
            [
                type("Result", (), {"returncode": 1, "stdout": "", "stderr": "permission denied"})(),
                type(
                    "Result",
                    (),
                    {"returncode": 0, "stdout": "42 com.other.app\n", "stderr": ""},
                )(),
                type("Result", (), {"returncode": 1, "stdout": "", "stderr": "permission denied"})(),
                type(
                    "Result",
                    (),
                    {"returncode": 0, "stdout": "42 com.example.game\n", "stderr": ""},
                )(),
            ]
        )
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: next(responses)  # type: ignore[assignment]
            self.assertFalse(runner.ensure_pid_alive("", 42, "com.example.game"))
            self.assertTrue(runner.ensure_pid_alive("", 42, "com.example.game"))
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

    def test_pid_identity_stays_unverified_when_only_liveness_is_known(self) -> None:
        responses = iter(
            [
                type("Result", (), {"returncode": 1, "stdout": "", "stderr": "permission denied"})(),
                type("Result", (), {"returncode": 1, "stdout": "", "stderr": "permission denied"})(),
                type("Result", (), {"returncode": 0, "stdout": "42 (game) S ...", "stderr": ""})(),
            ]
        )
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: next(responses)  # type: ignore[assignment]
            self.assertIsNone(runner.ensure_pid_alive("", 42, "com.example.game"))
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

    def test_surface_flinger_layer_list_prefers_appbrand_surface(self) -> None:
        output = """
com.tencent.mm/com.tencent.mm.ui.LauncherUI#0
SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0
com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0
"""

        layers = runner.ranked_surface_layers(output, "com.tencent.mm:appbrand0")

        self.assertEqual(layers[0], "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0")

    def test_surface_flinger_layer_candidates_exclude_unrelated_surface_views(self) -> None:
        output = """
SurfaceView[NotificationShade](BLAST)#1
SurfaceView[com.example.game/.MainActivity](BLAST)#2
SurfaceView[com.other.game/.MainActivity](BLAST)#3
"""

        layers = runner.ranked_surface_layers(output, "com.example.game")

        self.assertEqual(layers, ["SurfaceView[com.example.game/.MainActivity](BLAST)#2"])

    def test_hwc_fps_parser_supports_nonzero_foldable_display(self) -> None:
        output = """
Display 1 HWC layers:
SurfaceView[com.example.game/.MainActivity]
0 | 0x1234 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 58 | 0
h/w composer state:
"""

        self.assertEqual(
            runner.collect_hwc_layer_fps(output),
            [("SurfaceView[com.example.game/.MainActivity]", 58)],
        )

    def test_surface_flinger_layer_list_unwraps_android_16_requested_layer_state(self) -> None:
        output = """
RequestedLayerState{Window:abc com.example.game/.MainActivity#10 parentId=2}
RequestedLayerState{9c968ad SurfaceView[com.example.game/.MainActivity](BLAST)#11 parentId=10 relativeParentId=9 z=-2}
"""

        layers = runner.ranked_surface_layers(output, "com.example.game")

        self.assertEqual(layers[0], "9c968ad SurfaceView[com.example.game/.MainActivity](BLAST)#11")
        self.assertNotIn("RequestedLayerState", layers[0])
        self.assertNotIn("parentId=", layers[0])

    def test_surface_layer_owner_parser_keeps_active_pid_and_uid(self) -> None:
        output = (
            "\n  \u2514\u2500 com.example.game 07-10 17:10:01.627#591 pid=5324 uid=10096"
            "\n  \u2502  \u2514\u2500 9c968ad SurfaceView[com.example.game/.MainActivity](BLAST)#625"
            " parent=624 pid=8850 uid=10182\n"
        )

        owners = runner.parse_surface_layer_owners(output)

        self.assertEqual(
            owners,
            [
                runner.SurfaceLayerOwner(
                    "com.example.game 07-10 17:10:01.627#591",
                    5324,
                    10096,
                ),
                runner.SurfaceLayerOwner(
                    "9c968ad SurfaceView[com.example.game/.MainActivity](BLAST)#625",
                    8850,
                    10182,
                ),
            ],
        )

    def test_android_10_composition_layer_parser_uses_app_id_as_owner_uid(self) -> None:
        output = """
* compositionengine::Layer 0x718d052f98 (SurfaceView - com.example.game/.MainActivity#0)
    frontend:
      isSecure=false
      type=1 appId=10182 composition type=DEVICE (2)
      buffer: buffer=0x716d829540 slot=2
* compositionengine::Layer 0x718d054398 (com.example.game/.MainActivity#0)
    frontend:
      type=0 appId=0 composition type=DEVICE (2)
"""

        owners = runner.parse_surface_layer_owners(output)

        self.assertIn(
            runner.SurfaceLayerOwner(
                "SurfaceView - com.example.game/.MainActivity#0",
                0,
                10182,
            ),
            owners,
        )

    def test_android_10_appbrand_activity_parser_maps_ui_component_to_exact_process(self) -> None:
        output = """
    * TaskRecord{80669d #14 A=.AppBrandUI1 U=0 DislayId=0 StackId=12 sz=1}
      mActivityComponent=com.tencent.mm/.plugin.appbrand.ui.AppBrandUI1
      mRootProcess=ProcessRecord{7f8d5f1 18936:com.tencent.mm:appbrand1/u0a243}
      * Hist #0: ActivityRecord{4a062fc u0 com.tencent.mm/.plugin.appbrand.ui.AppBrandUI1 d0 s12 t14}
          packageName=com.tencent.mm processName=com.tencent.mm:appbrand1
          app=ProcessRecord{7f8d5f1 18936:com.tencent.mm:appbrand1/u0a243}
    * TaskRecord{5516baa #6 A=com.tencent.mm U=0 DislayId=0 StackId=4 sz=1}
      mRootProcess=ProcessRecord{1b6eacc 14139:com.tencent.mm/u0a243}
"""

        self.assertEqual(
            runner.parse_appbrand_activity_bindings(output),
            [
                runner.AppBrandActivityBinding(
                    "AppBrandUI1",
                    18936,
                    "com.tencent.mm:appbrand1",
                )
            ],
        )

    def test_android_10_appbrand_owner_join_requires_unique_component_pid_and_process(self) -> None:
        owner = runner.SurfaceLayerOwner(
            "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI1#0",
            0,
            10243,
        )
        selected = runner.AppBrandActivityBinding(
            "AppBrandUI1",
            18936,
            "com.tencent.mm:appbrand1",
        )

        self.assertEqual(
            runner.bind_appbrand_activity_owners(
                [owner],
                [selected],
                "com.tencent.mm:appbrand1",
                18936,
                10243,
            ),
            [runner.SurfaceLayerOwner(owner.name, 18936, 10243)],
        )
        self.assertEqual(
            runner.bind_appbrand_activity_owners(
                [owner],
                [selected],
                "com.tencent.mm:appbrand2",
                18936,
                10243,
            ),
            [owner],
        )
        self.assertEqual(
            runner.bind_appbrand_activity_owners(
                [owner],
                [
                    selected,
                    runner.AppBrandActivityBinding(
                        "AppBrandUI1",
                        19999,
                        "com.tencent.mm:appbrand2",
                    ),
                ],
                "com.tencent.mm:appbrand1",
                18936,
                10243,
            ),
            [owner],
        )

    def test_surface_owner_binding_uses_uid_for_native_but_pid_for_appbrand(self) -> None:
        native_child = runner.SurfaceLayerOwner(
            "SurfaceView[com.example.game/.MainActivity]#11",
            9900,
            10182,
        )
        appbrand_sibling = runner.SurfaceLayerOwner(
            "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#12",
            2002,
            10123,
        )

        self.assertTrue(
            runner.surface_owner_matches_target(native_child, 8850, 10182, "com.example.game")
        )
        legacy_native = runner.SurfaceLayerOwner(
            "SurfaceView - com.example.game/.MainActivity#0",
            0,
            10182,
        )
        self.assertTrue(
            runner.surface_owner_matches_target(legacy_native, 8850, 10182, "com.example.game")
        )
        self.assertFalse(
            runner.surface_owner_matches_target(
                legacy_native,
                2001,
                10182,
                "com.tencent.mm:appbrand1",
            )
        )
        self.assertFalse(
            runner.surface_owner_matches_target(
                appbrand_sibling,
                2001,
                10123,
                "com.tencent.mm:appbrand1",
            )
        )
        self.assertTrue(
            runner.surface_owner_matches_target(
                appbrand_sibling,
                2002,
                10123,
                "com.tencent.mm:appbrand2",
            )
        )

    def test_layer_owner_uid_probe_retries_after_transient_failure(self) -> None:
        original_adb = runner.adb
        original_read_uid = runner.read_process_uid
        original_time = runner.time.monotonic
        uid_results = iter([None, 10182])
        uid_calls: list[int] = []

        def fake_read_uid(_serial: str, pid: int) -> int | None:
            uid_calls.append(pid)
            return next(uid_results)

        try:
            runner.adb = lambda *_args, **_kwargs: type(  # type: ignore[assignment]
                "Result",
                (),
                {"returncode": 0, "stdout": ""},
            )()
            runner.read_process_uid = fake_read_uid  # type: ignore[assignment]
            runner.time.monotonic = lambda: 100.0  # type: ignore[method-assign]
            tracker = runner.LayerLatencyTracker(target_pid=8850)

            runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
            )
            self.assertFalse(tracker.target_uid_checked)
            self.assertEqual(tracker.target_uid, 0)

            runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.read_process_uid = original_read_uid  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertEqual(uid_calls, [8850, 8850])
        self.assertTrue(tracker.target_uid_checked)
        self.assertEqual(tracker.target_uid, 10182)

    def test_process_uid_parser_uses_the_real_uid_column(self) -> None:
        status = "Name:\tgame\nUid:\t10182\t10182\t10182\t10182\nGid:\t10182\n"

        self.assertEqual(runner.parse_process_uid(status), 10182)

    def test_layer_latency_rejects_name_matched_surface_owned_by_launcher(self) -> None:
        owner_output = "\n  \u2514\u2500 com.example.game 07-10 17:10:01.627#591 pid=5324 uid=10096\n"
        latency_calls: list[str] = []
        original_adb = runner.adb
        try:
            def fake_adb(_serial, args, **_kwargs):
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 0, "stdout": owner_output})()
                if "--latency" in args:
                    latency_calls.append(args[-1])
                    return type(
                        "Result",
                        (),
                        {"returncode": 0, "stdout": "16666667\n1 1000000000 2\n"},
                    )()
                raise AssertionError(args)

            runner.adb = fake_adb  # type: ignore[assignment]
            tracker = runner.LayerLatencyTracker()
            samples, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
                target_pid=8850,
                target_uid=10182,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertFalse(available)
        self.assertEqual(samples, [])
        self.assertEqual(latency_calls, [])
        self.assertEqual(tracker.selected_layer, "")

    def test_layer_latency_selects_active_surface_owned_by_target_process(self) -> None:
        owner_output = (
            "\n  \u2514\u2500 com.example.game 07-10 17:10:01.627#591 pid=5324 uid=10096"
            "\n  \u2502  \u2514\u2500 9c968ad SurfaceView[com.example.game/.MainActivity](BLAST)#625"
            " parent=624 pid=8850 uid=10182\n"
        )
        original_adb = runner.adb
        try:
            def fake_adb(_serial, args, **_kwargs):
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 0, "stdout": owner_output})()
                if "--latency" in args:
                    return type(
                        "Result",
                        (),
                        {
                            "returncode": 0,
                            "stdout": "16666667\n1 1000000000 2\n1 1016666667 2\n",
                        },
                    )()
                raise AssertionError(args)

            runner.adb = fake_adb  # type: ignore[assignment]
            tracker = runner.LayerLatencyTracker()
            samples, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
                target_pid=8850,
                target_uid=10182,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertTrue(available)
        self.assertEqual(samples, [])
        self.assertIn("SurfaceView[com.example.game/.MainActivity]", tracker.selected_layer)
        self.assertEqual(tracker.selected_owner_pid, 8850)
        self.assertEqual(tracker.selected_owner_uid, 10182)
        self.assertTrue(tracker.target_verified)

    def test_android_10_layer_latency_selects_native_surface_by_app_id(self) -> None:
        layer = "SurfaceView - com.example.game/.MainActivity#0"
        owner_output = (
            f"\n* compositionengine::Layer 0x718d052f98 ({layer})"
            "\n    frontend:"
            "\n      type=1 appId=10182 composition type=DEVICE (2)\n"
        )
        original_adb = runner.adb
        try:
            def fake_adb(_serial, args, **_kwargs):
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 0, "stdout": owner_output})()
                if "--latency" in args:
                    return type(
                        "Result",
                        (),
                        {
                            "returncode": 0,
                            "stdout": "16666667\n1 1000000000 2\n1 1016666667 2\n",
                        },
                    )()
                raise AssertionError(args)

            runner.adb = fake_adb  # type: ignore[assignment]
            tracker = runner.LayerLatencyTracker()
            samples, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
                target_pid=8850,
                target_uid=10182,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertTrue(available)
        self.assertEqual(samples, [])
        self.assertEqual(tracker.selected_layer, layer)
        self.assertEqual(tracker.selected_owner_pid, 0)
        self.assertEqual(tracker.selected_owner_uid, 10182)
        self.assertTrue(tracker.target_verified)

    def test_android_10_layer_latency_verifies_appbrand_surface_through_activity_pid(self) -> None:
        layer = "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI1#0"
        owner_output = (
            f"\n* compositionengine::Layer 0x718d052f98 ({layer})"
            "\n    frontend:"
            "\n      type=1 appId=10243 composition type=DEVICE (2)\n"
        )
        activity_output = """
    * TaskRecord{80669d #14 A=.AppBrandUI1 U=0 DislayId=0 StackId=12 sz=1}
      mActivityComponent=com.tencent.mm/.plugin.appbrand.ui.AppBrandUI1
      mRootProcess=ProcessRecord{7f8d5f1 18936:com.tencent.mm:appbrand1/u0a243}
"""
        calls: list[list[str]] = []
        original_adb = runner.adb
        try:
            def fake_adb(_serial, args, **_kwargs):
                calls.append(args)
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 0, "stdout": owner_output})()
                if args[-2:] == ["activity", "recents"]:
                    return type("Result", (), {"returncode": 0, "stdout": activity_output})()
                if "--latency" in args:
                    return type(
                        "Result",
                        (),
                        {
                            "returncode": 0,
                            "stdout": "16666667\n1 1000000000 2\n1 1016666667 2\n",
                        },
                    )()
                raise AssertionError(args)

            runner.adb = fake_adb  # type: ignore[assignment]
            tracker = runner.LayerLatencyTracker()
            samples, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.tencent.mm:appbrand1",
                tracker,
                target_pid=18936,
                target_uid=10243,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertTrue(available)
        self.assertEqual(samples, [])
        self.assertEqual(tracker.selected_layer, layer)
        self.assertEqual(tracker.selected_owner_pid, 18936)
        self.assertEqual(tracker.selected_owner_uid, 10243)
        self.assertTrue(tracker.target_verified)
        self.assertIn(["shell", "dumpsys", "activity", "recents"], calls)

    def test_layer_latency_prefers_main_surface_when_lower_ranked_layer_is_only_one_frame_newer(self) -> None:
        main_layer = "SurfaceView[com.example.game/.MainActivity](BLAST)#625"
        overlay_layer = "com.example.game Overlay#626"
        owner_output = (
            f"\n  \u2514\u2500 {main_layer} pid=8850 uid=10182"
            f"\n  \u2514\u2500 {overlay_layer} pid=8850 uid=10182\n"
        )
        original_adb = runner.adb
        original_read_latency = runner._read_layer_latency
        try:
            def fake_adb(_serial, args, **_kwargs):
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 0, "stdout": owner_output})()
                raise AssertionError(args)

            def fake_read_latency(_serial, layer):
                latest = 1_016_666_667 if "SurfaceView" in layer else 1_033_333_334
                return True, 16_666_667, [latest - 16_666_667, latest]

            runner.adb = fake_adb  # type: ignore[assignment]
            runner._read_layer_latency = fake_read_latency  # type: ignore[assignment]
            tracker = runner.LayerLatencyTracker(
                target_pid=8850,
                target_uid=10182,
                target_uid_checked=True,
            )

            _, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner._read_layer_latency = original_read_latency  # type: ignore[assignment]

        self.assertTrue(available)
        self.assertEqual(tracker.selected_layer, main_layer)

    def test_layer_latency_switches_when_main_surface_is_more_than_one_second_stale(self) -> None:
        main_layer = "SurfaceView[com.example.game/.MainActivity](BLAST)#625"
        active_layer = "com.example.game RenderOverlay#626"
        owner_output = (
            f"\n  \u2514\u2500 {main_layer} pid=8850 uid=10182"
            f"\n  \u2514\u2500 {active_layer} pid=8850 uid=10182\n"
        )
        original_adb = runner.adb
        original_read_latency = runner._read_layer_latency
        try:
            def fake_adb(_serial, args, **_kwargs):
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 0, "stdout": owner_output})()
                raise AssertionError(args)

            def fake_read_latency(_serial, layer):
                latest = 1_016_666_667 if "SurfaceView" in layer else 3_016_666_667
                return True, 16_666_667, [latest - 16_666_667, latest]

            runner.adb = fake_adb  # type: ignore[assignment]
            runner._read_layer_latency = fake_read_latency  # type: ignore[assignment]
            tracker = runner.LayerLatencyTracker(
                target_pid=8850,
                target_uid=10182,
                target_uid_checked=True,
            )

            _, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner._read_layer_latency = original_read_latency  # type: ignore[assignment]

        self.assertTrue(available)
        self.assertEqual(tracker.selected_layer, active_layer)

    def test_android_16_elided_owner_name_uses_full_requested_layer_by_id(self) -> None:
        full_layer = "7f3e08c SurfaceView[com.example.game/.MainActivity](BLAST)#811"
        owner_output = (
            "\ngarbled-tree 7f3e08c SurfaceView[com.example.g[...]ivity](BLAST)#811"
            " pid=8850 uid=10182\n"
        )
        listed_output = f"RequestedLayerState{{{full_layer} parentId=810}}\n"
        latency_calls: list[str] = []
        original_adb = runner.adb
        try:
            def fake_adb(_serial, args, **_kwargs):
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 0, "stdout": owner_output})()
                if args[-1] == "--list":
                    return type("Result", (), {"returncode": 0, "stdout": listed_output})()
                if "--latency" in args:
                    latency_calls.append(args[-1])
                    return type(
                        "Result",
                        (),
                        {
                            "returncode": 0,
                            "stdout": "16666667\n1 1000000000 2\n1 1016666667 2\n",
                        },
                    )()
                raise AssertionError(args)

            runner.adb = fake_adb  # type: ignore[assignment]
            tracker = runner.LayerLatencyTracker()
            samples, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
                target_pid=8850,
                target_uid=10182,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertTrue(available)
        self.assertEqual(samples, [])
        self.assertEqual(latency_calls, [runner.shlex.quote(full_layer)])
        self.assertEqual(tracker.selected_layer, full_layer)
        self.assertEqual(tracker.selected_owner_pid, 8850)
        self.assertEqual(tracker.selected_owner_uid, 10182)
        self.assertTrue(tracker.target_verified)

    def test_active_layer_is_still_revalidated_against_current_owner(self) -> None:
        old_layer = "SurfaceView[com.example.game/.OldActivity]#11"
        new_layer = "SurfaceView[com.example.game/.MainActivity]#12"
        owner_output = (
            "\n  \u2514\u2500 " + new_layer + " pid=8850 uid=10182\n"
        )
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            def fake_adb(_serial, args, **_kwargs):
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 0, "stdout": owner_output})()
                if "--latency" in args:
                    base = 2_000_000_000 if "MainActivity" in args[-1] else 1_000_000_000
                    return type(
                        "Result",
                        (),
                        {
                            "returncode": 0,
                            "stdout": f"16666667\n1 {base} 2\n1 {base + 16666667} 2\n",
                        },
                    )()
                raise AssertionError(args)

            runner.adb = fake_adb  # type: ignore[assignment]
            runner.time.monotonic = lambda: 100.0  # type: ignore[method-assign]
            tracker = runner.LayerLatencyTracker(
                selected_layer=old_layer,
                last_list_time=90.0,
                target_pid=8850,
                target_uid=10182,
                target_uid_checked=True,
                target_verified=True,
                selected_owner_pid=8850,
                selected_owner_uid=10182,
            )
            _, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertTrue(available)
        self.assertEqual(tracker.selected_layer, new_layer)
        self.assertEqual(tracker.selected_owner_pid, 8850)

    def test_owner_probe_failure_downgrades_current_window_before_emitting(self) -> None:
        layer = "SurfaceView[com.example.game/.MainActivity]#12"
        base = 1_000_000_000
        timestamps = [base + index * 16_666_667 for index in range(62)]
        latency_output = "16666667\n" + "\n".join(
            f"1 {timestamp} 2" for timestamp in timestamps
        )
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            def fake_adb(_serial, args, **_kwargs):
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 1, "stdout": "", "stderr": "timeout"})()
                if args[-1] == "--list":
                    return type("Result", (), {"returncode": 0, "stdout": layer + "\n", "stderr": ""})()
                if "--latency" in args:
                    return type("Result", (), {"returncode": 0, "stdout": latency_output, "stderr": ""})()
                raise AssertionError(args)

            runner.adb = fake_adb  # type: ignore[assignment]
            runner.time.monotonic = lambda: 100.0  # type: ignore[method-assign]
            tracker = runner.LayerLatencyTracker(
                selected_layer=layer,
                selected_state=runner.OrderedLayerState(
                    last_timestamp_ns=base,
                    last_new_frame_time=90.0,
                    accumulator=runner._ordered_layer_accumulator(base),
                ),
                last_list_time=90.0,
                target_pid=8850,
                target_uid=10182,
                target_uid_checked=True,
                target_verified=True,
                selected_owner_pid=8850,
                selected_owner_uid=10182,
            )
            samples, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertTrue(available)
        self.assertTrue(samples)
        self.assertTrue(all(sample.target_verified is False for sample in samples))
        self.assertTrue(all(sample.approximate for sample in samples))
        self.assertTrue(all(not sample.has_jank_metrics for sample in samples))
        self.assertTrue(all(sample.frame_time_ms is None for sample in samples))

    def test_unverified_name_only_layer_does_not_claim_exact_jank(self) -> None:
        timestamps = [1_000_000_000 + index * 16_666_667 for index in range(62)]
        state = runner.OrderedLayerState()
        runner.layer_latency_samples_from_timestamps(
            timestamps[:1],
            state,
            "SurfaceView[com.example.game]#11",
            16_666_667,
            now=100.0,
            target_verified=False,
        )

        samples = runner.layer_latency_samples_from_timestamps(
            timestamps,
            state,
            "SurfaceView[com.example.game]#11",
            16_666_667,
            now=101.1,
            target_verified=False,
        )

        self.assertEqual(len(samples), 1)
        self.assertTrue(samples[0].approximate)
        self.assertFalse(samples[0].has_jank_metrics)
        payload = runner.frame_sample_payload(
            samples[0],
            "adb-surfaceflinger-layer-latency",
            "surface",
            "ordered-layer-present",
        )
        self.assertFalse(payload["target_verified"])
        self.assertNotIn("jank", payload)
        self.assertNotIn("frame_time_ms", payload)

    def test_surface_flinger_layer_latency_quotes_remote_shell_metacharacters(self) -> None:
        captured: list[list[str]] = []
        original_adb = runner.adb
        try:
            def fake_adb(_serial, args, **_kwargs):
                captured.append(args)
                return type(
                    "Result",
                    (),
                    {"returncode": 0, "stdout": "16666667\n1 1000000000 2\n"},
                )()

            runner.adb = fake_adb  # type: ignore[assignment]
            available, refresh_ns, timestamps = runner._read_layer_latency(
                "",
                "SurfaceView[com.example.game/.MainActivity](BLAST)#11",
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertTrue(available)
        self.assertEqual(refresh_ns, 16_666_667)
        self.assertEqual(timestamps, [1_000_000_000])
        self.assertEqual(
            captured[0][-1],
            "'SurfaceView[com.example.game/.MainActivity](BLAST)#11'",
        )

    def test_surface_flinger_latency_parser_uses_actual_present_column_and_preserves_order(self) -> None:
        output = "\n".join(
            [
                "6944444",
                "90 100 1000",
                "110 120 1010",
                "110 120 1020",
                "100 110 1030",
                "130 9223372036854775807 140",
            ]
        )

        refresh_ns, timestamps = runner.parse_surface_flinger_latency(output)

        self.assertEqual(refresh_ns, 6_944_444)
        self.assertEqual(timestamps, [100, 120, 120, 110])

    def test_unparseable_present_timestamp_resets_the_interval_anchor(self) -> None:
        intervals, latest, duplicates, out_of_order, invalid = runner.ordered_present_intervals_ms(
            [1_016_000_000, object(), 1_032_000_000, 1_048_000_000],  # type: ignore[list-item]
            1_000_000_000,
        )

        self.assertEqual(intervals, [16.0])
        self.assertEqual(latest, 1_048_000_000)
        self.assertEqual(duplicates, 0)
        self.assertEqual(out_of_order, 0)
        self.assertEqual(invalid, 1)

    def test_layer_latency_refresh_period_without_timestamps_is_unavailable(self) -> None:
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: type(  # type: ignore[assignment]
                "Result",
                (),
                {"returncode": 0, "stdout": "16666667\n"},
            )()
            available, refresh_ns, timestamps = runner._read_layer_latency("", "SurfaceView[com.example.game]")
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertFalse(available)
        self.assertEqual(refresh_ns, 16_666_667)
        self.assertEqual(timestamps, [])

    def test_display_latency_zero_window_keeps_ordered_capability_metadata(self) -> None:
        original_time = runner.time.monotonic
        try:
            runner.time.monotonic = lambda: 12.0  # type: ignore[method-assign]
            sample = runner.latency_sample_from_timestamps(
                [1_000_000_000],
                runner.LatencyState(last_timestamp_ns=1_000_000_000, last_poll_time=10.0),
                refresh_period_ns=16_666_667,
            )
        finally:
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertEqual(sample.fps, 0.0)
        self.assertTrue(sample.ordered_frames)
        self.assertFalse(sample.approximate)
        self.assertEqual(sample.frame_count, 0)
        self.assertAlmostEqual(sample.window_sec, 2.0)
        self.assertEqual(sample.refresh_period_ns, 16_666_667)
        payload = runner.frame_sample_payload(
            sample,
            "adb-surfaceflinger-display-latency",
            "display",
            "ordered-display-present",
        )
        self.assertEqual(payload["frame_count"], 0)
        self.assertAlmostEqual(payload["window_sec"], 2.0)
        self.assertEqual(payload["jank"], 0.0)

    def test_display_latency_without_any_present_timestamp_is_unavailable(self) -> None:
        state = runner.LatencyState(last_poll_time=10.0)

        sample = runner.latency_sample_from_timestamps([], state, now=11.0)

        self.assertIsNone(sample)

    def test_display_latency_reports_duplicate_and_out_of_order_present_timestamps(self) -> None:
        state = runner.LatencyState(
            last_timestamp_ns=1_000_000_000,
            last_poll_time=10.0,
            recent_intervals_ms=[16.0, 16.0, 16.0],
        )

        sample = runner.latency_sample_from_timestamps(
            [
                1_016_000_000,
                1_016_000_000,
                1_008_000_000,
                1_032_000_000,
                1_048_000_000,
            ],
            state,
            now=11.0,
        )

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertEqual(sample.frame_count, 1)
        self.assertEqual(sample.duplicate_timestamps, 1)
        self.assertEqual(sample.out_of_order_timestamps, 1)
        self.assertTrue(sample.source_degraded)
        self.assertTrue(sample.approximate)
        self.assertFalse(sample.has_jank_metrics)
        self.assertIsNone(sample.frame_time_ms)
        payload = runner.frame_sample_payload(
            sample,
            "adb-surfaceflinger-display-latency",
            "display",
            "ordered-display-present",
        )
        self.assertNotIn("jank", payload)
        self.assertNotIn("big_jank", payload)
        self.assertNotIn("frame_time_ms", payload)

    def test_ordered_layer_latency_supports_144hz_without_fixed_rate_clamping(self) -> None:
        refresh_ns = 6_944_444
        baseline = 1_000_000_000
        state = runner.OrderedLayerState(
            last_timestamp_ns=baseline,
            last_poll_time=10.0,
            last_new_frame_time=10.0,
            accumulator=runner._ordered_layer_accumulator(baseline),
        )
        first_ring = [baseline + refresh_ns * index for index in range(1, 75)]
        self.assertEqual(
            runner.layer_latency_samples_from_timestamps(
                first_ring,
                state,
                "SurfaceView[com.example.game]",
                refresh_ns,
                now=10.55,
            ),
            [],
        )
        second_ring = [baseline + refresh_ns * index for index in range(19, 147)]

        samples = runner.layer_latency_samples_from_timestamps(
            second_ring,
            state,
            "SurfaceView[com.example.game]",
            refresh_ns,
            now=11.1,
            target_verified=True,
        )

        self.assertEqual(len(samples), 1)
        sample = samples[0]
        self.assertAlmostEqual(sample.fps, 144.0, places=2)
        self.assertTrue(sample.ordered_frames)
        self.assertFalse(sample.approximate)
        self.assertEqual(sample.refresh_period_ns, refresh_ns)
        self.assertIsNotNone(sample.source_elapsed_sec)
        self.assertAlmostEqual(sample.source_elapsed_sec or 0, 145 * refresh_ns / 1_000_000_000.0)
        self.assertEqual(sample.source_sequence, 1)
        payload = runner.frame_sample_payload(
            sample,
            "adb-surfaceflinger-layer-latency",
            "surface",
            "ordered-layer-present",
        )
        self.assertAlmostEqual(payload["source_elapsed_sec"], sample.source_elapsed_sec or 0)
        self.assertEqual(payload["source_sequence"], 1)

    def test_ordered_layer_ring_overrun_emits_degraded_fps_without_derived_metrics(self) -> None:
        refresh_ns = 4_166_667
        baseline = 1_000_000_000
        state = runner.OrderedLayerState(
            last_timestamp_ns=baseline,
            last_poll_time=10.0,
            last_new_frame_time=10.0,
            accumulator=runner._ordered_layer_accumulator(baseline),
        )
        overwritten_ring = [baseline + refresh_ns * index for index in range(1, 129)]

        samples = runner.layer_latency_samples_from_timestamps(
            overwritten_ring,
            state,
            "SurfaceView[com.example.game]",
            refresh_ns,
            now=10.8,
        )

        self.assertEqual(len(samples), 1)
        sample = samples[0]
        self.assertAlmostEqual(sample.fps, 240.0, places=2)
        self.assertEqual(sample.frame_count, 127)
        self.assertTrue(sample.ordered_frames)
        self.assertTrue(sample.approximate)
        self.assertTrue(sample.source_degraded)
        self.assertTrue(sample.ring_buffer_overrun)
        self.assertFalse(sample.has_jank_metrics)
        self.assertIsNone(sample.frame_time_ms)
        payload = runner.frame_sample_payload(
            sample,
            "adb-surfaceflinger-layer-latency",
            "surface",
            "ordered-layer-present",
        )
        self.assertTrue(payload["ring_buffer_overrun"])
        self.assertNotIn("jank", payload)
        self.assertNotIn("frame_time_ms", payload)

    def test_ordered_layer_verified_idle_resume_preserves_real_long_frame(self) -> None:
        refresh_ns = 16_666_667
        baseline = 1_000_000_000
        state = runner.OrderedLayerState(
            last_timestamp_ns=baseline,
            last_poll_time=10.0,
            last_new_frame_time=10.0,
            accumulator=runner._ordered_layer_accumulator(baseline),
        )
        active = [baseline + refresh_ns * index for index in range(1, 62)]
        first = runner.layer_latency_samples_from_timestamps(
            active,
            state,
            "SurfaceView[com.example.game]",
            refresh_ns,
            now=11.1,
        )
        self.assertEqual(len(first), 1)
        self.assertEqual(first[0].source_sequence, 1)

        zero = runner.layer_latency_samples_from_timestamps(
            active,
            state,
            "SurfaceView[com.example.game]",
            refresh_ns,
            now=12.2,
            target_verified=True,
        )
        self.assertEqual(len(zero), 1)
        self.assertEqual(zero[0].fps, 0.0)
        self.assertTrue(zero[0].idle_window)
        self.assertTrue(zero[0].no_present_frames)
        self.assertEqual(zero[0].source_sequence, 2)
        self.assertGreater(zero[0].source_elapsed_sec or 0, first[0].source_elapsed_sec or 0)

        resumed_start = active[-1] + 5_000_000_000
        resumed = [resumed_start + refresh_ns * index for index in range(0, 62)]
        after_resume = runner.layer_latency_samples_from_timestamps(
            resumed,
            state,
            "SurfaceView[com.example.game]",
            refresh_ns,
            now=13.3,
            target_verified=True,
        )

        self.assertGreaterEqual(len(after_resume), 1)
        gap_sample = max(after_resume, key=lambda sample: sample.frame_time_max_ms or 0)
        self.assertGreater(gap_sample.source_sequence or 0, zero[0].source_sequence or 0)
        self.assertGreater(gap_sample.source_elapsed_sec or 0, zero[0].source_elapsed_sec or 0)
        self.assertEqual(gap_sample.jank, 1)
        self.assertEqual(gap_sample.big_jank, 1)
        self.assertAlmostEqual(gap_sample.frame_time_max_ms or 0, 5000.0, places=3)
        self.assertAlmostEqual(gap_sample.resume_gap_ms or 0, 5000.0, places=3)

    def test_ordered_layer_transport_gap_still_rebases_without_jank(self) -> None:
        refresh_ns = 16_666_667
        baseline = 1_000_000_000
        state = runner.OrderedLayerState(
            last_timestamp_ns=baseline,
            last_poll_time=10.0,
            last_new_frame_time=10.0,
            accumulator=runner._ordered_layer_accumulator(baseline),
        )
        active = [baseline + refresh_ns * index for index in range(1, 62)]
        runner.layer_latency_samples_from_timestamps(
            active,
            state,
            "SurfaceView[com.example.game]",
            refresh_ns,
            now=11.1,
            target_verified=True,
        )
        runner.layer_latency_samples_from_timestamps(
            active,
            state,
            "SurfaceView[com.example.game]",
            refresh_ns,
            now=12.2,
            target_verified=True,
        )

        resumed_start = active[-1] + 5_000_000_000
        resumed = [resumed_start + refresh_ns * index for index in range(0, 62)]
        rebaseline = runner.layer_latency_samples_from_timestamps(
            resumed,
            state,
            "SurfaceView[com.example.game]",
            refresh_ns,
            now=16.5,
            target_verified=True,
        )
        self.assertEqual(rebaseline, [])

        follow_up = resumed + [resumed[-1] + refresh_ns * index for index in range(1, 62)]
        after_transport_gap = runner.layer_latency_samples_from_timestamps(
            follow_up,
            state,
            "SurfaceView[com.example.game]",
            refresh_ns,
            now=17.1,
            target_verified=True,
        )

        self.assertTrue(after_transport_gap)
        self.assertTrue(all(sample.resume_gap_ms is None for sample in after_transport_gap))
        self.assertTrue(all(sample.jank == 0 for sample in after_transport_gap))
        self.assertTrue(all(sample.big_jank == 0 for sample in after_transport_gap))
        self.assertLess(max(sample.frame_time_max_ms or 0 for sample in after_transport_gap), 20.0)

    def test_layer_latency_periodically_switches_from_stale_surface_to_newer_surface(self) -> None:
        old_layer = "SurfaceView - com.example.game/OldActivity#0"
        new_layer = "SurfaceView - com.example.game/NewActivity#0"
        tracker = runner.LayerLatencyTracker(
            selected_layer=old_layer,
            selected_state=runner.OrderedLayerState(
                last_timestamp_ns=100,
                last_new_frame_time=8.0,
                last_zero_emit_time=8.5,
                accumulator=runner._ordered_layer_accumulator(100),
            ),
            last_list_time=0.0,
        )
        probes = {
            old_layer: (True, 16_666_667, [100]),
            new_layer: (True, 16_666_667, [200]),
        }
        original_adb = runner.adb
        original_probe = runner._read_layer_latency
        original_time = runner.time.monotonic
        try:
            runner.time.monotonic = lambda: 10.0  # type: ignore[method-assign]
            runner.adb = lambda *_, **__: type(  # type: ignore[assignment]
                "Result",
                (),
                {"returncode": 0, "stdout": old_layer + "\n" + new_layer + "\n"},
            )()
            runner._read_layer_latency = lambda _, layer: probes[layer]  # type: ignore[assignment]

            samples, available = runner.read_surface_flinger_layer_latency_samples(
                "",
                "com.example.game",
                tracker,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner._read_layer_latency = original_probe  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertTrue(available)
        self.assertEqual(samples, [])
        self.assertEqual(tracker.selected_layer, new_layer)
        self.assertEqual(tracker.selected_state.last_timestamp_ns, 200)

    def test_verified_newer_surface_wins_even_with_a_lower_name_score(self) -> None:
        old_layer = "SurfaceView[com.example.game/.OldActivity](BLAST)#11"
        new_layer = "com.example.game/.MainActivity#12"
        owner_output = "\n".join(
            [
                f"  \u251c\u2500 {old_layer} pid=8850 uid=10182",
                f"  \u2514\u2500 {new_layer} pid=8850 uid=10182",
            ]
        )
        probes = {
            old_layer: (True, 16_666_667, [100]),
            new_layer: (True, 16_666_667, [200]),
        }
        original_adb = runner.adb
        original_probe = runner._read_layer_latency
        original_time = runner.time.monotonic
        try:
            runner.time.monotonic = lambda: 10.0  # type: ignore[method-assign]

            def fake_adb(_serial, args, **_kwargs):
                if args[-1] == "--layers":
                    return type("Result", (), {"returncode": 0, "stdout": owner_output})()
                raise AssertionError(args)

            runner.adb = fake_adb  # type: ignore[assignment]
            runner._read_layer_latency = lambda _, layer: probes[layer]  # type: ignore[assignment]
            tracker = runner.LayerLatencyTracker(
                selected_layer=old_layer,
                selected_state=runner.OrderedLayerState(
                    last_timestamp_ns=100,
                    last_new_frame_time=8.0,
                    accumulator=runner._ordered_layer_accumulator(100),
                ),
                last_list_time=0.0,
                target_pid=8850,
                target_uid=10182,
                target_uid_checked=True,
                target_verified=True,
                selected_owner_pid=8850,
                selected_owner_uid=10182,
            )
            _, available = runner.read_surface_flinger_layer_latency_samples(
                "serial",
                "com.example.game",
                tracker,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner._read_layer_latency = original_probe  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertTrue(available)
        self.assertEqual(tracker.selected_layer, new_layer)

    def test_surface_flinger_hwc_layer_fps_prefers_appbrand_surface(self) -> None:
        output = """
Display 0 HWC layers:
-------------------------------------------------------------------------------------------
 Layer name
           Z |  Window Type |  Comp Type |  Transform |   Disp Frame (LTRB) |          Source Crop (LTRB)
-------------------------------------------------------------------------------------------
 SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI1#0
  rel     -2 |            0 |     CLIENT |          0 |    0    0 1080 2400 |    0.0    0.0 1080.0 2400.0
 |     handle    |     fd     |  tr  | AFBC | dataSpace  |  format   |  blend  | planeAlpha | zOrder |          color         | fps | priority | windowIndex        |
 +---------------+------------+------+------+------------+-----------+---------+------------+--------+------------------------+-----+----------+--------------------+
 |  0x6fe9a6bdc0 | 78, 83, 84 | 0x 0 |   1  | 0x       0 | RGBA_8888 | 0x   1  |     1.0    |    0   | 0x 0, 0x 0, 0x 0, 0x 0 |  16 |     1    |    -1               |
 com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI1#0
  rel      0 |            1 |     CLIENT |          0 |    0    0 1080  238 |    0.0    0.0 1080.0  238.0
 |     handle    |     fd     |  tr  | AFBC | dataSpace  |  format   |  blend  | planeAlpha | zOrder |          color         | fps | priority | windowIndex        |
 +---------------+------------+------+------+------------+-----------+---------+------------+--------+------------------------+-----+----------+--------------------+
 |  0x6fe9a6c180 | 50, 51, 52 | 0x 0 |   1  | 0x       0 | RGBA_8888 | 0x   2  |     1.0    |    1   | 0x 0, 0x 0, 0x 0, 0x 0 |   0 |     1    |    -1               |
 h/w composer state:
  h/w composer enabled
"""

        fps, target = runner.surface_flinger_fps_from_output(output, "com.tencent.mm:appbrand1")

        self.assertEqual(fps, 16.0)
        self.assertEqual(target, "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI1#0")

    def test_surface_flinger_fps_is_unavailable_without_hwc_rows(self) -> None:
        fps, target = runner.surface_flinger_fps_from_output(
            "Display 0 HWC layers:\n Layer name\n\nh/w composer state:\n",
            "com.tencent.mm:appbrand1",
        )

        self.assertIsNone(fps)
        self.assertEqual(target, "")

    def test_surface_flinger_timestats_average_fps_prefers_appbrand_surface(self) -> None:
        output = """
layerName = com.tencent.mm/com.tencent.mm.ui.LauncherUI#0
packageName = com.tencent.mm
totalFrames = 20
averageFPS = 17.500
layerName = SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0
packageName = com.tencent.mm
totalFrames = 107
droppedFrames = 0
averageFPS = 61.921
layerName = com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0
packageName = com.tencent.mm
totalFrames = 107
averageFPS = 58.500
"""

        fps, target = runner.surface_flinger_timestats_fps_from_output(output, "com.tencent.mm:appbrand0")

        self.assertEqual(fps, 61.921)
        self.assertEqual(target, "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0")

    def test_native_android_game_timestats_fallback_parses_high_refresh_histogram(self) -> None:
        output = """
layerName = SurfaceView[com.more2.fkmj.pixel/org.cocos2dx.lua.RichAppActivity]@0(BLAST)#813
packageName = com.more2.fkmj.pixel
totalFrames = 120
averageFPS = 119.500
present2present histogram is as below:
0ms=0 8ms=118 32ms=2
"""

        sample = runner.surface_flinger_timestats_sample_from_output(output, "com.more2.fkmj.pixel")

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertEqual(sample.target, "SurfaceView[com.more2.fkmj.pixel/org.cocos2dx.lua.RichAppActivity]@0(BLAST)#813")
        self.assertGreater(sample.fps, 100.0)

    def test_surface_flinger_timestats_histogram_keeps_distribution_but_not_perfdog_jank(self) -> None:
        output = """
layerName = SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0
packageName = com.tencent.mm
totalFrames = 63
averageFPS = 60.000
present2present histogram is as below:
0ms=0 16ms=60 32ms=2 100ms=1
latch2present histogram is as below:
0ms=0 1000ms=9
"""

        sample = runner.surface_flinger_timestats_sample_from_output(output, "com.tencent.mm:appbrand0")

        self.assertIsNotNone(sample)
        assert sample is not None
        expected_duration_ms = (60 * (1000 / 60)) + (2 * 32) + 100
        self.assertAlmostEqual(sample.fps, 63 * 1000 / expected_duration_ms)
        self.assertAlmostEqual(sample.frame_time_ms or 0, 1000 / 60)
        self.assertAlmostEqual(sample.frame_time_max_ms or 0, 100.0)
        self.assertFalse(sample.has_jank_metrics)
        self.assertIsNone(sample.jank_time_ms)
        self.assertIsNone(sample.stutter_percent)
        self.assertEqual(sample.frame_count, 63)
        self.assertLess(sample.average_fps or 0, 60.0)
        payload = runner.frame_sample_payload(
            sample,
            "adb-surfaceflinger-timestats",
            "surface",
            "present2present-histogram",
        )
        self.assertNotIn("jank", payload)
        self.assertNotIn("big_jank", payload)
        self.assertNotIn("stutter_percent", payload)
        self.assertFalse(payload["target_verified"])

    def test_histogram_p95_uses_the_same_nearest_rank_rule_as_ordered_frames(self) -> None:
        self.assertEqual(runner.weighted_percentile_ms({16: 19, 100: 1}, 0.95), 16.0)

    def test_surface_flinger_timestats_caps_16ms_bins_at_nominal_60fps(self) -> None:
        metrics = runner.timestats_metrics_from_histogram({16: 60})

        self.assertIsNotNone(metrics)
        assert metrics is not None
        self.assertAlmostEqual(metrics.fps, 60.0)
        self.assertAlmostEqual(metrics.average_fps, 60.0)
        self.assertEqual(metrics.frame_count, 60)
        self.assertAlmostEqual(metrics.frame_time_p95_ms, 1000 / 60)
        self.assertAlmostEqual(metrics.window_sec, 1.0)

    def test_surface_flinger_timestats_uses_actual_144hz_refresh_period(self) -> None:
        metrics = runner.timestats_metrics_from_histogram(
            {7: 144},
            refresh_period_ms=1000.0 / 144.0,
        )

        self.assertIsNotNone(metrics)
        assert metrics is not None
        self.assertAlmostEqual(metrics.fps, 144.0, places=2)
        self.assertEqual(metrics.frame_count, 144)
        self.assertAlmostEqual(metrics.window_sec, 1.0, places=3)

    def test_surface_flinger_timestats_rejects_stale_slower_refresh_period(self) -> None:
        metrics = runner.timestats_metrics_from_histogram(
            {8: 120},
            refresh_period_ms=1000.0 / 60.0,
        )

        self.assertIsNotNone(metrics)
        assert metrics is not None
        self.assertAlmostEqual(metrics.fps, 120.0, places=2)
        self.assertAlmostEqual(metrics.frame_time_mean_ms, 1000.0 / 120.0, places=2)
        self.assertAlmostEqual(metrics.window_sec, 1.0, places=2)

    def test_surface_flinger_timestats_keeps_fast_refresh_for_60fps_app(self) -> None:
        metrics = runner.timestats_metrics_from_histogram(
            {16: 60},
            refresh_period_ms=1000.0 / 120.0,
        )

        self.assertIsNotNone(metrics)
        assert metrics is not None
        self.assertAlmostEqual(metrics.fps, 60.0, places=2)
        self.assertAlmostEqual(metrics.frame_time_mean_ms, 1000.0 / 60.0, places=2)

    def test_surface_flinger_timestats_rejects_implausible_fps_instead_of_clamping_to_240(self) -> None:
        metrics = runner.timestats_metrics_from_histogram(
            {1: 1000},
            refresh_period_ms=2.0,
        )

        self.assertIsNone(metrics)

    def test_surface_flinger_timestats_first_histogram_only_seeds_baseline(self) -> None:
        output = """
layerName = SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0
packageName = com.tencent.mm
totalFrames = 60
averageFPS = 60.000
present2present histogram is as below:
0ms=0 16ms=60
"""
        state: dict[str, tuple[dict[int, int], float]] = {}
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": output})()  # type: ignore[assignment]
            runner.time.monotonic = lambda: 100.0  # type: ignore[method-assign]
            sample = runner.read_surface_flinger_timestats_sample("", "com.tencent.mm:appbrand0", state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNone(sample)
        self.assertIn("SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0", state)

    def test_surface_flinger_timestats_total_frames_fallback_uses_poll_delta_without_fake_jank(self) -> None:
        layer = "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0"
        outputs = [
            f"layerName = {layer}\ntotalFrames = 60\naverageFPS = 60.000\n",
            f"layerName = {layer}\ntotalFrames = 115\naverageFPS = 60.000\n",
        ]
        state: dict[str, tuple[dict[int, int], float]] = {}
        times = iter([100.0, 101.0])
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": outputs.pop(0)})()  # type: ignore[assignment]
            runner.time.monotonic = lambda: next(times)  # type: ignore[method-assign]
            first = runner.read_surface_flinger_timestats_sample("", "com.tencent.mm:appbrand0", state)
            second = runner.read_surface_flinger_timestats_sample("", "com.tencent.mm:appbrand0", state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNone(first)
        self.assertIsNotNone(second)
        assert second is not None
        self.assertAlmostEqual(second.fps, 55.0)
        self.assertEqual(second.frame_count, 55)
        self.assertTrue(second.has_frame_observation)
        self.assertFalse(second.has_jank_metrics)
        self.assertTrue(second.approximate)
        payload = runner.frame_sample_payload(
            second,
            "adb-surfaceflinger-timestats",
            "surface",
            "surface-total-frame-count",
        )
        self.assertEqual(payload["frame_count"], 55)
        self.assertNotIn("jank", payload)
        self.assertNotIn("big_jank", payload)

    def test_surface_flinger_timestats_aggregates_refresh_buckets_for_same_layer(self) -> None:
        layer = "SurfaceView[com.example.game/MainActivity](BLAST)#42"
        outputs = [
            "\n".join(
                [
                    "displayRefreshRate = 60 fps",
                    "renderRate = 60 fps",
                    f"layerName = {layer}",
                    "totalFrames = 100",
                    "averageFPS = 60.000",
                    "present2present histogram is as below:",
                    "16ms=100",
                    "displayRefreshRate = 120 fps",
                    "renderRate = 120 fps",
                    f"layerName = {layer}",
                    "totalFrames = 20",
                    "averageFPS = 120.000",
                    "present2present histogram is as below:",
                    "8ms=20",
                ]
            ),
            "\n".join(
                [
                    "displayRefreshRate = 60 fps",
                    "renderRate = 60 fps",
                    f"layerName = {layer}",
                    "totalFrames = 160",
                    "averageFPS = 60.000",
                    "present2present histogram is as below:",
                    "16ms=160",
                    "displayRefreshRate = 120 fps",
                    "renderRate = 120 fps",
                    f"layerName = {layer}",
                    "totalFrames = 20",
                    "averageFPS = 120.000",
                    "present2present histogram is as below:",
                    "8ms=20",
                ]
            ),
        ]
        tracker = runner.TimestatsTracker()
        times = iter([100.0, 101.0])
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_args, **_kwargs: type(  # type: ignore[assignment]
                "Result", (), {"returncode": 0, "stdout": outputs.pop(0)}
            )()
            runner.time.monotonic = lambda: next(times)  # type: ignore[method-assign]
            first = runner.read_surface_flinger_timestats_sample("", "com.example.game", tracker)
            second = runner.read_surface_flinger_timestats_sample("", "com.example.game", tracker)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNone(first)
        self.assertIsNotNone(second)
        assert second is not None
        self.assertEqual(second.target, layer)
        self.assertEqual(second.frame_count, 60)
        self.assertAlmostEqual(second.fps, 60.0)
        self.assertEqual(list(tracker.history), [layer])

    def test_surface_flinger_timestats_total_frames_without_average_fps_is_usable(self) -> None:
        layer = "SurfaceView[com.example.game/MainActivity](BLAST)#42"
        outputs = [
            f"layerName = {layer}\ntotalFrames = 100\n",
            f"layerName = {layer}\ntotalFrames = 155\n",
        ]
        state: dict[str, tuple[dict[int, int], float]] = {}
        times = iter([100.0, 101.0])
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_args, **_kwargs: type(  # type: ignore[assignment]
                "Result", (), {"returncode": 0, "stdout": outputs.pop(0)}
            )()
            runner.time.monotonic = lambda: next(times)  # type: ignore[method-assign]
            first = runner.read_surface_flinger_timestats_sample("", "com.example.game", state)
            second = runner.read_surface_flinger_timestats_sample("", "com.example.game", state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNone(first)
        self.assertIsNotNone(second)
        assert second is not None
        self.assertEqual(second.frame_count, 55)
        self.assertAlmostEqual(second.fps, 55.0)

    def test_surface_flinger_cumulative_average_without_delta_is_not_realtime_fps(self) -> None:
        layer = "SurfaceView[com.example.game/MainActivity](BLAST)#42"
        output = f"layerName = {layer}\naverageFPS = 60.000\n"
        state: dict[str, tuple[dict[int, int], float]] = {}
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: type(  # type: ignore[assignment]
                "Result", (), {"returncode": 0, "stdout": output}
            )()
            first = runner.read_surface_flinger_timestats_sample("", "com.example.game", state)
            second = runner.read_surface_flinger_timestats_sample("", "com.example.game", state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNone(first)
        self.assertIsNone(second)

    def test_surface_flinger_timestats_missing_target_is_unavailable_not_zero(self) -> None:
        state: dict[str, tuple[dict[int, int], float]] = {
            "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0": ({16: 60}, 100.0)
        }
        original_adb = runner.adb
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": "layerName = com.android.launcher#0\naverageFPS = 60"})()  # type: ignore[assignment]
            sample = runner.read_surface_flinger_timestats_sample("", "com.tencent.mm:appbrand0", state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNone(sample)

    def test_surface_flinger_timestats_switches_from_large_stale_history_to_active_surface(self) -> None:
        old_layer = "SurfaceView[com.example.game/OldActivity]#0"
        new_layer = "SurfaceView[com.example.game/NewActivity]#0"
        outputs = [
            "\n".join(
                [
                    f"layerName = {old_layer}",
                    "totalFrames = 1000",
                    "present2present histogram is as below:",
                    "16ms=1000",
                    f"layerName = {new_layer}",
                    "totalFrames = 10",
                    "present2present histogram is as below:",
                    "16ms=10",
                ]
            ),
            "\n".join(
                [
                    f"layerName = {old_layer}",
                    "totalFrames = 1000",
                    "present2present histogram is as below:",
                    "16ms=1000",
                    f"layerName = {new_layer}",
                    "totalFrames = 70",
                    "present2present histogram is as below:",
                    "16ms=70",
                ]
            ),
        ]
        tracker = runner.TimestatsTracker()
        times = iter([100.0, 101.0])
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_args, **_kwargs: type(  # type: ignore[assignment]
                "Result",
                (),
                {"returncode": 0, "stdout": outputs.pop(0)},
            )()
            runner.time.monotonic = lambda: next(times)  # type: ignore[method-assign]
            first = runner.read_surface_flinger_timestats_sample("", "com.example.game", tracker)
            second = runner.read_surface_flinger_timestats_sample("", "com.example.game", tracker)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNone(first)
        self.assertIsNotNone(second)
        assert second is not None
        self.assertEqual(second.target, new_layer)
        self.assertEqual(second.frame_count, 60)
        self.assertEqual(tracker.selected_target, new_layer)

    def test_surface_flinger_timestats_delta_uses_observed_duration_to_avoid_optimistic_fps(self) -> None:
        state: dict[str, tuple[dict[int, int], float]] = {
            "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0": ({16: 60}, 100.0)
        }
        output = """
layerName = SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0
packageName = com.tencent.mm
totalFrames = 115
averageFPS = 60.000
present2present histogram is as below:
0ms=0 16ms=115
"""

        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": output})()  # type: ignore[assignment]
            runner.time.monotonic = lambda: 100.5  # type: ignore[method-assign]
            sample = runner.read_surface_flinger_timestats_sample("", "com.tencent.mm:appbrand0", state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertAlmostEqual(sample.fps, 60.0)
        self.assertAlmostEqual(sample.window_sec, 55 / 60)
        self.assertEqual(sample.frame_count, 55)
        self.assertAlmostEqual(sample.frame_time_mean_ms or 0, 1000 / 60)
        self.assertAlmostEqual(sample.frame_time_p95_ms or 0, 1000 / 60)

    def test_surface_flinger_timestats_poll_window_uses_command_start_time(self) -> None:
        layer = "SurfaceView[com.example.game/.MainActivity](BLAST)#1"
        state: dict[str, tuple[dict[int, int], float]] = {layer: ({16: 60}, 99.0)}
        output = f"""
layerName = {layer}
totalFrames = 120
present2present histogram is as below:
0ms=0 16ms=120
"""
        clock = [100.0]
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            def delayed_adb(*_args, **_kwargs):
                clock[0] = 100.8
                return type("Result", (), {"returncode": 0, "stdout": output})()

            runner.adb = delayed_adb  # type: ignore[assignment]
            runner.time.monotonic = lambda: clock[0]  # type: ignore[method-assign]
            sample = runner.read_surface_flinger_timestats_sample("", "com.example.game", state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertAlmostEqual(sample.fps, 60.0)
        self.assertAlmostEqual(sample.window_sec, 1.0)

    def test_surface_flinger_timestats_histogram_cannot_reconstruct_jank_order(self) -> None:
        output = """
layerName = SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0
packageName = com.tencent.mm
totalFrames = 63
averageFPS = 60.000
present2present histogram is as below:
0ms=0 16ms=60 32ms=2 200ms=1
"""

        sample = runner.surface_flinger_timestats_sample_from_output(output, "com.tencent.mm:appbrand0")

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertAlmostEqual(sample.frame_time_ms or 0, 1000 / 60)
        self.assertAlmostEqual(sample.frame_time_max_ms or 0, 200.0)
        self.assertFalse(sample.has_jank_metrics)
        payload = runner.frame_sample_payload(
            sample,
            "adb-surfaceflinger-timestats",
            "surface",
            "present2present-histogram",
        )
        self.assertNotIn("jank", payload)
        self.assertNotIn("big_jank", payload)

    def test_surface_flinger_timestats_empty_delta_outputs_real_zero_fps(self) -> None:
        state: dict[str, tuple[dict[int, int], float]] = {
            "SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0": ({16: 60}, 100.0)
        }
        output = """
layerName = SurfaceView - com.tencent.mm/com.tencent.mm.plugin.appbrand.ui.AppBrandUI#0
packageName = com.tencent.mm
totalFrames = 60
averageFPS = 60.000
present2present histogram is as below:
0ms=0 16ms=60
"""

        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": output})()  # type: ignore[assignment]
            runner.time.monotonic = lambda: 101.0  # type: ignore[method-assign]
            sample = runner.read_surface_flinger_timestats_sample("", "com.tencent.mm:appbrand0", state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertEqual(sample.fps, 0.0)
        self.assertEqual(sample.average_fps, 0.0)
        self.assertIsNone(sample.frame_time_ms)
        self.assertEqual(sample.frame_count, 0)
        self.assertEqual(sample.jank, 0)
        self.assertEqual(sample.big_jank, 0)
        self.assertFalse(sample.has_jank_metrics)

    def test_surface_flinger_display_latency_uses_average_fps_and_real_frame_time(self) -> None:
        stdout = "\n".join(
            [
                "16666666",
                "9223372036854775807\t1000000000\t9223372036854775807",
                "9223372036854775807\t1016000000\t9223372036854775807",
                "9223372036854775807\t1116000000\t9223372036854775807",
            ]
        )
        _, timestamps = runner.parse_surface_flinger_latency(stdout)
        state = runner.LatencyState(
            last_timestamp_ns=1_000_000_000,
            last_poll_time=10.0,
            recent_intervals_ms=[16.0, 16.0, 16.0],
        )

        sample = runner.latency_sample_from_timestamps(timestamps, state)

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertAlmostEqual(sample.fps, 2 * 1000 / 116)
        self.assertAlmostEqual(sample.frame_time_ms or 0, 100.0)
        self.assertEqual(sample.jank, 1)
        self.assertEqual(sample.big_jank, 0)
        self.assertEqual(sample.frame_count, 2)

    def test_global_display_latency_does_not_claim_target_jank(self) -> None:
        sample = runner.latency_sample_from_timestamps(
            [1_016_000_000, 1_116_000_000],
            runner.LatencyState(
                last_timestamp_ns=1_000_000_000,
                last_poll_time=10.0,
                recent_intervals_ms=[16.0, 16.0, 16.0],
            ),
            now=11.0,
            target_verified=False,
        )

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertGreater(sample.fps, 0.0)
        self.assertFalse(sample.has_jank_metrics)
        self.assertIsNone(sample.frame_time_ms)
        self.assertTrue(sample.approximate)
        self.assertFalse(sample.target_verified)

    def test_perfdog_ordered_jank_requires_previous_three_average_and_movie_threshold(self) -> None:
        jank, big_jank = runner.perfdog_jank_counts([50.0, 100.0, 130.0], [16.0, 16.0, 16.0])

        self.assertEqual(jank, 2)
        self.assertEqual(big_jank, 1)

    def test_perfdog_ordered_jank_does_not_mark_uniform_low_fps_as_jank(self) -> None:
        jank, big_jank = runner.perfdog_jank_counts([33.3, 33.3, 33.3, 33.3, 33.3])

        self.assertEqual(jank, 0)
        self.assertEqual(big_jank, 0)

    def test_surface_flinger_display_latency_empty_delta_outputs_zero_fps(self) -> None:
        original_time = runner.time.monotonic
        try:
            runner.time.monotonic = lambda: 12.0  # type: ignore[method-assign]
            sample = runner.latency_sample_from_timestamps(
                [1_000_000_000],
                runner.LatencyState(last_timestamp_ns=1_000_000_000, last_poll_time=10.0),
            )
        finally:
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertEqual(sample.fps, 0.0)
        self.assertEqual(sample.average_fps, 0.0)
        self.assertIsNone(sample.frame_time_ms)
        self.assertEqual(sample.frame_count, 0)

    def test_global_display_latency_idle_resume_drops_static_gap_from_jank(self) -> None:
        refresh_ns = 16_666_667
        baseline = 1_000_000_000
        state = runner.LatencyState(
            last_timestamp_ns=baseline,
            last_poll_time=10.0,
            recent_intervals_ms=[16.67, 16.67, 16.67],
        )

        zero = runner.latency_sample_from_timestamps([baseline], state, now=12.0)
        self.assertIsNotNone(zero)
        assert zero is not None
        self.assertEqual(zero.fps, 0.0)
        self.assertEqual(zero.source_sequence, 1)
        self.assertGreater(zero.source_elapsed_sec or 0, 0)

        resumed_start = baseline + 5_000_000_000
        resumed = [resumed_start + refresh_ns * index for index in range(62)]
        after_resume = runner.latency_sample_from_timestamps(resumed, state, now=13.1)

        self.assertIsNotNone(after_resume)
        assert after_resume is not None
        self.assertEqual(after_resume.source_sequence, 2)
        self.assertGreater(after_resume.source_elapsed_sec or 0, zero.source_elapsed_sec or 0)
        self.assertEqual(after_resume.jank, 0)
        self.assertEqual(after_resume.big_jank, 0)
        self.assertLess(after_resume.frame_time_max_ms or 0, 20.0)

    def test_gfxinfo_summary_without_new_frames_is_not_real_zero_fps(self) -> None:
        state = {"total": 4.0, "jank": 1.0, "big_jank": 1.0, "time": 100.0}
        stdout = "Total frames rendered: 4\nJanky frames: 1 (25.00%)\nNumber Frame deadline missed: 1\n"

        original_time = runner.time.monotonic
        try:
            runner.time.monotonic = lambda: 103.0  # type: ignore[method-assign]
            fps, _, _, _, _ = runner.read_fps("", "", set(), state)
        finally:
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        # Empty package exits before adb; use the parser contract directly for no-frame state.
        self.assertIsNone(fps)
        self.assertEqual(runner.parse_gfxinfo_summary(stdout), (4, 1, 1))

    def test_gfxinfo_summary_fps_does_not_relabel_android_janky_frames_as_perfdog_jank(self) -> None:
        state = {"total": 4.0, "time": 100.0}
        stdout = "Total frames rendered: 64\nJanky frames: 20 (31.25%)\nNumber Frame deadline missed: 10\n"

        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": stdout})()  # type: ignore[assignment]
            runner.time.monotonic = lambda: 101.0  # type: ignore[method-assign]
            fps, jank, big_jank, target, frame_time_ms = runner.read_fps(
                "",
                "com.example.game",
                set(),
                state,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertAlmostEqual(fps or 0, 60.0)
        self.assertIsNone(jank)
        self.assertIsNone(big_jank)
        self.assertEqual(target, "com.example.game")
        self.assertIsNone(frame_time_ms)

    def test_android_cpu_raw_uses_per_core_denominator(self) -> None:
        for cores in (1, 4, 8):
            with self.subTest(cores=cores):
                previous = runner.CpuSnapshot(total_jiffies=10_000, process_jiffies=1_000, online_cpus=cores)
                current = runner.CpuSnapshot(
                    total_jiffies=10_000 + 100 * cores,
                    process_jiffies=1_100,
                    online_cpus=cores,
                )
                self.assertAlmostEqual(runner.cpu_percent_from_snapshots(previous, current) or 0, 100.0)

    def test_android_cpu_normalized_divides_raw_by_online_cores(self) -> None:
        self.assertAlmostEqual(runner.normalized_cpu_percent_from_raw(100.0, 1), 100.0)
        self.assertAlmostEqual(runner.normalized_cpu_percent_from_raw(200.0, 4), 50.0)

    def test_android_cpu_core_values_report_device_wide_each_core(self) -> None:
        previous = runner.CpuSnapshot(
            10_000,
            1_000,
            4,
            ((0, 1_000), (1, 1_000), (2, 1_000), (3, 1_000)),
            (0, 1, 2, 3),
        )
        current = runner.CpuSnapshot(
            10_400,
            1_100,
            4,
            ((0, 1_100), (1, 1_000), (2, 1_000), (3, 1_000)),
            (0, 1, 2, 3),
        )

        self.assertAlmostEqual(runner.cpu_percent_from_snapshots(previous, current) or 0, 100.0)
        self.assertAlmostEqual(runner.normalized_cpu_percent_from_raw(100.0, 4), 25.0)
        core_values = runner.core_percent_from_snapshots(previous, current)
        self.assertIsNotNone(core_values)
        assert core_values is not None
        self.assertAlmostEqual(core_values[0], 100.0)
        self.assertEqual(core_values[1:], [0.0, 0.0, 0.0])

    def test_android_cpu_core_values_are_unavailable_when_core_set_changes_or_rolls_back(self) -> None:
        previous = runner.CpuSnapshot(10_000, 1_000, 2, ((0, 1_000), (1, 1_000)), (0, 1))
        changed = runner.CpuSnapshot(10_200, 1_100, 2, ((0, 1_100), (2, 1_000)), (0, 2))
        rollback = runner.CpuSnapshot(10_200, 1_100, 2, ((0, 900), (1, 1_000)), (0, 1))
        self.assertIsNone(runner.core_percent_from_snapshots(previous, changed))
        self.assertIsNone(runner.core_percent_from_snapshots(previous, rollback))

    def test_android_cpu_total_does_not_double_count_guest_fields(self) -> None:
        stdout = "\n".join(
            [
                "cpu  100 10 20 200 5 2 3 4 50 6",
                "cpu0 1 1 1 1 1 1 1 1 0 0",
                "42 (game process) R 0 0 0 0 0 0 0 0 0 0 7 8 0 0 0",
                "0",
            ]
        )

        snapshot = runner.parse_cpu_snapshot(stdout, 42)

        self.assertIsNotNone(snapshot)
        assert snapshot is not None
        self.assertEqual(snapshot.total_jiffies, 344)
        self.assertEqual(snapshot.process_jiffies, 15)
        self.assertEqual(snapshot.core_jiffies, ((0, 6),))

    def test_android_cpu_reset_or_nonadvancing_clock_is_unavailable(self) -> None:
        previous = runner.CpuSnapshot(total_jiffies=10_000, process_jiffies=1_000, online_cpus=8)

        self.assertIsNone(runner.cpu_percent_from_snapshots(previous, runner.CpuSnapshot(10_000, 1_100, 8)))
        self.assertIsNone(runner.cpu_percent_from_snapshots(previous, runner.CpuSnapshot(10_800, 900, 8)))

    def test_cpu_and_memory_loops_subtract_command_time_from_sampling_interval(self) -> None:
        clock = [100.0]
        cpu_waits: list[float] = []
        memory_waits: list[float] = []
        emitted: list[tuple[str, dict]] = []

        class OneShotStop:
            def __init__(self, waits: list[float]) -> None:
                self.stopped = False
                self.waits = waits

            def is_set(self) -> bool:
                return self.stopped

            def wait(self, delay: float) -> bool:
                self.waits.append(delay)
                self.stopped = True
                return True

        originals = (
            runner.read_cpu,
            runner.read_memory_sample,
            runner.time.monotonic,
            runner.emit,
        )

        def read_cpu(*_args):
            clock[0] += 0.30
            return 75.0, runner.CpuSnapshot(1000, 100, 8)

        def read_memory(*_args):
            clock[0] += 0.25
            return runner.MemorySample(512.0, "pss", "test-pss", False, 700.0)

        try:
            runner.read_cpu = read_cpu  # type: ignore[assignment]
            runner.read_memory_sample = read_memory  # type: ignore[assignment]
            runner.time.monotonic = lambda: clock[0]  # type: ignore[method-assign]
            runner.emit = lambda kind, payload: emitted.append((kind, payload))  # type: ignore[assignment]

            runner.cpu_loop("", 42, 1.0, OneShotStop(cpu_waits))  # type: ignore[arg-type]
            runner.memory_loop("", 42, 1.0, OneShotStop(memory_waits))  # type: ignore[arg-type]
        finally:
            (
                runner.read_cpu,
                runner.read_memory_sample,
                runner.time.monotonic,
                runner.emit,
            ) = originals

        self.assertEqual([kind for kind, _ in emitted], ["cpu", "memory"])
        self.assertEqual(emitted[1][1]["rss_value"], 700.0)
        self.assertAlmostEqual(cpu_waits[0], 0.70, places=5)
        self.assertAlmostEqual(memory_waits[0], 0.75, places=5)

    def test_metric_thread_failure_does_not_stop_other_sources(self) -> None:
        stop_event = threading.Event()
        failure_event = threading.Event()
        failure_lock = threading.Lock()
        observed: list[tuple[str, bool]] = []
        original_emit = runner.emit

        def fail() -> None:
            raise RuntimeError("source failed")

        try:
            runner.emit = lambda kind, _payload: observed.append((kind, stop_event.is_set()))  # type: ignore[assignment]
            runner.guarded_metric_loop(
                "Android FPS",
                fail,
                (),
                stop_event,
                failure_event,
                failure_lock,
            )
        finally:
            runner.emit = original_emit  # type: ignore[assignment]

        self.assertFalse(stop_event.is_set())
        self.assertFalse(failure_event.is_set())
        self.assertEqual(observed, [("status", False)])

    def test_android_memory_rss_fallback_is_labeled_honestly(self) -> None:
        responses = iter(
            [
                type("Result", (), {"returncode": 1, "stdout": "permission denied", "stderr": ""})(),
                type("Result", (), {"returncode": 0, "stdout": "TOTAL RSS: 204800\n"})(),
                type("Result", (), {"returncode": 0, "stdout": "TOTAL RSS: 204800\n"})(),
                type("Result", (), {"returncode": 0, "stdout": "VmRSS:\t102400 kB\n"})(),
            ]
        )
        original_adb = runner.adb
        try:
            runner.adb = lambda *_, **__: next(responses)  # type: ignore[assignment]
            sample = runner.read_memory_sample("", 42)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertEqual(sample.value_mb, 100.0)
        self.assertEqual(sample.metric, "rss")
        self.assertEqual(sample.source, "adb-proc-status-rss")
        self.assertTrue(sample.fallback)

    def test_zero_android_memory_is_unavailable(self) -> None:
        responses = iter(
            [
                type("Result", (), {"returncode": 0, "stdout": "TOTAL PSS: 0\n", "stderr": ""})(),
                type("Result", (), {"returncode": 0, "stdout": "VmRSS:\t0 kB\n", "stderr": ""})(),
            ]
        )
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: next(responses)  # type: ignore[assignment]
            sample = runner.read_memory_sample(
                "",
                42,
                runner.MemoryProbeState(rollup_supported=False, compact_meminfo_supported=False),
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNone(sample)

    def test_android_memory_uses_compact_local_pss_and_caches_rollup_denial(self) -> None:
        calls: list[list[str]] = []
        responses = iter(
            [
                type("Result", (), {"returncode": 1, "stdout": "", "stderr": "Permission denied"})(),
                type("Result", (), {"returncode": 0, "stdout": "TOTAL PSS: 204800 TOTAL RSS: 307200\n"})(),
                type("Result", (), {"returncode": 0, "stdout": "TOTAL PSS: 205824 TOTAL RSS: 308224\n"})(),
            ]
        )
        state = runner.MemoryProbeState()
        original_adb = runner.adb
        try:
            def fake_adb(_serial, args, **_kwargs):
                calls.append(args)
                return next(responses)

            runner.adb = fake_adb  # type: ignore[assignment]
            first = runner.read_memory_sample("", 42, state)
            second = runner.read_memory_sample("", 42, state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNotNone(first)
        self.assertIsNotNone(second)
        assert first is not None and second is not None
        self.assertEqual(first.source, "adb-dumpsys-meminfo-local-pss")
        self.assertAlmostEqual(first.value_mb, 200.0)
        self.assertAlmostEqual(second.value_mb, 201.0)
        self.assertEqual(sum("smaps_rollup" in " ".join(args) for args in calls), 1)
        self.assertEqual(calls[-1][-4:], ["meminfo", "--local", "--pssonly", "42"])

    def test_transient_compact_meminfo_failure_is_retried_next_sample(self) -> None:
        compact_calls = 0
        original_adb = runner.adb
        state = runner.MemoryProbeState(rollup_supported=False)
        try:
            def fake_adb(_serial, args, **_kwargs):
                nonlocal compact_calls
                if "--pssonly" in args:
                    compact_calls += 1
                    if compact_calls == 1:
                        return type("Result", (), {"returncode": 124, "stdout": "", "stderr": "adb timed out"})()
                    return type("Result", (), {"returncode": 0, "stdout": "TOTAL PSS: 205824\n", "stderr": ""})()
                if args[-3:] == ["dumpsys", "meminfo", "42"]:
                    return type("Result", (), {"returncode": 0, "stdout": "TOTAL PSS: 204800\n", "stderr": ""})()
                raise AssertionError(args)

            runner.adb = fake_adb  # type: ignore[assignment]
            first = runner.read_memory_sample("", 42, state)
            second = runner.read_memory_sample("", 42, state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNotNone(first)
        self.assertIsNotNone(second)
        assert first is not None and second is not None
        self.assertEqual(first.source, "adb-dumpsys-meminfo")
        self.assertEqual(second.source, "adb-dumpsys-meminfo-local-pss")
        self.assertEqual(compact_calls, 2)

    def test_android_memory_prefers_smaps_rollup_pss(self) -> None:
        original_adb = runner.adb
        try:
            runner.adb = lambda *_, **__: type(  # type: ignore[assignment]
                "Result",
                (),
                {"returncode": 0, "stdout": "Rss: 307200 kB\nPss: 204800 kB\n"},
            )()
            sample = runner.read_memory_sample("", 42)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertEqual(sample.value_mb, 200.0)
        self.assertEqual(sample.metric, "pss")
        self.assertEqual(sample.source, "adb-proc-smaps-rollup")
        self.assertFalse(sample.fallback)

    def test_wechat_appbrand_sparse_framestats_rows_are_unavailable(self) -> None:
        def frame_row(intended: int, completed: int) -> str:
            return ",".join(["0", str(intended)] + ["0"] * 11 + [str(completed)])

        stdout = "\n".join(
            [
                "---PROFILEDATA---",
                "Flags,IntendedVsync,...",
                frame_row(1_000_000_000, 1_020_000_000),
                frame_row(7_000_000_000, 7_020_000_000),
                frame_row(13_000_000_000, 13_020_000_000),
                frame_row(19_000_000_000, 19_020_000_000),
                "---PROFILEDATAEND---",
            ]
        )

        original_adb = runner.adb
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": stdout})()  # type: ignore[assignment]
            fps, jank, big_jank, target, frame_time_ms = runner.read_fps(
                "",
                "1690",
                {123},
                {},
                app_package="com.tencent.mm:appbrand1",
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNone(fps)
        self.assertIsNone(jank)
        self.assertIsNone(big_jank)
        self.assertEqual(target, "")
        self.assertIsNone(frame_time_ms)

    def test_wechat_appbrand_exact_display_present_preserves_true_low_fps(self) -> None:
        def frame_row(intended: int, presented: int) -> str:
            return f"0,{intended},0,{presented},{presented}"

        timestamps = [1_000_000_000 + index * 250_000_000 for index in range(6)]
        stdout = "\n".join(
            [
                "Window com.tencent.mm:appbrand1/AppBrandUI",
                "---PROFILEDATA---",
                "Flags,IntendedVsync,Vsync,FrameCompleted,DisplayPresentTime",
                *(frame_row(value - 10_000_000, value) for value in timestamps),
                "---PROFILEDATA---",
            ]
        )
        state: dict[str, object] = {
            "last_end_ns": float(timestamps[0]),
            "last_new_frame_time": 100.0,
            "recent_intervals_ms": [250.0, 250.0, 250.0],
        }
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type(
                "Result", (), {"returncode": 0, "stdout": stdout, "stderr": ""}
            )()  # type: ignore[assignment]
            runner.time.monotonic = lambda: 101.25  # type: ignore[method-assign]
            result = runner.read_gfxinfo_sample(
                "",
                "1690",
                {timestamps[0]},
                state,
                app_package="com.tencent.mm:appbrand1",
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNotNone(result)
        assert result is not None
        self.assertEqual(result.frame_source, "gfxinfo-display-present")
        self.assertAlmostEqual(result.sample.fps, 4.0)
        self.assertEqual(result.sample.jank, 0.0)
        self.assertEqual(result.sample.big_jank, 0.0)
        self.assertTrue(result.sample.has_jank_metrics)
        self.assertFalse(result.sample.approximate)

    def test_rejected_wechat_sparse_framestats_does_not_poison_jank_history(self) -> None:
        def frame_row(intended: int, completed: int) -> str:
            return ",".join(["0", str(intended)] + ["0"] * 11 + [str(completed)])

        stdout = "\n".join(
            [
                "---PROFILEDATA---",
                "Flags,IntendedVsync,...",
                frame_row(1_000_000_000, 1_020_000_000),
                frame_row(7_000_000_000, 7_020_000_000),
                frame_row(13_000_000_000, 13_020_000_000),
                frame_row(19_000_000_000, 19_020_000_000),
                "---PROFILEDATAEND---",
            ]
        )
        previous_history = [16.0, 16.0, 16.0]
        state: dict[str, object] = {
            "last_end_ns": 1_020_000_000.0,
            "last_new_frame_time": 100.0,
            "recent_intervals_ms": list(previous_history),
        }
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type(
                "Result", (), {"returncode": 0, "stdout": stdout, "stderr": ""}
            )()  # type: ignore[assignment]
            runner.time.monotonic = lambda: 100.5  # type: ignore[method-assign]
            result = runner.read_gfxinfo_sample(
                "",
                "1690",
                {1_020_000_000},
                state,
                app_package="com.tencent.mm:appbrand1",
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNone(result)
        self.assertEqual(state["recent_intervals_ms"], previous_history)

    def test_wechat_appbrand_summary_tiny_delta_is_unavailable(self) -> None:
        state = {"total": 4.0, "jank": 1.0, "big_jank": 1.0, "time": 100.0}
        stdout = "Total frames rendered: 6\nJanky frames: 1 (16.67%)\nNumber Frame deadline missed: 1\n"

        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": stdout})()  # type: ignore[assignment]
            runner.time.monotonic = lambda: 110.0  # type: ignore[method-assign]
            fps, _, _, target, _ = runner.read_fps(
                "",
                "1690",
                set(),
                state,
                app_package="com.tencent.mm:appbrand1",
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNone(fps)
        self.assertEqual(target, "")

    def test_gfxinfo_frame_completed_fallback_does_not_claim_display_jank(self) -> None:
        def frame_row(completed: int) -> str:
            return ",".join(["0", str(completed - 10_000_000)] + ["0"] * 11 + [str(completed)])

        stdout = "\n".join(
            [
                "---PROFILEDATA---",
                "Flags,IntendedVsync,...",
                frame_row(1_016_000_000),
                frame_row(1_116_000_000),
                "---PROFILEDATAEND---",
            ]
        )

        original_adb = runner.adb
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": stdout})()  # type: ignore[assignment]
            fps, jank, big_jank, target, frame_time_ms = runner.read_fps(
                "",
                "com.example.game",
                {999},
                {"last_end_ns": 1_000_000_000.0, "recent_intervals_ms": [16.0, 16.0, 16.0]},
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertAlmostEqual(fps or 0, 2 * 1000 / 116)
        self.assertIsNone(jank)
        self.assertIsNone(big_jank)
        self.assertEqual(target, "com.example.game")
        self.assertIsNone(frame_time_ms)

    def test_gfxinfo_display_present_time_supports_ordered_perfdog_jank(self) -> None:
        header = "Flags,IntendedVsync,Vsync,FrameCompleted,DisplayPresentTime"
        stdout = "\n".join(
            [
                "Window com.example.game/MainActivity",
                "---PROFILEDATA---",
                header,
                "0,1000000000,0,1010000000,1016000000",
                "0,1016000000,0,1100000000,1116000000",
                "---PROFILEDATA---",
            ]
        )
        state: dict[str, object] = {
            "last_end_ns": 1_000_000_000.0,
            "recent_intervals_ms": [16.0, 16.0, 16.0],
        }
        original_adb = runner.adb
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": stdout})()  # type: ignore[assignment]
            result = runner.read_gfxinfo_sample("", "com.example.game", {999}, state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNotNone(result)
        assert result is not None
        self.assertEqual(result.frame_source, "gfxinfo-display-present")
        self.assertTrue(result.sample.has_jank_metrics)
        self.assertEqual(result.sample.jank, 1)
        self.assertAlmostEqual(result.sample.frame_time_ms or 0, 100.0)
        self.assertAlmostEqual(result.sample.source_elapsed_sec or 0, 0.116)
        self.assertEqual(result.sample.source_sequence, 1)
        self.assertTrue(result.sample.target_verified)
        payload = runner.frame_sample_payload(
            result.sample,
            "adb-gfxinfo-framestats",
            "app",
            result.frame_source,
        )
        self.assertTrue(payload["target_verified"])

    def test_gfxinfo_dynamic_header_prefers_display_present_time(self) -> None:
        header = [
            "Flags", "IntendedVsync", "Vsync", "OldestInputEvent", "NewestInputEvent",
            "HandleInputStart", "AnimationStart", "PerformTraversalsStart", "DrawStart",
            "FrameDeadline", "FrameInterval", "FrameStartTime", "SyncQueued", "SyncStart",
            "IssueDrawCommandsStart", "SwapBuffers", "FrameCompleted", "DequeueBufferDuration",
            "QueueBufferDuration", "GpuCompleted", "SwapBuffersCompleted", "DisplayPresentTime",
        ]

        def row(intended: int, completed: int, presented: int) -> str:
            values = ["0"] * len(header)
            values[0] = "0"
            values[1] = str(intended)
            values[16] = str(completed)
            values[21] = str(presented)
            return ",".join(values)

        stdout = "\n".join(
            [
                "Window com.example.game/MainActivity",
                "---PROFILEDATA---",
                ",".join(header),
                row(1_000_000_000, 1_050_000_000, 1_016_000_000),
                row(1_016_000_000, 1_200_000_000, 1_032_000_000),
                "---PROFILEDATA---",
            ]
        )

        blocks = runner.parse_framestats_blocks(stdout)

        self.assertEqual(len(blocks), 1)
        self.assertEqual(blocks[0].timestamp_source, "DisplayPresentTime")
        self.assertTrue(blocks[0].exact_display_present)
        self.assertEqual([timestamp for _, timestamp in blocks[0].rows], [1_016_000_000, 1_032_000_000])

    def test_gfxinfo_invalid_display_present_time_falls_back_to_frame_completed(self) -> None:
        header = "Flags,IntendedVsync,Vsync,FrameCompleted,DisplayPresentTime"
        stdout = "\n".join(
            [
                "Window com.example.game/MainActivity",
                "---PROFILEDATA---",
                header,
                "0,1000000000,0,1016000000,0",
                "0,1016000000,0,1032000000,9223372036854775807",
                "---PROFILEDATA---",
            ]
        )

        blocks = runner.parse_framestats_blocks(stdout)

        self.assertEqual(len(blocks), 1)
        self.assertEqual(blocks[0].timestamp_source, "FrameCompleted")
        self.assertFalse(blocks[0].exact_display_present)

    def test_gfxinfo_multiple_windows_selects_most_recent_timeline(self) -> None:
        stdout = "\n".join(
            [
                "OldWindow",
                "---PROFILEDATA---",
                "Flags,IntendedVsync,FrameCompleted",
                "0,100,200",
                "0,200,300",
                "---PROFILEDATA---",
                "ActiveWindow",
                "---PROFILEDATA---",
                "Flags,IntendedVsync,FrameCompleted",
                "0,1000,1100",
                "0,1100,1200",
                "---PROFILEDATA---",
            ]
        )

        rows = runner.parse_framestats(stdout)

        self.assertEqual(rows, [(1000, 1100), (1100, 1200)])

    def test_gfxinfo_target_key_survives_unrelated_window_removal(self) -> None:
        header = "Flags,IntendedVsync,Vsync,FrameCompleted,DisplayPresentTime"

        def block(name: str, timestamps: list[int]) -> list[str]:
            rows = [
                f"0,{timestamp - 16_000_000},0,{timestamp},{timestamp}"
                for timestamp in timestamps
            ]
            return [name, "---PROFILEDATA---", header, *rows, "---PROFILEDATA---"]

        target_name = "Window com.example.game/MainActivity"
        first_stdout = "\n".join(
            block("Window com.example.other/OldActivity", [1_000_000_000, 1_016_000_000])
            + block(target_name, [2_000_000_000, 2_016_000_000])
        )
        second_stdout = "\n".join(
            block(target_name, [2_000_000_000, 2_016_000_000, 2_032_000_000])
        )
        responses = iter(
            [
                type("Result", (), {"returncode": 0, "stdout": first_stdout})(),
                type("Result", (), {"returncode": 0, "stdout": second_stdout})(),
            ]
        )
        original_adb = runner.adb
        try:
            runner.adb = lambda *_args, **_kwargs: next(responses)  # type: ignore[assignment]
            seen: set[int] = set()
            state: dict[str, object] = {}
            baseline = runner.read_gfxinfo_sample("", "com.example.game", seen, state)
            sample = runner.read_gfxinfo_sample("", "com.example.game", seen, state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNone(baseline)
        self.assertIsNotNone(sample)
        assert sample is not None
        self.assertEqual(sample.frame_source, "gfxinfo-display-present")
        self.assertAlmostEqual(sample.sample.fps, 62.5)

    def test_gfxinfo_ordered_source_reports_duplicate_and_out_of_order_rows(self) -> None:
        def frame_row(completed: int) -> str:
            return ",".join(["0", str(completed - 1_000_000)] + ["0"] * 11 + [str(completed)])

        stdout = "\n".join(
            [
                "Window com.example.game/MainActivity",
                "---PROFILEDATA---",
                "Flags,IntendedVsync,...",
                frame_row(1_016_000_000),
                frame_row(1_016_000_000),
                frame_row(1_008_000_000),
                frame_row(1_032_000_000),
                frame_row(1_048_000_000),
                "---PROFILEDATAEND---",
            ]
        )
        state: dict[str, object] = {
            "last_end_ns": 1_000_000_000.0,
            "recent_intervals_ms": [16.0, 16.0, 16.0],
        }
        original_adb = runner.adb
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": stdout})()  # type: ignore[assignment]
            result = runner.read_gfxinfo_sample("", "com.example.game", {999}, state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNotNone(result)
        assert result is not None
        self.assertEqual(result.sample.frame_count, 1)
        self.assertEqual(result.sample.duplicate_timestamps, 1)
        self.assertEqual(result.sample.out_of_order_timestamps, 1)

    def test_gfxinfo_clears_stale_exact_capability_when_display_block_disappears(self) -> None:
        original_adb = runner.adb
        seen = {1_000_000_000}
        state: dict[str, object] = {
            "frame_exact_display": True,
            "frame_block": "Window com.example.game/OldActivity#window-0",
            "frame_timestamp_source": "DisplayPresentTime",
            "last_end_ns": 1_000_000_000.0,
            "recent_intervals_ms": [16.0, 16.0, 16.0],
            "source_origin_timestamp_ns": 900_000_000.0,
            "source_sequence": 8,
            "last_new_frame_time": 100.0,
            "last_zero_emit_time": 101.0,
        }
        try:
            runner.adb = lambda *_args, **_kwargs: type(  # type: ignore[assignment]
                "Result",
                (),
                {"returncode": 0, "stdout": "Total frames rendered: 100\n"},
            )()
            result = runner.read_gfxinfo_sample(
                "",
                "com.example.game",
                seen,
                state,
            )
        finally:
            runner.adb = original_adb  # type: ignore[assignment]

        self.assertIsNone(result)
        self.assertFalse(bool(state.get("frame_exact_display")))
        self.assertEqual(seen, set())
        for key in (
            "frame_block",
            "frame_timestamp_source",
            "last_end_ns",
            "recent_intervals_ms",
            "source_origin_timestamp_ns",
            "source_sequence",
            "last_new_frame_time",
            "last_zero_emit_time",
        ):
            self.assertNotIn(key, state)

    def test_vendor_current_layer_fps_is_explicitly_approximate(self) -> None:
        payload = runner.vendor_current_layer_fps_payload(59.5, "SurfaceView[com.example.game]")

        self.assertEqual(payload["fps"], 59.5)
        self.assertEqual(payload["platform"], "android")
        self.assertFalse(payload["ordered_frames"])
        self.assertTrue(payload["approximate"])
        self.assertFalse(payload["target_verified"])
        self.assertNotIn("frame_time_ms", payload)
        self.assertNotIn("jank", payload)

    def test_fps_loop_uses_ordered_display_only_after_target_sources_are_unavailable(self) -> None:
        display_sample = runner.TimestatsSample(
            fps=60.0,
            target="display",
            frame_count=60,
            window_sec=1.0,
            has_frame_observation=True,
            has_jank_metrics=True,
            ordered_frames=True,
        )
        emitted: list[tuple[str, dict]] = []

        class OneShotStop:
            stopped = False

            def is_set(self) -> bool:
                return self.stopped

            def wait(self, _: float) -> bool:
                self.stopped = True
                return True

        originals = (
            runner.read_surface_flinger_layer_latency_samples,
            runner.read_gfxinfo_sample,
            runner.read_surface_flinger_display_latency_sample,
            runner.read_surface_flinger_timestats_sample,
            runner.prepare_surface_flinger_timestats,
            runner.read_surface_refresh_period_ns,
            runner.read_surface_flinger_fps,
            runner.emit,
        )
        try:
            runner.read_surface_flinger_layer_latency_samples = lambda *_: ([], False)  # type: ignore[assignment]
            runner.read_gfxinfo_sample = lambda *_args, **_kwargs: None  # type: ignore[assignment]
            runner.read_surface_flinger_display_latency_sample = lambda *_: display_sample  # type: ignore[assignment]
            runner.read_surface_flinger_timestats_sample = lambda *_args, **_kwargs: None  # type: ignore[assignment]
            runner.prepare_surface_flinger_timestats = lambda *_: (True, False)  # type: ignore[assignment]
            runner.read_surface_refresh_period_ns = lambda *_: 16_666_667  # type: ignore[assignment]
            runner.read_surface_flinger_fps = lambda *_: (None, "")  # type: ignore[assignment]
            runner.emit = lambda kind, payload: emitted.append((kind, payload))  # type: ignore[assignment]

            runner.fps_loop("", 42, "com.example.game", 1.0, OneShotStop())  # type: ignore[arg-type]
        finally:
            (
                runner.read_surface_flinger_layer_latency_samples,
                runner.read_gfxinfo_sample,
                runner.read_surface_flinger_display_latency_sample,
                runner.read_surface_flinger_timestats_sample,
                runner.prepare_surface_flinger_timestats,
                runner.read_surface_refresh_period_ns,
                runner.read_surface_flinger_fps,
                runner.emit,
            ) = originals

        self.assertEqual(len(emitted), 1)
        self.assertEqual(emitted[0][1]["source"], "adb-surfaceflinger-display-latency")

    def test_fps_loop_prefers_timestats_distribution_over_non_display_gfxinfo(self) -> None:
        approximate_gfxinfo = runner.GfxInfoResult(
            runner.TimestatsSample(fps=58.0, target="com.example.game", ordered_frames=True, approximate=True),
            "com.example.game",
            "gfxinfo-framecompleted",
        )
        histogram = runner.TimestatsSample(
            fps=59.0,
            target="SurfaceView[com.example.game]",
            histogram={16: 59},
            frame_time_ms=16.0,
            frame_count=59,
            window_sec=1.0,
            has_frame_observation=True,
            approximate=True,
        )
        emitted: list[tuple[str, dict]] = []

        class OneShotStop:
            stopped = False

            def is_set(self) -> bool:
                return self.stopped

            def wait(self, _: float) -> bool:
                self.stopped = True
                return True

        originals = (
            runner.read_surface_flinger_layer_latency_samples,
            runner.read_gfxinfo_sample,
            runner.read_surface_flinger_display_latency_sample,
            runner.read_surface_refresh_period_ns,
            runner.prepare_surface_flinger_timestats,
            runner.read_surface_flinger_timestats_sample,
            runner.disable_surface_flinger_timestats,
            runner.emit,
        )
        try:
            runner.read_surface_flinger_layer_latency_samples = lambda *_: ([], False)  # type: ignore[assignment]
            runner.read_gfxinfo_sample = lambda *_args, **_kwargs: approximate_gfxinfo  # type: ignore[assignment]
            runner.read_surface_flinger_display_latency_sample = lambda *_: None  # type: ignore[assignment]
            runner.read_surface_refresh_period_ns = lambda *_: 16_666_667  # type: ignore[assignment]
            runner.prepare_surface_flinger_timestats = lambda *_: (True, False)  # type: ignore[assignment]
            runner.read_surface_flinger_timestats_sample = lambda *_args, **_kwargs: histogram  # type: ignore[assignment]
            runner.disable_surface_flinger_timestats = lambda *_: None  # type: ignore[assignment]
            runner.emit = lambda kind, payload: emitted.append((kind, payload))  # type: ignore[assignment]

            runner.fps_loop("", 42, "com.example.game", 1.0, OneShotStop())  # type: ignore[arg-type]
        finally:
            (
                runner.read_surface_flinger_layer_latency_samples,
                runner.read_gfxinfo_sample,
                runner.read_surface_flinger_display_latency_sample,
                runner.read_surface_refresh_period_ns,
                runner.prepare_surface_flinger_timestats,
                runner.read_surface_flinger_timestats_sample,
                runner.disable_surface_flinger_timestats,
                runner.emit,
            ) = originals

        self.assertEqual(len(emitted), 1)
        self.assertEqual(emitted[0][1]["source"], "adb-surfaceflinger-timestats")

    def test_fps_loop_cools_down_non_display_gfxinfo_probes(self) -> None:
        approximate_gfxinfo = runner.GfxInfoResult(
            runner.TimestatsSample(fps=58.0, target="com.example.game", ordered_frames=True, approximate=True),
            "com.example.game",
            "gfxinfo-framecompleted",
        )
        probe_count = 0

        class TwoShotStop:
            stopped = False
            waits = 0
            now = 100.0

            def is_set(self) -> bool:
                return self.stopped

            def wait(self, _: float) -> bool:
                self.waits += 1
                self.now += 1.0
                self.stopped = self.waits >= 2
                return self.stopped

        stop = TwoShotStop()
        originals = (
            runner.read_surface_flinger_layer_latency_samples,
            runner.read_gfxinfo_sample,
            runner.read_surface_refresh_period_ns,
            runner.prepare_surface_flinger_timestats,
            runner.read_surface_flinger_timestats_sample,
            runner.read_surface_flinger_fps,
            runner.read_surface_flinger_display_latency_sample,
            runner.time.monotonic,
            runner.emit,
        )

        def read_gfxinfo(*_args, **_kwargs):
            nonlocal probe_count
            probe_count += 1
            return approximate_gfxinfo

        try:
            runner.read_surface_flinger_layer_latency_samples = lambda *_: ([], False)  # type: ignore[assignment]
            runner.read_gfxinfo_sample = read_gfxinfo  # type: ignore[assignment]
            runner.read_surface_refresh_period_ns = lambda *_: 16_666_667  # type: ignore[assignment]
            runner.prepare_surface_flinger_timestats = lambda *_: (True, False)  # type: ignore[assignment]
            runner.read_surface_flinger_timestats_sample = lambda *_args, **_kwargs: runner.TimestatsSample(  # type: ignore[assignment]
                fps=60.0,
                target="SurfaceView[com.example.game]",
                histogram={16: 60},
                approximate=True,
            )
            runner.read_surface_flinger_fps = lambda *_: (None, "")  # type: ignore[assignment]
            runner.read_surface_flinger_display_latency_sample = lambda *_: None  # type: ignore[assignment]
            runner.time.monotonic = lambda: stop.now  # type: ignore[method-assign]
            runner.emit = lambda *_: None  # type: ignore[assignment]

            runner.fps_loop("", 42, "com.example.game", 1.0, stop)  # type: ignore[arg-type]
        finally:
            (
                runner.read_surface_flinger_layer_latency_samples,
                runner.read_gfxinfo_sample,
                runner.read_surface_refresh_period_ns,
                runner.prepare_surface_flinger_timestats,
                runner.read_surface_flinger_timestats_sample,
                runner.read_surface_flinger_fps,
                runner.read_surface_flinger_display_latency_sample,
                runner.time.monotonic,
                runner.emit,
            ) = originals

        self.assertEqual(probe_count, len(runner.gfxinfo_targets("com.example.game", 42)))

    def test_fps_loop_uses_240hz_ring_budget_and_fixed_deadline(self) -> None:
        refresh_ns = 4_166_667
        clock = [100.0]
        waits: list[float] = []

        class OneShotStop:
            stopped = False

            def is_set(self) -> bool:
                return self.stopped

            def wait(self, delay: float) -> bool:
                waits.append(delay)
                self.stopped = True
                return True

        originals = (
            runner.read_surface_flinger_layer_latency_samples,
            runner.time.monotonic,
            runner.emit,
        )

        def read_layer(_serial, _package, tracker):
            clock[0] += 0.12
            tracker.selected_state.refresh_period_ns = refresh_ns
            return [], True

        try:
            runner.read_surface_flinger_layer_latency_samples = read_layer  # type: ignore[assignment]
            runner.time.monotonic = lambda: clock[0]  # type: ignore[method-assign]
            runner.emit = lambda *_: None  # type: ignore[assignment]

            runner.fps_loop("", 42, "com.example.game", 1.0, OneShotStop())  # type: ignore[arg-type]
        finally:
            (
                runner.read_surface_flinger_layer_latency_samples,
                runner.time.monotonic,
                runner.emit,
            ) = originals

        expected_period = (runner.SURFACE_LATENCY_RING_CAPACITY // 2) * refresh_ns / 1_000_000_000.0
        self.assertEqual(len(waits), 1)
        self.assertAlmostEqual(waits[0], expected_period - 0.12, places=5)
        self.assertLess(waits[0], 0.2)

    def test_fps_loop_searches_all_gfxinfo_targets_for_exact_display_present_time(self) -> None:
        approximate = runner.GfxInfoResult(
            runner.TimestatsSample(fps=58.0, target="42", ordered_frames=True, approximate=True),
            "42",
            "gfxinfo-framecompleted",
        )
        exact = runner.GfxInfoResult(
            runner.TimestatsSample(
                fps=60.0,
                target="com.example.game",
                jank=0.0,
                big_jank=0.0,
                frame_time_ms=16.7,
                frame_count=60,
                window_sec=1.0,
                has_frame_observation=True,
                has_jank_metrics=True,
                ordered_frames=True,
            ),
            "com.example.game",
            "gfxinfo-display-present",
        )
        probed: list[str] = []
        emitted: list[tuple[str, dict]] = []

        class OneShotStop:
            stopped = False

            def is_set(self) -> bool:
                return self.stopped

            def wait(self, _: float) -> bool:
                self.stopped = True
                return True

        originals = (
            runner.read_surface_flinger_layer_latency_samples,
            runner.read_gfxinfo_sample,
            runner.read_surface_refresh_period_ns,
            runner.prepare_surface_flinger_timestats,
            runner.read_surface_flinger_timestats_sample,
            runner.read_surface_flinger_fps,
            runner.read_surface_flinger_display_latency_sample,
            runner.emit,
        )

        def read_gfxinfo(_serial, target, _seen, _state, **_kwargs):
            probed.append(target)
            return approximate if target == "42" else exact

        try:
            runner.read_surface_flinger_layer_latency_samples = lambda *_: ([], False)  # type: ignore[assignment]
            runner.read_gfxinfo_sample = read_gfxinfo  # type: ignore[assignment]
            runner.read_surface_refresh_period_ns = lambda *_: 16_666_667  # type: ignore[assignment]
            runner.prepare_surface_flinger_timestats = lambda *_: (True, False)  # type: ignore[assignment]
            runner.read_surface_flinger_timestats_sample = lambda *_args, **_kwargs: runner.TimestatsSample(  # type: ignore[assignment]
                fps=59.0,
                target="SurfaceView[com.example.game]",
                histogram={16: 59},
                approximate=True,
            )
            runner.read_surface_flinger_fps = lambda *_: (None, "")  # type: ignore[assignment]
            runner.read_surface_flinger_display_latency_sample = lambda *_: None  # type: ignore[assignment]
            runner.emit = lambda kind, payload: emitted.append((kind, payload))  # type: ignore[assignment]

            runner.fps_loop("", 42, "com.example.game", 1.0, OneShotStop())  # type: ignore[arg-type]
        finally:
            (
                runner.read_surface_flinger_layer_latency_samples,
                runner.read_gfxinfo_sample,
                runner.read_surface_refresh_period_ns,
                runner.prepare_surface_flinger_timestats,
                runner.read_surface_flinger_timestats_sample,
                runner.read_surface_flinger_fps,
                runner.read_surface_flinger_display_latency_sample,
                runner.emit,
            ) = originals

        self.assertEqual(probed, ["42", "com.example.game"])
        self.assertEqual(len(emitted), 1)
        self.assertEqual(emitted[0][1]["source"], "adb-gfxinfo-framestats")
        self.assertEqual(emitted[0][1]["frame_source"], "gfxinfo-display-present")

    def test_fps_loop_reprobes_exact_display_gfxinfo_without_cooldown(self) -> None:
        probe_count = 0

        class TwoShotStop:
            stopped = False
            waits = 0

            def is_set(self) -> bool:
                return self.stopped

            def wait(self, _: float) -> bool:
                self.waits += 1
                self.stopped = self.waits >= 2
                return self.stopped

        originals = (
            runner.read_surface_flinger_layer_latency_samples,
            runner.read_gfxinfo_sample,
            runner.read_surface_refresh_period_ns,
            runner.prepare_surface_flinger_timestats,
            runner.read_surface_flinger_timestats_sample,
            runner.read_surface_flinger_fps,
            runner.read_surface_flinger_display_latency_sample,
            runner.emit,
        )

        def read_gfxinfo(_serial, _target, _seen, state, **_kwargs):
            nonlocal probe_count
            probe_count += 1
            state["frame_exact_display"] = True
            return None

        try:
            runner.read_surface_flinger_layer_latency_samples = lambda *_: ([], False)  # type: ignore[assignment]
            runner.read_gfxinfo_sample = read_gfxinfo  # type: ignore[assignment]
            runner.read_surface_refresh_period_ns = lambda *_: 16_666_667  # type: ignore[assignment]
            runner.prepare_surface_flinger_timestats = lambda *_: (True, False)  # type: ignore[assignment]
            runner.read_surface_flinger_timestats_sample = lambda *_args, **_kwargs: runner.TimestatsSample(  # type: ignore[assignment]
                fps=60.0,
                target="SurfaceView[com.example.game]",
                histogram={16: 60},
                approximate=True,
            )
            runner.read_surface_flinger_fps = lambda *_: (None, "")  # type: ignore[assignment]
            runner.read_surface_flinger_display_latency_sample = lambda *_: None  # type: ignore[assignment]
            runner.emit = lambda *_: None  # type: ignore[assignment]

            runner.fps_loop("", 42, "com.example.game", 1.0, TwoShotStop())  # type: ignore[arg-type]
        finally:
            (
                runner.read_surface_flinger_layer_latency_samples,
                runner.read_gfxinfo_sample,
                runner.read_surface_refresh_period_ns,
                runner.prepare_surface_flinger_timestats,
                runner.read_surface_flinger_timestats_sample,
                runner.read_surface_flinger_fps,
                runner.read_surface_flinger_display_latency_sample,
                runner.emit,
            ) = originals

        self.assertEqual(probe_count, 2)

    def test_gfxinfo_verified_idle_resume_preserves_real_long_frame(self) -> None:
        def frame_row(completed: int) -> str:
            return ",".join(["0", str(completed - 10_000_000), "0", str(completed), str(completed)])

        stdout = "\n".join(
            [
                "---PROFILEDATA---",
                "Flags,IntendedVsync,Vsync,FrameCompleted,DisplayPresentTime",
                frame_row(6_000_000_000),
                frame_row(6_016_000_000),
                frame_row(6_032_000_000),
                frame_row(6_048_000_000),
                frame_row(6_064_000_000),
                "---PROFILEDATAEND---",
            ]
        )
        state: dict[str, object] = {
            "last_end_ns": 1_000_000_000.0,
            "last_new_frame_time": 100.0,
            "last_zero_emit_time": 102.0,
            "recent_intervals_ms": [16.0, 16.0, 16.0],
        }
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type("Result", (), {"returncode": 0, "stdout": stdout})()  # type: ignore[assignment]
            runner.time.monotonic = lambda: 103.0  # type: ignore[method-assign]
            result = runner.read_gfxinfo_sample("", "com.example.game", {999}, state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNotNone(result)
        assert result is not None
        self.assertEqual(result.sample.jank, 1)
        self.assertEqual(result.sample.big_jank, 1)
        self.assertAlmostEqual(result.sample.frame_time_max_ms or 0, 5000.0, places=3)
        self.assertAlmostEqual(result.sample.resume_gap_ms or 0, 5000.0, places=3)

    def test_exact_gfxinfo_idle_window_emits_real_zero_fps(self) -> None:
        stdout = "\n".join(
            [
                "Window com.example.game/MainActivity",
                "---PROFILEDATA---",
                "Flags,IntendedVsync,Vsync,FrameCompleted,DisplayPresentTime",
                "0,1000000000,0,1016000000,1016000000",
                "0,1016000000,0,1032000000,1032000000",
                "---PROFILEDATA---",
            ]
        )
        seen = {1_016_000_000, 1_032_000_000}
        state: dict[str, object] = {
            "last_end_ns": 1_032_000_000.0,
            "last_new_frame_time": 100.0,
            "recent_intervals_ms": [16.0, 16.0, 16.0],
            "source_origin_timestamp_ns": 1_000_000_000.0,
            "source_elapsed_sec": 0.032,
            "source_sequence": 4,
        }
        original_adb = runner.adb
        original_time = runner.time.monotonic
        try:
            runner.adb = lambda *_, **__: type(  # type: ignore[assignment]
                "Result", (), {"returncode": 0, "stdout": stdout, "stderr": ""}
            )()
            runner.time.monotonic = lambda: 101.2  # type: ignore[method-assign]
            result = runner.read_gfxinfo_sample("", "com.example.game", seen, state)
        finally:
            runner.adb = original_adb  # type: ignore[assignment]
            runner.time.monotonic = original_time  # type: ignore[method-assign]

        self.assertIsNotNone(result)
        assert result is not None
        self.assertEqual(result.sample.fps, 0.0)
        self.assertEqual(result.sample.frame_count, 0)
        self.assertTrue(result.sample.ordered_frames)
        self.assertTrue(result.sample.has_jank_metrics)
        self.assertEqual(result.sample.jank, 0.0)
        self.assertIsNone(result.sample.frame_time_ms)
        self.assertTrue(result.sample.idle_window)
        self.assertTrue(result.sample.no_present_frames)
        self.assertTrue(result.sample.target_verified)
        self.assertEqual(result.sample.source_sequence, 5)
        self.assertAlmostEqual(result.sample.source_elapsed_sec or 0, 1.232)

    def test_meminfo_parser_prefers_pss_and_ignores_total_rss(self) -> None:
        stdout = """
Applications Memory Usage (in Kilobytes):
TOTAL PSS: 204800
TOTAL RSS: 999999
TOTAL SWAP PSS: 10
"""

        self.assertAlmostEqual(runner.parse_meminfo_pss_mb(stdout) or 0, 200.0)
        self.assertAlmostEqual(runner.parse_meminfo_rss_mb(stdout) or 0, 999999 / 1024.0)

    def test_wechat_appbrand_targets_do_not_fall_back_to_host_package(self) -> None:
        self.assertEqual(runner.gfxinfo_targets("com.tencent.mm:appbrand1", 1690), ["1690", "com.tencent.mm:appbrand1"])
        self.assertEqual(runner.gfxinfo_targets("com.example.game:render", 42), ["42", "com.example.game:render", "com.example.game"])


if __name__ == "__main__":
    unittest.main()
