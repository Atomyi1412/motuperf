from __future__ import annotations

import argparse
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "csharp_perf_monitor" / "tools"))

import android_device_frame_runner as runner


def test_device_frame_sample_maps_to_existing_fps_contract():
    events = runner.frame_record_to_events({
        "kind": "sample",
        "window": 7,
        "fps": 60.0,
        "frame_count": 120,
        "observation_ms": 2000.0,
        "frame_time_ms": 16.7,
        "frame_time_mean_ms": 16.8,
        "frame_time_p95_ms": 20.1,
        "frame_time_max_ms": 33.4,
        "jank": 1,
        "big_jank": 0,
        "source": "perfetto_surface_present_fence",
    }, source_elapsed_sec=5.25)

    assert len(events) == 1
    kind, payload = events[0]
    assert kind == "fps"
    assert payload["fps"] == 60.0
    assert payload["frame_source"] == "perfetto_surface_present_fence"
    assert payload["source_sequence"] == 7
    assert payload["frame_observation_ms"] == 2000.0
    assert payload["frame_time_p95_ms"] == 20.1
    assert payload["target_verified"] is True
    assert payload["ordered_frames"] is True


def test_unavailable_and_fatal_never_become_zero_fps_or_silent_fallback():
    unavailable = runner.frame_record_to_events({
        "kind": "unavailable",
        "code": "target_surface_unverified",
        "message": "没有目标 Surface",
    })
    fatal = runner.frame_record_to_events({
        "kind": "fatal",
        "code": "trace_failed",
        "message": "trace capture failed",
    })

    assert unavailable[0][0] == "status"
    assert fatal[0][0] == "status"
    assert unavailable[0][1]["code"] == "target_surface_unverified"
    assert "其他指标继续采集" in fatal[0][1]["message"]


def test_invalid_sample_is_explicitly_missing():
    events = runner.frame_record_to_events({
        "kind": "sample",
        "window": 1,
        "fps": "not-a-number",
    })
    assert events[0][0] == "status"
    assert events[0][1]["code"] == "device_frame_invalid_fps"


def test_metrics_child_disables_legacy_fps_but_preserves_other_switch_flags():
    config = argparse.Namespace(
        adb="adb.exe",
        serial="serial",
        pid=42,
        package="com.example.game",
        target_name="com.example.game",
        target_start_time_ticks=123,
        interval=1.0,
        no_memory=True,
        no_cpu=False,
        no_temperature=True,
        no_thermal_state=False,
    )
    command = runner.build_metrics_command(config, "android_perf_runner.py", "python.exe")
    assert "--no-fps" in command
    assert command.count("--no-fps") == 1
    assert "--no-memory" in command
    assert "--no-cpu" not in command
    assert "--no-temperature" in command
