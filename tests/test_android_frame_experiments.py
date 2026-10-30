from __future__ import annotations

import sys
import io
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "csharp_perf_monitor" / "tools"))
import android_frame_probe as probe
import android_frame_trace as trace
import android_frame_experiment as experiment
from android_frame_transport import LatencySnapshot, summarize_snapshots
import android_frame_transport as transport


def latency(*points):
    return "16666667\n" + "\n".join(f"{t} {t} {t}" for t in points)


def test_identical_snapshots_cannot_reuse_history_as_current_fps():
    history = latency(1_000_000_000, 1_016_666_667)
    assert probe.frame_metrics_from_latency(history, history, target_verified=True) == {
        "available": False, "reason": "no_new_present_timestamps"}


def test_latency_cannot_bridge_wrapped_ring_or_unverified_owner():
    before = latency(1_000_000_000, 1_016_666_667)
    assert not probe.frame_metrics_from_latency(before, latency(3_000_000_000, 3_016_666_667), target_verified=True)["available"]
    assert not probe.frame_metrics_from_latency(before, latency(1_016_666_667, 1_033_333_334))["available"]
    assert not probe.frame_metrics_from_latency(latency(1_000_000_000), latency(1_000_000_000, 1_000_100_000), target_verified=True)["available"]


def test_standard_atrace_category_does_not_claim_capture_permission():
    class Result:
        returncode = 0
        stdout = "gfx - Graphics\nview - View System\n"
        stderr = ""
    with patch.object(probe, "shell", return_value=Result()):
        cap = probe.trace_capabilities("adb", "s")
    assert cap["gfx_category_available"] is True
    assert cap["trace_permission"] == "tracefs_writable_only"


def row(token, presented, *, pid="42", layer="game#1", kind="On-time Present", dur="1000000"):
    return dict(pid=pid, layer_name=layer, ts=str(presented - int(dur)), dur=dur,
                surface_frame_token=str(token), present_type=kind)


def test_trace_filters_target_surface_and_dropped_frames_before_shared_jank():
    points = [1_000_000_000 + i * 16_666_667 for i in range(65)]
    rows = [row(i, p) for i, p in enumerate(points, 1)]
    rows += [row(300, 1_100_000_000, pid="99"), row(301, 1_120_000_000, layer="overlay#2"),
             row(302, 1_200_000_000, kind="Dropped Frame"), row(303, 2_200_000_000, dur="-1")]
    result = trace.frame_timeline_metrics(rows, 42, "game#1", True)
    assert result["available"]
    assert result["frame_count"] == 64
    assert abs(result["fps"] - 60) < 0.001
    assert result["dropped_frames"] == 1
    assert result["unfinished_frames"] == 1
    assert not trace.frame_timeline_metrics(rows, 42, "game#1", False)["available"]


def test_trace_ambiguous_tokens_and_bad_timing_are_unavailable():
    assert not trace.frame_timeline_metrics([row(1, 100), row(1, 200)], 42, "game#1", True)["available"]
    assert not trace.frame_timeline_metrics([row(1, 20_000_000), row(2, 10_000_000)], 42, "game#1", True)["available"]
    assert not trace.frame_timeline_metrics([row(0, 20_000_000), row(2, 30_000_000)], 42, "game#1", True)["available"]


def test_native_present_fences_require_exact_owner_layer_and_order():
    rows = [dict(name="PresentFenceSignaled", layer_name="game#1", frame_number=str(i), ts=str(1_000_000_000 + i * 16_666_667)) for i in range(65)]
    assert trace.graphics_frame_metrics(rows, "game#1", True)["available"]
    assert not trace.graphics_frame_metrics(rows, "game#1", False)["available"]
    assert not trace.graphics_frame_metrics(rows, "other#1", True)["available"]
    assert not trace.graphics_frame_metrics(rows + [rows[-1]], "game#1", True)["available"]


def test_device_batches_detect_sequence_loss_ring_wrap_and_static_history():
    a = LatencySnapshot(0, 10, latency(1_000_000_000))
    b = LatencySnapshot(1, 10.25, latency(1_000_000_000, 1_016_666_667, 1_033_333_334))
    assert summarize_snapshots([a, b], True)["metrics"]["available"]
    assert summarize_snapshots([a, a], True)["discontinuities"] == 1
    assert not summarize_snapshots([a, LatencySnapshot(2, 10.25, b.stdout)], True)["metrics"]["available"]
    assert not summarize_snapshots([a, LatencySnapshot(1, 10.25, latency(2_000_000_000))], True)["metrics"]["available"]
    assert not summarize_snapshots([a, LatencySnapshot(1, 10.25, a.stdout)], True)["metrics"]["available"]


