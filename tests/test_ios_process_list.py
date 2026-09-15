from __future__ import annotations

import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "csharp_perf_monitor" / "tools"))

import ios_process_list as process_list  # noqa: E402


def _app(pid: int, name: str, bundle: str) -> dict[str, object]:
    return {
        "pid": pid,
        "name": name,
        "bundleIdentifier": bundle,
        "displayLocalizedAppName": name,
        "isApplication": True,
    }


def _web(pid: int) -> dict[str, object]:
    return {
        "pid": pid,
        "name": "com.apple.WebKit.WebContent",
        "isApplication": False,
    }


class IosProcessOwnershipTests(unittest.TestCase):
    def test_process_discovery_persists_the_canonical_sysmontap_schema(self) -> None:
        source = (ROOT / "csharp_perf_monitor" / "tools" / "ios_process_list.py").read_text(
            encoding="utf-8"
        )

        self.assertIn("save_sysmon_schema(", source)
        self.assertIn("process_attributes_cls.__dataclass_fields__", source)
        self.assertIn("system_attributes_cls.__dataclass_fields__", source)

    def test_usable_responsible_pid_wins(self) -> None:
        processes = [_app(100, "WeChat", "com.tencent.xin"), _web(200)]
        rows = [
            {"pid": 100, "coalitionID": 10},
            {"pid": 200, "responsiblePID": 100, "coalitionID": 10, "startAbsTime": 1234},
        ]

        child = process_list.enrich_processes(processes, rows)[1]

        self.assertTrue(child["ownershipVerified"])
        self.assertEqual(child["ownershipSource"], "responsible-pid")
        self.assertEqual(child["ownerPID"], 100)
        self.assertEqual(child["ownerBundleIdentifier"], "com.tencent.xin")
        self.assertEqual(child["startAbsTime"], 1234)

    def test_extensionkit_responsible_pid_uses_unique_application_coalition(self) -> None:
        processes = [_app(300, "appstoreCN", "com.apple.AppStore"), _web(301)]
        rows = [
            {"pid": 300, "coalitionID": 77},
            {"pid": 301, "responsiblePID": 1, "coalitionID": 77},
        ]

        child = process_list.enrich_processes(processes, rows)[1]

        self.assertTrue(child["ownershipVerified"])
        self.assertEqual(child["ownershipSource"], "coalition")
        self.assertEqual(child["ownerPID"], 300)
        self.assertEqual(child["ownerBundleIdentifier"], "com.apple.AppStore")

    def test_multiple_application_processes_make_coalition_ambiguous(self) -> None:
        processes = [
            _app(400, "HostA", "com.example.a"),
            _app(401, "HostB", "com.example.b"),
            _web(402),
        ]
        rows = [
            {"pid": 400, "coalitionID": 88},
            {"pid": 401, "coalitionID": 88},
            {"pid": 402, "responsiblePID": 1, "coalitionID": 88},
        ]

        child = process_list.enrich_processes(processes, rows)[2]

        self.assertFalse(child["ownershipVerified"])
        self.assertTrue(child["ownershipAmbiguous"])
        self.assertEqual(child["ownershipSource"], "ambiguous-coalition")
        self.assertEqual(child["ownerPID"], 0)

    def test_missing_sysmontap_data_does_not_guess_owner(self) -> None:
        child = process_list.enrich_processes([_web(500)], [])[0]

        self.assertFalse(child["ownershipVerified"])
        self.assertFalse(child["ownershipAmbiguous"])
        self.assertEqual(child["ownershipSource"], "unavailable")
        self.assertEqual(child["ownerBundleIdentifier"], "")

    def test_unique_running_user_application_is_foreground(self) -> None:
        processes = [
            _app(600, "WidgetRenderer_Default", "com.apple.chrono.WidgetRenderer-Default"),
            _app(700, "NativeGame", "com.example.game"),
        ]
        notifications = [
            {
                "pid": 600,
                "state_description": "Suspended",
                "execName": "/Applications/WidgetRenderer_Default.app",
            },
            {
                "pid": 700,
                "state_description": "Running",
                "execName": "/private/var/containers/Bundle/Application/UUID/NativeGame.app",
            },
        ]

        enriched = process_list.apply_application_states(processes, notifications)

        self.assertFalse(enriched[0]["foregroundApplication"])
        self.assertEqual(enriched[0]["applicationState"], "Suspended")
        self.assertTrue(enriched[1]["foregroundApplication"])
        self.assertEqual(enriched[1]["applicationState"], "Running")

    def test_multiple_running_user_applications_are_not_guessed(self) -> None:
        processes = [
            _app(800, "GameA", "com.example.a"),
            _app(801, "GameB", "com.example.b"),
        ]
        notifications = [
            {
                "pid": 800,
                "state_description": "Running",
                "execName": "/private/var/containers/Bundle/Application/A/GameA.app",
            },
            {
                "pid": 801,
                "state_description": "Running",
                "execName": "/private/var/containers/Bundle/Application/B/GameB.app",
            },
        ]

        enriched = process_list.apply_application_states(processes, notifications)

        self.assertFalse(any(process["foregroundApplication"] for process in enriched))

    def test_running_system_service_is_not_a_foreground_candidate(self) -> None:
        processes = [
            _app(900, "Family", "com.apple.family"),
            _app(901, "NativeGame", "com.example.game"),
        ]
        notifications = [
            {
                "pid": 900,
                "state_description": "Running",
                "execName": "/Applications/Family.app",
            },
            {
                "pid": 901,
                "state_description": "Running",
                "execName": "/private/var/containers/Bundle/Application/C/NativeGame.app",
            },
        ]

        enriched = process_list.apply_application_states(processes, notifications)

        self.assertFalse(enriched[0]["foregroundApplication"])
        self.assertTrue(enriched[1]["foregroundApplication"])


if __name__ == "__main__":
    unittest.main()
