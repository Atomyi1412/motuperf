import unittest
from unittest.mock import Mock, patch

from tests.test_harmony_perf_runner import options, runner, snapshot, stat


class HarmonyTemperatureTests(unittest.TestCase):
    def test_malformed_pairs_do_not_reuse_a_previous_sensor(self):
        for invalid in (
            "Type: soc\nTemperature: denied\nTemperature: 42500\n",
            "Type: soc\nother sensor output\nTemperature: 42500\n",
            "Temperature: 42500\n",
            "Type: soc\nTemperature: nan\n",
            "Type: soc\nTemperature: 42.5\n",
            "Type: soc\nTemperature: " + "9" * 5000 + "\n",
            "Type: soc\nTemperature: 42500\n[Failed] permission denied\n",
        ):
            with self.subTest(text=invalid[:100]):
                self.assertEqual(runner.parse_harmony_thermal_temperatures(invalid), {})

    def test_conflicting_or_invalid_duplicate_sensor_is_missing(self):
        for duplicate in ("43000", "invalid", "999999"):
            with self.subTest(duplicate=duplicate):
                output = ("Type: soc\nTemperature: 42500\nType: soc\nTemperature: " + duplicate
                          + "\nType: soc\nTemperature: 42500\nType: battery\nTemperature: 0\n")
                self.assertEqual(runner.parse_harmony_thermal_temperatures(output), {"battery": 0})

    def test_readable_sensors_remain_independent_and_keep_millidegrees(self):
        self.assertEqual(runner.parse_harmony_thermal_temperatures(
            "------------------[ability]------------------\nType: soc\nTemperature: 42500\n"
            "Type: battery\nTemperature: -1000\nType: shell\nTemperature: 120000\n"
            "Type: sensor1\nTemperature: 120001\nType: soc\nTemperature: 42500\n"),
            {"soc": 42.5, "battery": -1, "shell": 120})

    def test_sysfs_success_skips_manager_and_preserves_existing_names(self):
        hdc = Mock()
        hdc.shell.return_value = "thermal_zone0|soc|42500\n"
        self.assertEqual(runner.read_device_temperatures(hdc),
                         ({"soc (thermal_zone0)": 42.5}, "hdc-sysfs-thermal-millidegrees"))
        self.assertEqual(hdc.shell.call_count, 1)

    def test_empty_or_denied_sysfs_uses_optional_manager(self):
        for first in ("", "Permission denied", runner.HdcFailure("timeout")):
            with self.subTest(first=str(first)):
                hdc = Mock()
                hdc.shell.side_effect = [first, "Type: soc\nTemperature: 42500\n"]
                self.assertEqual(runner.read_device_temperatures(hdc),
                                 ({"soc (Thermal Manager)": 42.5}, "hdc-hidumper-3303-temperature"))
                self.assertEqual(hdc.shell.call_args.args[0], "hidumper -s 3303 -a -t")
                self.assertTrue(all(call.kwargs["timeout"] <= 1 for call in hdc.shell.call_args_list))

    def test_temperature_failure_does_not_stop_cpu_memory_or_retry_each_tick(self):
        now = [0.0]
        cycle = [0]
        events = []
        hdc = Mock()

        def shell(command, **kwargs):
            if command.startswith("printf '__MOTUPERF_"):
                cycle[0] += 1
                return snapshot(ticks=30 + 10 * cycle[0], cpu=(
                    f"cpu0 {100 + 10 * cycle[0]} 0 0 100\ncpu1 {100 + 10 * cycle[0]} 0 0 100"))
            if command == "cat /proc/42/stat":
                return stat()
            raise runner.HdcFailure("denied")

        def sleep(_delay):
            now[0] += 1
            if now[0] == 8:
                raise KeyboardInterrupt

        hdc.shell.side_effect = shell
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "monotonic", side_effect=lambda: now[0]), \
             patch.object(runner.time, "sleep", side_effect=sleep):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(no_temperature=False), hdc)
        self.assertEqual(sum(kind == "cpu" for kind, _ in events), 7)
        self.assertEqual(sum(kind == "memory" for kind, _ in events), 8)
        self.assertFalse(any(kind in ("temperature", "thermal_state", "fatal") for kind, _ in events))
        self.assertEqual(sum(call.args[0] == "hidumper -s 3303 -a -t" for call in hdc.shell.call_args_list), 2)
        self.assertEqual(sum(data.get("code") == "harmony_temperature_unavailable" for _, data in events), 1)

    def test_disabled_temperature_never_queries_manager_or_cluster_levels(self):
        hdc = Mock()
        hdc.shell.side_effect = [snapshot(), stat()]
        events = []
        with patch.object(runner, "emit", side_effect=lambda kind, data: events.append((kind, data))), \
             patch.object(runner.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.collect(options(no_temperature=True, no_thermal_state=False), hdc)
        self.assertEqual(hdc.shell.call_count, 2)
        self.assertFalse(any(kind in ("temperature", "thermal_state") for kind, _ in events))

    def test_cancellation_is_not_swallowed_by_optional_fallback(self):
        hdc = Mock()
        hdc.shell.side_effect = KeyboardInterrupt
        with self.assertRaises(KeyboardInterrupt):
            runner.read_device_temperatures(hdc)
        self.assertEqual(hdc.shell.call_count, 1)
