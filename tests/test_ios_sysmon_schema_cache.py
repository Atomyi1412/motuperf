from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "csharp_perf_monitor" / "tools"))

import ios_sysmon_schema_cache as schema_cache  # noqa: E402


class IosSysmonSchemaCacheTests(unittest.TestCase):
    def test_schema_round_trip_is_device_scoped(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            cache_dir = Path(directory)
            schema_cache.save_sysmon_schema(
                "ipad-a",
                ["pid", "name", "cpuUsage"],
                ["vmPressure"],
                cache_dir=cache_dir,
                now=100.0,
            )

            self.assertEqual(
                schema_cache.load_sysmon_schema(
                    "ipad-a",
                    cache_dir=cache_dir,
                    now=101.0,
                ),
                (["pid", "name", "cpuUsage"], ["vmPressure"]),
            )
            self.assertIsNone(
                schema_cache.load_sysmon_schema(
                    "ipad-b",
                    cache_dir=cache_dir,
                    now=101.0,
                )
            )

    def test_stale_or_malformed_schema_is_ignored(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            cache_dir = Path(directory)
            schema_cache.save_sysmon_schema(
                "ipad-a",
                ["pid", "name"],
                ["vmPressure"],
                cache_dir=cache_dir,
                now=100.0,
            )

            self.assertIsNone(
                schema_cache.load_sysmon_schema(
                    "ipad-a",
                    cache_dir=cache_dir,
                    now=200.0,
                    max_age_seconds=60.0,
                )
            )

            path = schema_cache.sysmon_schema_cache_path("ipad-a", cache_dir=cache_dir)
            path.write_text("not-json", encoding="utf-8")
            self.assertIsNone(
                schema_cache.load_sysmon_schema(
                    "ipad-a",
                    cache_dir=cache_dir,
                    now=101.0,
                )
            )


if __name__ == "__main__":
    unittest.main()