def test_refresh_switch_static_resume_preserves_measured_intervals():
    points = [1_000_000_000]
    snapshots = [LatencySnapshot(0, 10, latency(*points))]
    for i, hz in enumerate((60, 120, 144, 60), 1):
        step = round(1_000_000_000 / hz)
        start = points[-1]
        points.extend(start + step * n for n in range(1, 21))
        snapshots.append(LatencySnapshot(i, 10 + i * .25, latency(*points[-128:])))
    snapshots.append(LatencySnapshot(5, 11.25, snapshots[-1].stdout))
    snapshots.append(LatencySnapshot(6, 11.5, latency(*points[-128:])))
    points.append(points[-1] + 1_500_000_000)
    snapshots.append(LatencySnapshot(7, 11.75, latency(*points[-128:])))
    result = summarize_snapshots(snapshots, True)
    assert result["discontinuities"] == 0
    assert result["metrics"]["available"]
    assert result["metrics"]["frame_time_max_ms"] == 1500
    assert result["unchanged_snapshots"] == 2


def test_persistent_transport_rejects_partial_batch_and_starts_only_one_adb():
    class Process:
        returncode = 0
        stdout = io.StringIO("__MOTUPERF_SNAPSHOT__ 0 10\n" + latency(1_000_000_000) + "\n")
        def poll(self): return 0
        def wait(self, timeout): return 0
    with patch.object(transport.subprocess, "Popen", return_value=Process()) as spawn:
        result = transport.collect_device_batches("adb", "s", 42, "a'; touch bad; '", True, .5)
    assert spawn.call_count == 1
    assert result["status"] == "inconclusive"
    assert not result["metrics"]["available"]
    assert "dumpsys -t 2" in spawn.call_args.args[0][-1]


def test_failed_persistent_transport_is_structured_missing():
    with patch.object(transport.subprocess, "Popen", side_effect=OSError("missing")):
        result = transport.collect_device_batches("adb", "s", 42, "game#1", True, .5)
    assert result["status"] == "unavailable"
    assert not result["metrics"]["available"]


def test_probe_rejects_successful_trace_when_surface_changes():
    class Result:
        stdout = "36"
        stderr = ""
        returncode = 0
    surface = dict(selected_layer="game#1", target_verified=True, owner_pid=42)
    changed = dict(selected_layer="game#2", target_verified=True, owner_pid=42)
    trace_result = dict(status="captured", metrics=dict(available=True, fps=60),
                        integrity_checked=True, target_specific_frame_events_parsed=True)
    with patch.object(probe, "shell", return_value=Result()), \
         patch.object(probe, "trace_capabilities", return_value={}), \
         patch.object(probe, "resolve_surface", side_effect=[surface, changed]), \
         patch.object(probe, "target_identity", return_value=123), \
         patch.object(probe, "sample_cpu", return_value=None), \
         patch.object(probe, "sample_battery", return_value={}), \
         patch.object(probe.time, "sleep"), \
         patch.object(probe, "capture_frame_timeline", return_value=trace_result):
        report = probe.collect_report("adb", "s", 42, "game", 1, "tp", Path("unused"))
    assert not report["is_device_side_trace_validated"]
    assert not report["trace_capture"]["target_specific_frame_events_parsed"]
    assert not report["trace_capture"]["metrics"]["available"]
    assert not report["frame_metrics"]["available"]


def test_experiment_process_rows_only_returns_third_party_packages():
    class Result:
        returncode = 0
        stderr = ""

        def __init__(self, stdout):
            self.stdout = stdout

    with patch.object(experiment, "adb", side_effect=[
        Result("  PID NAME\n42 com.example.game\n43 com.android.systemui\n44 com.example.game:render\n"),
        Result("package:com.example.game\n"),
    ]):
        rows = experiment.process_rows("adb", "s")
    assert [(row["pid"], row["name"]) for row in rows] == [
        (42, "com.example.game"), (44, "com.example.game:render")
    ]
