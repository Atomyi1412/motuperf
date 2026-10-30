from __future__ import annotations

import sys
import threading
import time
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "csharp_perf_monitor" / "tools"))
import android_perf_runner as runner


def test_batch_tail_is_sent_without_another_frame_and_close_is_terminal():
    sent = []
    ready = threading.Event()
    def emit(kind, payload):
        sent.append((kind, payload))
        ready.set()
    batch = runner.FrameBatchEmitter(flush_interval=0.03, emit_func=emit)
    try:
        batch.add({"source_sequence": 1})
        assert ready.wait(1), "The final frame waited for a subsequent add/stop"
        batch.close()
        batch.add({"source_sequence": 2})
        batch.flush()
        assert len(sent) == 1
        assert sent[0][1]["events"][0]["payload"]["source_sequence"] == 1
    finally:
        batch.close()


def test_concurrent_flushes_preserve_all_event_order_and_do_not_duplicate():
    sent = []
    batch = runner.FrameBatchEmitter(flush_interval=0.001, max_events=3,
                                     emit_func=lambda _, p: sent.extend(e["payload"]["index"] for e in p["events"]))
    try:
        for i in range(100):
            batch.add({"index": i})
        batch.close()
        assert sent == list(range(100))
    finally:
        batch.close()


def test_slow_memory_fallback_does_not_block_cpu_or_queue_requests():
    stop = threading.Event()
    entered = threading.Event()
    release = threading.Event()
    calls = []
    cpu_samples = []
    reads = []
    def slow_memory(*args, **kwargs):
        calls.append(args)
        entered.set()
        release.wait(2)
        return runner.MemorySample(42, "pss", "slow")
    def snapshot(*args):
        reads.append(args)
        return runner.LowFrequencySnapshot(runner.CpuSnapshot(100 * len(reads), 10 * len(reads), 1), None, "")
    def emit(kind, payload):
        if kind == "cpu":
            cpu_samples.append(payload)
            if len(cpu_samples) == 2:
                stop.set()
    try:
        with patch.object(runner, "read_memory_sample", side_effect=slow_memory), \
             patch.object(runner, "read_low_frequency_snapshot", side_effect=snapshot), \
             patch.object(runner, "emit", side_effect=emit):
            worker = threading.Thread(target=runner.low_frequency_resource_loop, args=("", 42, 0.5, stop))
            worker.start()
            assert entered.wait(1)
            worker.join(1.8)
            assert not worker.is_alive(), "CPU stalled behind dumpsys meminfo"
            assert len(cpu_samples) == 2
            assert len(calls) == 1
            assert [r[2] for r in reads] == [True, False, False]
            release.set()
    finally:
        stop.set()
        release.set()


def test_memory_fallback_drops_result_after_capture_stops():
    stop = threading.Event()
    entered = threading.Event()
    release = threading.Event()
    sent = []
    def query(*args, **kwargs):
        entered.set()
        release.wait(1)
        return runner.MemorySample(42, "pss", "slow")
    with patch.object(runner, "read_memory_sample", side_effect=query), \
         patch.object(runner, "emit", side_effect=lambda *args: sent.append(args)):
        fallback = runner.MemoryFallbackWorker("serial", 42, stop)
        fallback.request("")
        assert entered.wait(1)
        stop.set()
        release.set()
        fallback.thread.join(1)
        assert not sent
