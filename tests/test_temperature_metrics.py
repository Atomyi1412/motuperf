from __future__ import annotations

import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "csharp_perf_monitor" / "tools"))

from temperature_metrics import (  # noqa: E402
    parse_android_battery_temperature,
    parse_android_thermal_status,
    parse_android_thermalservice,
    parse_ios_battery_temperature,
    parse_ios_thermal_state,
    ios_thermal_state_unavailable_reason,
)


class TemperatureMetricTests(unittest.TestCase):
    def test_android_thermal_status_accepts_only_the_aggregate_seven_level_field(self) -> None:
        expected = {
            0: "none",
            1: "light",
            2: "moderate",
            3: "severe",
            4: "critical",
            5: "emergency",
            6: "shutdown",
        }
        for level, name in expected.items():
            with self.subTest(level=level):
                self.assertEqual(
                    parse_android_thermal_status(f"Thermal Status: {level}\n"),
                    (level, name),
                )

        self.assertIsNone(parse_android_thermal_status("Thermal Status: -1\n"))
        self.assertIsNone(parse_android_thermal_status("Thermal Status: 7\n"))
        self.assertIsNone(parse_android_thermal_status("Thermal Status: 2.0\n"))
        self.assertIsNone(
            parse_android_thermal_status(
                "Temperature{mValue=46.25, mType=0, mName=cpu7, mStatus=4}\n"
            )
        )

    def test_android_thermalservice_keeps_hottest_sensor_per_category(self) -> None:
        output = """
        Cached temperatures:
          Temperature{mValue=41.5, mType=0, mName=cpu0, mStatus=0}
          Temperature{mValue=46.25, mType=0, mName=cpu7, mStatus=1}
          Temperature{mValue=39.0, mType=1, mName=gpu, mStatus=0}
          Temperature{mValue=33.8, mType=2, mName=battery, mStatus=0}
          Temperature{mValue=35.2, mType=3, mName=skin, mStatus=0}
          Temperature{mValue=nan, mType=9, mName=npu, mStatus=0}
        """

        self.assertEqual(
            parse_android_thermalservice(output),
            {"CPU": 46.25, "GPU": 39.0, "Battery": 33.8, "Skin": 35.2},
        )

    def test_android_battery_fallback_uses_tenths_of_a_degree(self) -> None:
        self.assertEqual(parse_android_battery_temperature("temperature: 328\n"), 32.8)
        self.assertIsNone(parse_android_battery_temperature("temperature: 9999\n"))

    def test_ios_ioregistry_temperature_uses_hundredths_of_a_degree(self) -> None:
        self.assertEqual(parse_ios_battery_temperature({"Temperature": 3289}), 32.89)
        self.assertIsNone(parse_ios_battery_temperature({"Temperature": "nan"}))
        self.assertIsNone(parse_ios_battery_temperature({}))

    def test_ios_thermal_state_accepts_only_real_four_level_field(self) -> None:
        self.assertEqual(
            parse_ios_thermal_state({597: {"energy.thermalstate.cost": 2}}, 597),
            (2, "serious"),
        )
        self.assertEqual(
            parse_ios_thermal_state({"597": {"energy.thermalstate.cost": 3.0}}, 597),
            (3, "critical"),
        )
        self.assertIsNone(
            parse_ios_thermal_state({597: {"energy.inducedthermalstate.cost": 2}}, 597)
        )
        self.assertIsNone(parse_ios_thermal_state({597: {"energy.thermalstate.cost": 4}}, 597))
        self.assertIsNone(parse_ios_thermal_state({597: {"energy.thermalstate.cost": 1.5}}, 597))

    def test_ios_thermal_state_reports_missing_source(self) -> None:
        self.assertEqual(ios_thermal_state_unavailable_reason({}, 597), "empty_response")
        self.assertEqual(ios_thermal_state_unavailable_reason({"598": {}}, 597), "pid_attributes_missing")
        self.assertEqual(ios_thermal_state_unavailable_reason({597: {}}, 597), "thermal_state_field_missing")
        self.assertEqual(
            ios_thermal_state_unavailable_reason({597: {"energy.thermalstate.cost": 4}}, 597),
            "thermal_state_field_invalid",
        )
        self.assertIsNone(ios_thermal_state_unavailable_reason({597: {"energy.thermalstate.cost": 2}}, 597))


if __name__ == "__main__":
    unittest.main()
