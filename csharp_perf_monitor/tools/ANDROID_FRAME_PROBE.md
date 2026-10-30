# Android frame experiments (v0.56.0)

The desktop uses its existing SurfaceFlinger/gfxinfo collector by default. The
following experiments do not replace it. They collect FPS/Jank/FrameTime only;
they do not install a monitoring agent, require root, or change display settings.

## Run a diagnostic

Use the package's `runtime/python/python.exe` and `runtime/android/adb.exe`.
Supply the selected running process PID/package and an explicit output directory
on a data drive. With more than one connected phone, `--serial` is mandatory.

```powershell
runtime/python/python.exe tools/android_frame_probe.py `
  --adb runtime/android/adb.exe --serial SERIAL --pid PID --package com.example.game `
  --trace-seconds 5 --trace-processor D:/Tools/trace_processor_shell.exe `
  --device-batch --output D:/MoTuPerf-diagnostics/report.json
```

The output directory must already exist. The official Perfetto trace processor is
optional and is not downloaded or executed automatically. Obtain it from
https://perfetto.dev/docs/quickstart/trace-analysis and supply its full path.
No processor means `official_trace_processor_required`, not a false successful
trace result. `--device-batch` can still run independently.

The diagnostic writes schema-version-2 JSON and an at-most-30-second, 8 MiB trace
buffer. Its unique phone-side trace file is deleted after transfer. Local trace
files remain beside the report for analysis and can be deleted afterwards.

## Interpret the report

1. `surface.target_verified` confirms target ownership. Name-only matches do not
   qualify for exact metrics. PID start time and Surface ownership are checked
   again after capture; changed or ambiguous targets stay unavailable.
2. `trace_capture.status=captured` means a trace was transferred, not that usable
   target frames exist. `is_device_side_trace_validated=true` additionally requires
   target-specific presents and successful integrity checks.
3. FrameTimeline uses actual SurfaceFrame presentation endpoints for the selected
   PID and exact layer. Native games without app FrameTimeline tokens can use
   exact-layer `PresentFenceSignaled` events from `android.surfaceflinger.frame`.
   Display-wide frames, `doFrame`, queue and GPU completion are not substitutes.
4. Both sources reuse the same ordered interval/FPS/Jank calculations as the
   product. Unfinished/dropped frames, unknown presentation types, invalid order,
   duplicate identities and trace loss are handled explicitly. Historical
   SurfaceFlinger timestamps cannot be counted as new frames.
5. `device_batch_experiment` is a separate interval, not simultaneous trace ground
   truth. A persistent ADB shell polls SurfaceFlinger on the phone and transmits
   two snapshots per batch. This reduces host ADB launches and round trips; the
   phone still executes one `dumpsys` per snapshot. Poll and batch counts are
   reported. Queue/backpressure, per-query/overall timeouts, sequence gaps and
   ring overlap are checked. The nominal extra batching wait is 250 ms plus query
   time; it is not zero-latency streaming.
6. Device CPU is total device activity, not collector-only CPU. Short battery
   samples, particularly while USB charging, cannot establish power overhead.
   Compare repeated runs in the same game/brightness/refresh/thermal conditions;
   assess battery cost over a longer controlled discharge test.

## Remaining acceptance matrix

The connected vivo Android 16 native-game run is one compatibility result.
Other vendors/older Android versions, physical 120/144 Hz transitions, Surface
recreation, background/resume, slow/disconnected ADB, multi-hour sessions, and
controlled CPU/power comparisons remain separate acceptance work before making
either experiment the default desktop source. Synthetic regressions cover
refresh transitions, idle/resume, slow-memory isolation, bounded batch drains,
partial data and timestamp/identity failures.
