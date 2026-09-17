using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpIosPerfMonitor
{
    public sealed class PerfCollector : IDisposable
    {
        private const double MaximumReasonableFps = 240.0;
        private const double MaximumReasonableFrameTimeMs = 60000.0;
        internal const double IosFpsFreshSeconds = 3.5;
        internal const double AndroidFpsFreshSeconds = 4.0;
        internal const double ProcessMetricFreshSeconds = 6.0;
        private readonly object _lock = new object();
        private readonly Queue<PerfSample> _pendingFrameSamples = new Queue<PerfSample>();
        private readonly Queue<PerfSample> _pendingProcessSamples = new Queue<PerfSample>();
        private readonly Queue<string> _stderrTail = new Queue<string>();
        private readonly SemaphoreSlim _sampleSignal = new SemaphoreSlim(0, int.MaxValue);
        private CaptureDiagnosticsLog _diagnosticsLog;
        private DateTime? _frameTimelineAnchor;
        private double? _lastFrameSourceElapsed;
        private string _frameTimelineSource = "";
        private string _frameSequenceIdentity = "";
        private long? _lastFrameSourceSequence;
        private double? _lastFrameSequenceElapsed;
        private Process _metricsProcess;
        private double? _latestFps;
        private double? _latestAverageFps;
        private double? _latestFrameTimeMs;
        private double? _latestFrameTimeMeanMs;
        private double? _latestFrameTimeP95Ms;
        private double? _latestFrameTimeMaxMs;
        private DateTime? _latestFpsAt;
        private string _latestFpsSource;
        private string _latestFpsScope;
        private string _latestFrameSource;
        private bool _latestOrderedFrames;
        private bool _latestApproximateFrameMetrics;
        private bool _latestSourceDegraded;
        private bool _latestHasFrameTargetVerification;
        private bool _latestFrameTargetVerified;
        private int _latestSurfaceOwnerPid;
        private int _latestSurfaceOwnerUid;
        private double? _latestMemory;
        private DateTime? _latestMemoryAt;
        private string _latestMemoryMetric;
        private string _latestMemorySource;
        private double? _latestMemoryRss;
        private double? _latestCpu;
        private DateTime? _latestCpuAt;
        private string _latestCpuSource;
        private double? _latestCpuNormalized;
        private DateTime? _latestCpuNormalizedAt;
        private string _latestCpuNormalizedSource;
        private List<double> _latestCpuCorePercents;
        private DateTime? _latestCpuCoreAt;
        private string _latestCpuCoreSource;
        private string _latestCpuCoreScope;
        private int? _latestThermalState;
        private DateTime? _latestThermalStateAt;
        private string _latestThermalStateName;
        private string _latestThermalStateSource;
        private string _latestThermalStateScope;
        private double _latestJank;
        private double _latestBigJank;
        private double _latestStutterPercent;
        private double _latestJankTimeMs;
        private double _latestFrameObservationMs;
        private bool _latestHasFrameObservation;
        private int _latestFrameCount;
        private double? _latestRefreshRateHz;
        private long _latestFpsSequence;
        private long _lastFpsSampledSequence;
        private long _lastJankSampledSequence;
        private long _latestMemorySequence;
        private long _lastMemorySampledSequence;
        private long _latestCpuSequence;
        private long _lastCpuSampledSequence;
        private long _latestCpuNormalizedSequence;
        private long _lastCpuNormalizedSampledSequence;
        private long _latestCpuCoreSequence;
        private long _lastCpuCoreSampledSequence;
        private bool _nativeJankSeen;
        private bool _nativeStutterSeen;
        private bool _processDataSeen;
        private bool _targetConfirmed;
        private int _activeTargetPid;
        private bool _waitingNoticeShown;
        private bool _android;
        private int _captureGeneration;
        private DateTime _startedAt;
        private CancellationTokenSource _cts;

        public event Action<PerfSample> SampleReady;
        public event Action<bool> TargetConfirmationChanged;
        public event Action<string> Message;
        public event Action<string> Failed;
        public string LastDiagnosticsLogPath { get; private set; }

        public void Start(CaptureConfig config)
        {
            Stop("replaced_by_new_capture");
            int targetPid = config == null || !config.TargetPid.HasValue ? 0 : config.TargetPid.Value;
            if (targetPid <= 0)
            {
                Raise(Failed, "请先在“选择设备及应用”里选择要测试的进程。CPU/内存按选中的 pid 采集，FPS 为屏幕级数据。");
                return;
            }
            int generation;
            lock (_lock)
            {
                _captureGeneration++;
                generation = _captureGeneration;
                _latestFps = null;
                _latestAverageFps = null;
                _latestFrameTimeMs = null;
                _latestFrameTimeMeanMs = null;
                _latestFrameTimeP95Ms = null;
                _latestFrameTimeMaxMs = null;
                _latestFpsAt = null;
                _latestFpsSource = "";
                _latestFpsScope = "";
                _latestFrameSource = "";
                _latestOrderedFrames = false;
                _latestApproximateFrameMetrics = false;
                _latestSourceDegraded = false;
                _latestHasFrameTargetVerification = false;
                _latestFrameTargetVerified = false;
                _latestSurfaceOwnerPid = 0;
                _latestSurfaceOwnerUid = 0;
                _latestMemory = null;
                _latestMemoryAt = null;
                _latestMemoryMetric = "";
                _latestMemorySource = "";
                _latestMemoryRss = null;
                _latestCpu = null;
                _latestCpuAt = null;
                _latestCpuSource = "";
                _latestCpuNormalized = null;
                _latestCpuNormalizedAt = null;
                _latestCpuNormalizedSource = "";
                _latestCpuCorePercents = null;
                _latestCpuCoreAt = null;
                _latestCpuCoreSource = "";
                _latestCpuCoreScope = "";
                _latestThermalState = null;
                _latestThermalStateAt = null;
                _latestThermalStateName = "";
                _latestThermalStateSource = "";
                _latestThermalStateScope = "";
                _latestJank = 0;
                _latestBigJank = 0;
                _latestStutterPercent = 0;
                _latestJankTimeMs = 0;
                _latestFrameObservationMs = 0;
                _latestHasFrameObservation = false;
                _latestFrameCount = 0;
                _latestRefreshRateHz = null;
                _pendingFrameSamples.Clear();
                _pendingProcessSamples.Clear();
                _stderrTail.Clear();
                DrainSampleSignal();
                _frameTimelineAnchor = null;
                _lastFrameSourceElapsed = null;
                _frameTimelineSource = "";
                _frameSequenceIdentity = "";
                _lastFrameSourceSequence = null;
                _lastFrameSequenceElapsed = null;
                _latestFpsSequence = 0;
                _lastFpsSampledSequence = 0;
                _lastJankSampledSequence = 0;
                _latestMemorySequence = 0;
                _lastMemorySampledSequence = 0;
                _latestCpuSequence = 0;
                _lastCpuSampledSequence = 0;
                _latestCpuNormalizedSequence = 0;
                _lastCpuNormalizedSampledSequence = 0;
                _latestCpuCoreSequence = 0;
                _lastCpuCoreSampledSequence = 0;
                _nativeJankSeen = false;
                _nativeStutterSeen = false;
                _processDataSeen = false;
                _targetConfirmed = false;
                _activeTargetPid = targetPid;
                _waitingNoticeShown = false;
                _startedAt = DateTime.Now;
            }
            CancellationTokenSource captureCts = new CancellationTokenSource();
            _cts = captureCts;
            bool isAndroid = DeviceLookupService.IsAndroid(config.Platform);
            _android = isAndroid;
            CaptureDiagnosticsLog diagnostics = CaptureDiagnosticsLog.TryCreate();
            lock (_lock) _diagnosticsLog = diagnostics;
            LastDiagnosticsLogPath = diagnostics == null ? "" : diagnostics.Path;
            string runner;
            try
            {
                runner = RunnerPath(isAndroid);
            }
            catch (Exception ex)
            {
                diagnostics?.WriteEvent("capture_start_failed", ex.Message, "runner_path");
                Stop("start_failed");
                captureCts.Dispose();
                throw;
            }
            string runnerPath = runner;
            if (diagnostics != null)
            {
                diagnostics.WriteStart(config, isAndroid ? "android metrics runner" : "pyidevice metrics runner", runnerPath);
            }
            string androidFpsTarget = string.IsNullOrWhiteSpace(config.TargetName) ? config.BundleId : config.TargetName;
            List<string> args = isAndroid
                ? new List<string> { runner, "--adb", RuntimeTools.AdbExecutable, "--serial", config.Udid, "--pid", targetPid.ToString(CultureInfo.InvariantCulture), "--package", androidFpsTarget, "--target-name", config.TargetName ?? "", "--target-start-time-ticks", config.TargetAndroidStartTimeTicks.ToString(CultureInfo.InvariantCulture), "--interval", "1" }
                : new List<string>
                {
                    runner,
                    "--udid", config.Udid,
                    "--pid", targetPid.ToString(CultureInfo.InvariantCulture),
                    "--target-name", config.TargetName ?? "",
                    "--target-bundle-id", config.BundleId ?? "",
                    "--target-start-abs-time", config.TargetStartAbsTime.ToString(CultureInfo.InvariantCulture),
                    "--target-coalition-id", config.TargetCoalitionId.ToString(CultureInfo.InvariantCulture),
                    "--target-owner-pid", config.TargetOwnerPid.ToString(CultureInfo.InvariantCulture),
                    "--target-owner-name", config.TargetOwnerName ?? "",
                    "--interval", "1000",
                    "--ios-version", config.ProductVersion ?? ""
                };
            if (!config.CollectFps) args.Add("--no-fps");
            if (!config.CollectMemory) args.Add("--no-memory");
            if (!config.CollectCpu) args.Add("--no-cpu");
            if (!config.CollectTemperature) args.Add("--no-temperature");
            if (!config.CollectThermalState) args.Add("--no-thermal-state");
            Process captureProcess;
            try
            {
                captureProcess = RuntimeTools.StartPythonStreaming(args);
            }
            catch (Exception ex)
            {
                diagnostics?.WriteEvent("capture_start_failed", ex.Message, "runner_launch");
                Stop("start_failed");
                captureCts.Dispose();
                throw;
            }
            _metricsProcess = captureProcess;
            string label = isAndroid ? "adb metrics runner" : "pyidevice metrics runner";
            WriteDiagnosticsEvent("runner_started", "采集子进程已启动，pid=" + captureProcess.Id.ToString(CultureInfo.InvariantCulture), "runner_started");
            CancellationToken captureToken = captureCts.Token;
            Task stderrTask = Task.Run(() => RunCaptureTaskAsync("stderr", () => ReadStderrLoop(captureProcess, label, generation, captureToken), diagnostics, generation, captureToken));
            Task stdoutTask = Task.Run(() => RunCaptureTaskAsync("stdout", () => ReadStdoutLoop(captureProcess, label, generation, stderrTask, captureToken, isAndroid), diagnostics, generation, captureToken));
            Task sampleTask = Task.Run(() => RunCaptureTaskAsync("samples", () => SampleLoop(config, generation, captureToken, isAndroid), diagnostics, generation, captureToken));
            Task.WhenAll(stderrTask, stdoutTask, sampleTask).ContinueWith(delegate
            {
                try { captureProcess.Dispose(); } catch { }
                try { captureCts.Dispose(); } catch { }
                diagnostics?.Dispose();
            }, TaskScheduler.Default);
            Raise(Message, CollectionStartMessage(config));
        }

        private async Task RunCaptureTaskAsync(string name, Func<Task> run, CaptureDiagnosticsLog diagnostics, int generation, CancellationToken token)
        {
            Stopwatch duration = Stopwatch.StartNew();
            diagnostics?.WriteTask(name, "started", 0);
            string state = "completed";
            try { await run().ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { state = "cancelled"; }
            catch (Exception ex)
            {
                state = "faulted";
                diagnostics?.WriteEvent("capture_task_failed", ex.Message, name + "_task_error");
                if (!token.IsCancellationRequested && IsGenerationCurrent(generation))
                    FailAndStop("采集任务异常（" + name + "）：" + ex.Message, name + "_task_error");
            }
            finally
            {
                diagnostics?.WriteTask(name, state == "completed" && token.IsCancellationRequested ? "cancelled" : state, duration.ElapsedMilliseconds);
            }
        }

        public void RecordEvent(string eventName, string message, string code)
        {
            WriteDiagnosticsEvent(eventName, message, code);
        }

        public void Stop()
        {
            Stop("stop_requested");
        }

        public void Stop(string reason)
        {
            CaptureDiagnosticsLog diagnostics;
            CancellationTokenSource captureCts;
            Process captureProcess;
            lock (_lock)
            {
                _captureGeneration++;
                _pendingFrameSamples.Clear();
                _pendingProcessSamples.Clear();
                DrainSampleSignal();
                diagnostics = _diagnosticsLog;
                _diagnosticsLog = null;
                captureCts = _cts;
                captureProcess = _metricsProcess;
                _cts = null;
                _metricsProcess = null;
            }
            if (diagnostics != null)
            {
                diagnostics.WriteStop(reason, "");
                if (captureProcess == null) diagnostics.Dispose();
            }
            if (captureCts != null)
            {
                try { captureCts.Cancel(); } catch (ObjectDisposedException) { }
            }
            if (captureProcess != null)
            {
                ProcessRunner.TryKill(captureProcess);
            }
        }

        private async Task ReadStdoutLoop(Process process, string label, int generation, Task stderrTask, CancellationToken token, bool isAndroid)
        {
            try
            {
                while (!token.IsCancellationRequested && !process.StandardOutput.EndOfStream)
                {
                    string line = await process.StandardOutput.ReadLineAsync();
                    if (line == null) break;
                    lock (_lock) { if (generation == _captureGeneration) _diagnosticsLog?.ObserveOutput(); }
                    ParseLine(line, generation, isAndroid);
                }
                if (!token.IsCancellationRequested)
                {
                    await WaitForExitAsync(process);
                    await stderrTask;
                    if (IsGenerationCurrent(generation))
                    {
                        string exitMessage = process.ExitCode == 0
                            ? "意外结束"
                            : "已退出，退出码：" + process.ExitCode;
                        string stderr = StderrTail(generation);
                        WriteDiagnosticsRunnerExit(label, process.ExitCode, stderr);
                        string logPath = LastDiagnosticsLogPath;
                        string detail = string.IsNullOrWhiteSpace(stderr) ? "" : "；错误：" + Clip(stderr);
                        string logHint = string.IsNullOrWhiteSpace(logPath) ? "" : "；详细日志：" + logPath;
                        FailAndStop(label + " " + exitMessage + detail + logHint + "。请重新选择当前在线设备和正在运行的 pid。", "runner_exit");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && IsGenerationCurrent(generation)) FailAndStop("读取采集输出失败：" + ex.Message, "stdout_read_error");
            }
        }

        private async Task ReadStderrLoop(Process process, string label, int generation, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && !process.StandardError.EndOfStream)
                {
                    string line = await process.StandardError.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (IsGenerationCurrent(generation))
                    {
                        RecordStderr(generation, line);
                        WriteDiagnosticsStderr(label, line);
                        Raise(Message, label + " 日志：" + Clip(line));
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && IsGenerationCurrent(generation)) FailAndStop("读取采集错误输出失败：" + ex.Message, "stderr_read_error");
            }
        }

        private async Task SampleLoop(CaptureConfig config, int generation, CancellationToken token, bool isAndroid)
        {
            while (!token.IsCancellationRequested && IsGenerationCurrent(generation))
            {
                DateTime now = DateTime.Now;
                double memory;
                double memoryRss;
                double cpu;
                bool hasMemory;
                bool hasMemoryRss;
                bool memoryUpdated;
                bool hasCpu;
                bool cpuUpdated;
                string memoryMetric;
                string memorySource;
                string cpuSource;
                bool hasCpuNormalized;
                bool cpuNormalizedUpdated;
                double cpuNormalized;
                string cpuNormalizedSource;
                bool hasCpuCoreUsage;
                bool cpuCoreUpdated;
                List<double> cpuCorePercents;
                int cpuCoreCount;
                string cpuCoreSource;
                string cpuCoreScope;
                List<PerfSample> frameSamples = new List<PerfSample>();
                List<PerfSample> processSamples = new List<PerfSample>();
                lock (_lock)
                {
                    bool hasRecentMemoryData = _targetConfirmed && _latestMemoryAt.HasValue && (now - _latestMemoryAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
                    bool hasRecentCpuData = _targetConfirmed && _latestCpuAt.HasValue && (now - _latestCpuAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
                    bool expectsProcessData = config.CollectMemory || config.CollectCpu;
                    bool hasRecentProcessData = (!config.CollectMemory || hasRecentMemoryData)
                        && (!config.CollectCpu || hasRecentCpuData);
                    if (hasRecentProcessData) _waitingNoticeShown = false;
                    if (expectsProcessData && (!_processDataSeen || !hasRecentProcessData) && (now - _startedAt).TotalSeconds >= 5)
                    {
                        if (!_waitingNoticeShown)
                        {
                            _waitingNoticeShown = true;
                            Raise(Message, "暂未收到或已中断所选 pid 的 CPU/内存数据，仍在等待。请确认该进程仍在运行。");
                        }
                    }
                    while (_pendingFrameSamples.Count > 0)
                    {
                        frameSamples.Add(_pendingFrameSamples.Dequeue());
                    }
                    while (_pendingProcessSamples.Count > 0)
                    {
                        processSamples.Add(_pendingProcessSamples.Dequeue());
                    }
                    ApplyFrameTimeline(frameSamples, now);
                    if (frameSamples.Count > 0)
                    {
                        _lastFpsSampledSequence = _latestFpsSequence;
                        _lastJankSampledSequence = _latestFpsSequence;
                    }
                    else
                    {
                        double fpsFreshSeconds = isAndroid ? AndroidFpsFreshSeconds : IosFpsFreshSeconds;
                        bool hasFreshFps = _latestFps.HasValue && _latestFpsAt.HasValue && (now - _latestFpsAt.Value).TotalSeconds <= fpsFreshSeconds;
                        frameSamples.Add(new PerfSample
                        {
                            Timestamp = now,
                            HasFps = hasFreshFps,
                            Fps = hasFreshFps ? _latestFps.Value : 0,
                            FpsUpdated = false,
                            HasAverageFps = hasFreshFps && _latestAverageFps.HasValue,
                            AverageFps = hasFreshFps && _latestAverageFps.HasValue ? _latestAverageFps.Value : 0,
                            HasFrameTime = hasFreshFps && _latestFrameTimeMs.HasValue,
                            FrameTimeMs = hasFreshFps && _latestFrameTimeMs.HasValue ? _latestFrameTimeMs.Value : 0,
                            HasFrameTimeMean = hasFreshFps && _latestFrameTimeMeanMs.HasValue,
                            FrameTimeMeanMs = hasFreshFps && _latestFrameTimeMeanMs.HasValue ? _latestFrameTimeMeanMs.Value : 0,
                            HasFrameTimeP95 = hasFreshFps && _latestFrameTimeP95Ms.HasValue,
                            FrameTimeP95Ms = hasFreshFps && _latestFrameTimeP95Ms.HasValue ? _latestFrameTimeP95Ms.Value : 0,
                            HasFrameTimeMax = hasFreshFps && _latestFrameTimeMaxMs.HasValue,
                            FrameTimeMaxMs = hasFreshFps && _latestFrameTimeMaxMs.HasValue ? _latestFrameTimeMaxMs.Value : 0,
                            HasJank = hasFreshFps && _nativeJankSeen,
                            Jank = hasFreshFps && _nativeJankSeen ? _latestJank : 0,
                            BigJank = hasFreshFps && _nativeJankSeen ? _latestBigJank : 0,
                            HasStutter = hasFreshFps && _nativeStutterSeen,
                            StutterPercent = hasFreshFps && _nativeStutterSeen ? _latestStutterPercent : 0,
                            JankTimeMs = hasFreshFps && _nativeStutterSeen ? _latestJankTimeMs : 0,
                            HasFrameObservation = false,
                            FrameCount = 0,
                            FrameObservationMs = 0,
                            HasRefreshRate = hasFreshFps && _latestRefreshRateHz.HasValue,
                            RefreshRateHz = hasFreshFps && _latestRefreshRateHz.HasValue ? _latestRefreshRateHz.Value : 0,
                            OrderedFrames = hasFreshFps && _latestOrderedFrames,
                            ApproximateFrameMetrics = hasFreshFps && _latestApproximateFrameMetrics,
                            SourceDegraded = hasFreshFps && _latestSourceDegraded,
                            HasFrameTargetVerification = hasFreshFps && _latestHasFrameTargetVerification,
                            FrameTargetVerified = hasFreshFps && _latestFrameTargetVerified,
                            SurfaceOwnerPid = hasFreshFps ? _latestSurfaceOwnerPid : 0,
                            SurfaceOwnerUid = hasFreshFps ? _latestSurfaceOwnerUid : 0,
                            FpsScope = hasFreshFps ? _latestFpsScope : "",
                            FrameSource = hasFreshFps ? _latestFrameSource : "",
                            Source = hasFreshFps ? _latestFpsSource : ""
                        });
                    }
                    hasMemory = config.CollectMemory && _latestMemory.HasValue && _latestMemoryAt.HasValue && (now - _latestMemoryAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
                    hasMemoryRss = hasMemory && _latestMemoryRss.HasValue;
                    hasCpu = config.CollectCpu && _latestCpu.HasValue && _latestCpuAt.HasValue && (now - _latestCpuAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
                    hasCpuNormalized = config.CollectCpu && _latestCpuNormalized.HasValue && _latestCpuNormalizedAt.HasValue && (now - _latestCpuNormalizedAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
                    hasCpuCoreUsage = config.CollectCpu && _latestCpuCorePercents != null && _latestCpuCorePercents.Count > 0 && _latestCpuCoreAt.HasValue && (now - _latestCpuCoreAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
                    memory = hasMemory ? _latestMemory.Value : 0;
                    memoryRss = hasMemoryRss ? _latestMemoryRss.Value : 0;
                    cpu = hasCpu ? _latestCpu.Value : 0;
                    cpuNormalized = hasCpuNormalized ? _latestCpuNormalized.Value : 0;
                    cpuCorePercents = hasCpuCoreUsage ? new List<double>(_latestCpuCorePercents) : new List<double>();
                    cpuCoreCount = hasCpuCoreUsage ? cpuCorePercents.Count : 0;
                    bool queuedMemoryUpdate = processSamples.Any(delegate(PerfSample sample) { return sample.MemoryUpdated; });
                    bool queuedCpuUpdate = processSamples.Any(delegate(PerfSample sample) { return sample.CpuUpdated; });
                    bool queuedCpuNormalizedUpdate = processSamples.Any(delegate(PerfSample sample) { return sample.CpuNormalizedUpdated; });
                    bool queuedCpuCoreUpdate = processSamples.Any(delegate(PerfSample sample) { return sample.CpuCoreUpdated; });
                    if (queuedMemoryUpdate) _lastMemorySampledSequence = _latestMemorySequence;
                    if (queuedCpuUpdate) _lastCpuSampledSequence = _latestCpuSequence;
                    if (queuedCpuNormalizedUpdate) _lastCpuNormalizedSampledSequence = _latestCpuNormalizedSequence;
                    if (queuedCpuCoreUpdate) _lastCpuCoreSampledSequence = _latestCpuCoreSequence;
                    memoryUpdated = hasMemory && _latestMemorySequence > _lastMemorySampledSequence;
                    cpuUpdated = hasCpu && _latestCpuSequence > _lastCpuSampledSequence;
                    cpuNormalizedUpdated = hasCpuNormalized && _latestCpuNormalizedSequence > _lastCpuNormalizedSampledSequence;
                    cpuCoreUpdated = hasCpuCoreUsage && _latestCpuCoreSequence > _lastCpuCoreSampledSequence;
                    if (memoryUpdated) _lastMemorySampledSequence = _latestMemorySequence;
                    if (cpuUpdated) _lastCpuSampledSequence = _latestCpuSequence;
                    if (cpuNormalizedUpdated) _lastCpuNormalizedSampledSequence = _latestCpuNormalizedSequence;
                    if (cpuCoreUpdated) _lastCpuCoreSampledSequence = _latestCpuCoreSequence;
                    memoryMetric = hasMemory ? _latestMemoryMetric : "";
                    memorySource = hasMemory ? _latestMemorySource : "";
                    cpuSource = hasCpu ? _latestCpuSource : "";
                    cpuNormalizedSource = hasCpuNormalized ? _latestCpuNormalizedSource : "";
                    cpuCoreSource = hasCpuCoreUsage ? _latestCpuCoreSource : "";
                    cpuCoreScope = hasCpuCoreUsage ? _latestCpuCoreScope : "";
                }
                if (!IsGenerationCurrent(generation)) break;
                frameSamples.AddRange(processSamples);
                frameSamples.Sort(delegate(PerfSample left, PerfSample right)
                {
                    return left.Timestamp.CompareTo(right.Timestamp);
                });
                for (int index = 0; index < frameSamples.Count; index++)
                {
                    PerfSample sample = frameSamples[index];
                    bool finalSample = index == frameSamples.Count - 1;
                    bool processMetricEvent = sample.MemoryUpdated || sample.CpuUpdated || sample.CpuNormalizedUpdated || sample.CpuCoreUpdated;
                    if (sample.Timestamp == default(DateTime)) sample.Timestamp = now;
                    sample.ElapsedSec = Math.Max(0, (sample.Timestamp - _startedAt).TotalSeconds);
                    sample.TargetPid = _activeTargetPid;
                    sample.FpsScope = string.IsNullOrWhiteSpace(sample.FpsScope) ? FpsScope(config) : sample.FpsScope;
                    sample.Source = string.IsNullOrWhiteSpace(sample.Source) ? DefaultSampleSource(config) : sample.Source;
                    if (!processMetricEvent)
                    {
                        sample.HasMemory = hasMemory;
                        sample.MemoryMb = memory;
                        sample.MemoryUpdated = finalSample && memoryUpdated;
                        sample.MemoryMetric = memoryMetric;
                        sample.MemorySource = memorySource;
                        sample.HasMemoryRss = hasMemoryRss;
                        sample.MemoryRssMb = memoryRss;
                        sample.HasCpu = hasCpu;
                        sample.CpuPercent = cpu;
                        sample.CpuUpdated = finalSample && cpuUpdated;
                        sample.CpuSource = cpuSource;
                        sample.HasCpuNormalized = hasCpuNormalized;
                        sample.CpuNormalizedPercent = cpuNormalized;
                        sample.CpuNormalizedUpdated = finalSample && cpuNormalizedUpdated;
                        sample.CpuNormalizedSource = cpuNormalizedSource;
                        sample.HasCpuCoreUsage = hasCpuCoreUsage;
                        sample.CpuCorePercents = new List<double>(cpuCorePercents);
                        sample.CpuCoreCount = cpuCoreCount;
                        sample.CpuCoreUpdated = finalSample && cpuCoreUpdated;
                        sample.CpuCoreSource = cpuCoreSource;
                        sample.CpuCoreScope = cpuCoreScope;
                    }
                    sample.HasFreshnessMetadata = true;
                    sample.Note = !hasMemory && !hasCpu
                        ? "Waiting for pid CPU/memory; FPS scope " + FpsScope(config) + "; pid " + _activeTargetPid
                        : "Real USB data; pid CPU/memory " + _activeTargetPid + "; FPS scope " + sample.FpsScope + "; target " + config.TargetName;
                    if (sample.ApproximateFrameMetrics) sample.Note += "; frame metrics approximate";
                    if (sample.SourceDegraded) sample.Note += "; frame source degraded";
                    if (sample.DuplicateFrameTimestamps > 0
                        || sample.OutOfOrderFrameTimestamps > 0
                        || sample.InvalidFrameIntervals > 0
                        || sample.FrameRingBufferOverrun)
                    {
                        sample.Note += string.Format(
                            CultureInfo.InvariantCulture,
                            "; frame diagnostics duplicate={0}, out-of-order={1}, invalid={2}, ring-overrun={3}",
                            sample.DuplicateFrameTimestamps,
                            sample.OutOfOrderFrameTimestamps,
                            sample.InvalidFrameIntervals,
                            sample.FrameRingBufferOverrun ? "true" : "false");
                    }
                    if (sample.FrameSourceSequenceDiscontinuity)
                    {
                        sample.Note += "; frame source sequence discontinuity; missing windows "
                            + sample.MissingFrameSourceWindows.ToString(CultureInfo.InvariantCulture);
                    }
                    if (sample.HasFrameTargetVerification)
                    {
                        sample.Note += sample.FrameTargetVerified
                            ? "; surface owner verified pid " + sample.SurfaceOwnerPid
                            : "; surface owner unverified";
                    }
                    if (!IsGenerationCurrent(generation)) break;
                    lock (_lock) { if (generation == _captureGeneration) _diagnosticsLog?.ObserveSample(sample); }
                    Raise(SampleReady, sample);
                }
                try
                {
                    bool signaled = await _sampleSignal.WaitAsync(1000, token);
                    if (signaled) DrainSampleSignal();
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void SignalSampleLoop()
        {
            try
            {
                _sampleSignal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void DrainSampleSignal()
        {
            try
            {
                while (_sampleSignal.Wait(0))
                {
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ApplyFrameTimeline(List<PerfSample> frameSamples, DateTime receivedAt)
        {
            List<PerfSample> timed = frameSamples
                .Where(delegate(PerfSample sample) { return sample.HasFrameSourceElapsed; })
                .ToList();
            if (timed.Count == 0) return;

            PerfSample latest = timed[timed.Count - 1];
            bool reset = !_frameTimelineAnchor.HasValue ||
                !string.Equals(_frameTimelineSource, latest.Source, StringComparison.Ordinal) ||
                (_lastFrameSourceElapsed.HasValue && latest.FrameSourceElapsedSec < _lastFrameSourceElapsed.Value);
            if (reset)
            {
                _frameTimelineAnchor = receivedAt.AddSeconds(-latest.FrameSourceElapsedSec);
                _frameTimelineSource = latest.Source;
            }

            DateTime anchor = _frameTimelineAnchor.Value;
            foreach (PerfSample sample in timed)
            {
                if (!string.Equals(sample.Source, _frameTimelineSource, StringComparison.Ordinal)) continue;
                DateTime timestamp = anchor.AddSeconds(sample.FrameSourceElapsedSec);
                sample.Timestamp = timestamp < _startedAt ? _startedAt : timestamp;
            }
            _lastFrameSourceElapsed = latest.FrameSourceElapsedSec;
        }

        private static string RunnerPath(bool android)
        {
            string fileName = android ? "android_perf_runner.py" : "pid_perf_runner.py";
            return RuntimeTools.ResolveToolPath(fileName);
        }

        private static string FpsScope(CaptureConfig config)
        {
            return DeviceLookupService.IsAndroid(config.Platform) ? "android app/surface" : "screen global";
        }

        private static string DefaultSampleSource(CaptureConfig config)
        {
            return DeviceLookupService.IsAndroid(config.Platform) ? "adb-pid+app-fps" : "pyidevice-pid+screen-fps";
        }

        private static string CollectionStartMessage(CaptureConfig config)
        {
            List<string> processMetrics = new List<string>();
            if (config.CollectCpu) processMetrics.Add("CPU");
            if (config.CollectMemory) processMetrics.Add("内存");

            string processStatus = processMetrics.Count > 0
                ? "等待 pid " + config.TargetPid.Value + " 的 " + string.Join("/", processMetrics) + " 数据"
                : "pid " + config.TargetPid.Value + " 身份校验已启动";
            string frameStatus = !config.CollectFps
                ? "FPS/FrameTime 采集未启用"
                : (DeviceLookupService.IsAndroid(config.Platform)
                    ? "FPS 优先使用 SurfaceFlinger，必要时回退到 gfxinfo/显示层"
                    : "FPS 使用屏幕级采集");
            return "采集已启动，" + processStatus + "；" + frameStatus + "。";
        }

        private void ParseLine(string line, int generation, bool isAndroid)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            int firstSpace = line.IndexOf(' ');
            if (firstSpace <= 0) return;
            string kind = line.Substring(0, firstSpace);
            string json = line.Substring(firstSpace + 1);
            if (kind != "fps" && kind != "memory" && kind != "cpu" && kind != "temperature" && kind != "thermal_state" && kind != "status" && kind != "fatal" && kind != "target" && kind != "target_rebound") return;
            Dictionary<string, object> obj = SimpleJson.Parse(json) as Dictionary<string, object>;
            if (obj == null) return;
            if (kind == "fatal")
            {
                if (!IsGenerationCurrent(generation)) return;
                string fatalMessage = Convert.ToString(Get(obj, "message") ?? "所选 iOS pid 已失效，请重新选择当前运行的进程。");
                string fatalCode = Convert.ToString(Get(obj, "code") ?? "fatal");
                WriteDiagnosticsEvent("runner_fatal", fatalMessage, fatalCode);
                FailAndStop(fatalMessage, fatalCode);
                return;
            }
            if (kind == "status")
            {
                if (!IsGenerationCurrent(generation)) return;
                object message = Get(obj, "message");
                if (message != null)
                {
                    string statusMessage = Convert.ToString(message);
                    if (statusMessage.IndexOf("Thermal State", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        WriteDiagnosticsEvent("thermal_state_status", statusMessage, "thermal_state_unavailable");
                    }
                    Raise(Message, statusMessage);
                }
                return;
            }
            if (kind == "target")
            {
                if (!IsGenerationCurrent(generation)) return;
                int targetPid;
                lock (_lock)
                {
                    if (TryNonNegativeInt(Get(obj, "pid"), out targetPid) && targetPid > 0) _activeTargetPid = targetPid;
                    _targetConfirmed = AsBool(Get(obj, "confirmed"));
                }
                Raise(TargetConfirmationChanged, _targetConfirmed);
                return;
            }
            if (kind == "target_rebound")
            {
                if (!IsGenerationCurrent(generation)) return;
                int parsedOldPid;
                int parsedNewPid;
                lock (_lock)
                {
                    _targetConfirmed = true;
                    int oldPidValue;
                    int newPidValue;
                    parsedOldPid = TryNonNegativeInt(Get(obj, "old_pid"), out oldPidValue) ? oldPidValue : 0;
                    parsedNewPid = TryNonNegativeInt(Get(obj, "new_pid"), out newPidValue) ? newPidValue : 0;
                    if (parsedNewPid > 0) _activeTargetPid = parsedNewPid;
                }
                string bundleId = Convert.ToString(Get(obj, "bundle_id") ?? "");
                string reason = Convert.ToString(Get(obj, "reason") ?? "");
                WritePidReboundDiagnostics(parsedOldPid, parsedNewPid, bundleId, reason);
                Raise(Message, "已自动切换采集进程：PID " + parsedOldPid + " -> PID " + parsedNewPid);
                return;
            }
            lock (_lock)
            {
                if (generation != _captureGeneration) return;
                if (kind == "fps")
                {
                    object fpsObject = Get(obj, "fps") ?? Get(obj, "value");
                    double fpsValue;
                    string source = Convert.ToString(Get(obj, "source") ?? "");
                    if (!TryAsDouble(fpsObject, out fpsValue) || !IsValidFps(fpsValue))
                    {
                        return;
                    }
                    _latestFps = fpsValue;
                    double averageFps;
                    _latestAverageFps = obj.ContainsKey("average_fps") && TryAsDouble(obj["average_fps"], out averageFps) && IsValidFps(averageFps) ? averageFps : fpsValue;
                    _latestFpsSource = source;
                    _latestFpsScope = Convert.ToString(Get(obj, "scope") ?? "");
                    _latestFrameSource = Convert.ToString(Get(obj, "frame_source") ?? "");
                    double parsedSourceElapsed;
                    bool hasSourceElapsed = TryNonNegativeDouble(Get(obj, "source_elapsed_sec"), out parsedSourceElapsed);
                    long parsedSourceSequence;
                    bool hasSourceSequence = TryPositiveLong(Get(obj, "source_sequence"), out parsedSourceSequence);
                    bool frameSourceSequenceDiscontinuity = false;
                    long missingFrameSourceWindows = 0;
                    if (hasSourceSequence)
                    {
                        string sequenceIdentity = source + "\n" + _latestFrameSource;
                        bool sameIdentity = string.Equals(_frameSequenceIdentity, sequenceIdentity, StringComparison.Ordinal);
                        bool elapsedReset = sameIdentity
                            && hasSourceElapsed
                            && _lastFrameSequenceElapsed.HasValue
                            && parsedSourceElapsed < _lastFrameSequenceElapsed.Value;
                        if (sameIdentity && _lastFrameSourceSequence.HasValue)
                        {
                            long expectedSequence = _lastFrameSourceSequence.Value == long.MaxValue
                                ? long.MaxValue
                                : _lastFrameSourceSequence.Value + 1;
                            frameSourceSequenceDiscontinuity = elapsedReset || parsedSourceSequence != expectedSequence;
                            if (!elapsedReset && parsedSourceSequence > expectedSequence)
                            {
                                missingFrameSourceWindows = parsedSourceSequence - expectedSequence;
                            }
                        }
                        _frameSequenceIdentity = sequenceIdentity;
                        _lastFrameSourceSequence = parsedSourceSequence;
                        _lastFrameSequenceElapsed = hasSourceElapsed ? parsedSourceElapsed : (double?)null;
                    }
                    _latestOrderedFrames = AsBool(Get(obj, "ordered_frames"));
                    int parsedDuplicateFrameTimestamps;
                    int parsedOutOfOrderFrameTimestamps;
                    int parsedInvalidFrameIntervals;
                    int duplicateFrameTimestamps = TryNonNegativeInt(Get(obj, "duplicate_timestamps"), out parsedDuplicateFrameTimestamps)
                        ? parsedDuplicateFrameTimestamps
                        : 0;
                    int outOfOrderFrameTimestamps = TryNonNegativeInt(Get(obj, "out_of_order_timestamps"), out parsedOutOfOrderFrameTimestamps)
                        ? parsedOutOfOrderFrameTimestamps
                        : 0;
                    int invalidFrameIntervals = TryNonNegativeInt(Get(obj, "invalid_frame_intervals"), out parsedInvalidFrameIntervals)
                        ? parsedInvalidFrameIntervals
                        : 0;
                    bool frameRingBufferOverrun = AsBool(Get(obj, "ring_buffer_overrun"));
                    bool hasFrameIntegrityDiagnostic = duplicateFrameTimestamps > 0
                        || outOfOrderFrameTimestamps > 0
                        || invalidFrameIntervals > 0
                        || frameRingBufferOverrun;
                    bool derivedFrameMetricsUnavailable = outOfOrderFrameTimestamps > 0
                        || invalidFrameIntervals > 0
                        || frameRingBufferOverrun;
                    _latestSourceDegraded = AsBool(Get(obj, "source_degraded")) || hasFrameIntegrityDiagnostic;
                    _latestApproximateFrameMetrics = AsBool(Get(obj, "approximate")) || _latestSourceDegraded;
                    bool hasFrameTargetVerification = obj.ContainsKey("target_verified");
                    bool frameTargetVerified = hasFrameTargetVerification && AsBool(Get(obj, "target_verified"));
                    int parsedSurfaceOwnerPid;
                    int parsedSurfaceOwnerUid;
                    int surfaceOwnerPid = TryNonNegativeInt(Get(obj, "surface_owner_pid"), out parsedSurfaceOwnerPid)
                        ? parsedSurfaceOwnerPid
                        : 0;
                    int surfaceOwnerUid = TryNonNegativeInt(Get(obj, "surface_owner_uid"), out parsedSurfaceOwnerUid)
                        ? parsedSurfaceOwnerUid
                        : 0;
                    _latestHasFrameTargetVerification = hasFrameTargetVerification;
                    _latestFrameTargetVerified = frameTargetVerified;
                    _latestSurfaceOwnerPid = surfaceOwnerPid;
                    _latestSurfaceOwnerUid = surfaceOwnerUid;
                    _latestFpsSequence++;
                    if (obj.ContainsKey("frame_time_ms"))
                    {
                        double frameTimeMs;
                        _latestFrameTimeMs = TryAsDouble(obj["frame_time_ms"], out frameTimeMs) && IsValidFrameTimeMs(frameTimeMs) ? frameTimeMs : (double?)null;
                    }
                    else if (obj.ContainsKey("frameTimeMs"))
                    {
                        double frameTimeMs;
                        _latestFrameTimeMs = TryAsDouble(obj["frameTimeMs"], out frameTimeMs) && IsValidFrameTimeMs(frameTimeMs) ? frameTimeMs : (double?)null;
                    }
                    else
                    {
                        _latestFrameTimeMs = null;
                    }
                    _latestFrameTimeMeanMs = ParseValidFrameTime(Get(obj, "frame_time_mean_ms"));
                    _latestFrameTimeP95Ms = ParseValidFrameTime(Get(obj, "frame_time_p95_ms"));
                    _latestFrameTimeMaxMs = ParseValidFrameTime(Get(obj, "frame_time_max_ms"));
                    if (!_latestFrameTimeMs.HasValue && _latestFrameTimeP95Ms.HasValue) _latestFrameTimeMs = _latestFrameTimeP95Ms;
                    if (!_latestFrameTimeP95Ms.HasValue) _latestFrameTimeP95Ms = _latestFrameTimeMs;
                    if (derivedFrameMetricsUnavailable)
                    {
                        _latestFrameTimeMs = null;
                        _latestFrameTimeMeanMs = null;
                        _latestFrameTimeP95Ms = null;
                        _latestFrameTimeMaxMs = null;
                    }
                    _latestFpsAt = DateTime.Now;
                    int parsedFrameCount;
                    bool hasFrameCount = TryNonNegativeInt(Get(obj, "frame_count"), out parsedFrameCount);
                    _latestFrameCount = hasFrameCount ? parsedFrameCount : 0;
                    double parsedObservationMs;
                    double parsedWindowSec;
                    _latestFrameObservationMs = TryNonNegativeDouble(Get(obj, "frame_observation_ms"), out parsedObservationMs)
                        ? parsedObservationMs
                        : (TryNonNegativeDouble(Get(obj, "window_sec"), out parsedWindowSec) ? parsedWindowSec * 1000.0 : 0);
                    _latestHasFrameObservation = hasFrameCount && _latestFrameObservationMs > 0;
                    if (!_latestHasFrameObservation) _latestFrameObservationMs = 0;
                    bool noPresentFrames = AsBool(Get(obj, "no_present_frames"));
                    double parsedResumeGapMs;
                    double resumeGapMs = TryNonNegativeDouble(Get(obj, "resume_gap_ms"), out parsedResumeGapMs)
                        && parsedResumeGapMs > 0
                        ? parsedResumeGapMs
                        : 0;
                    double parsedRefreshRate;
                    _latestRefreshRateHz = TryAsDouble(Get(obj, "refresh_rate_hz"), out parsedRefreshRate) && IsValidRefreshRate(parsedRefreshRate)
                        ? parsedRefreshRate
                        : (double?)null;
                    double parsedJank = 0;
                    double parsedBigJank = 0;
                    bool hasJankValue = !derivedFrameMetricsUnavailable
                        && TryNonNegativeDouble(Get(obj, "jank"), out parsedJank);
                    bool hasBigJankValue = !derivedFrameMetricsUnavailable
                        && TryNonNegativeDouble(Get(obj, "big_jank") ?? Get(obj, "bigJank"), out parsedBigJank);
                    if (hasJankValue || hasBigJankValue)
                    {
                        _nativeJankSeen = true;
                        _latestBigJank = hasBigJankValue ? parsedBigJank : 0;
                        _latestJank = Math.Max(hasJankValue ? parsedJank : 0, _latestBigJank);
                        double parsedJankTime;
                        bool hasJankTime = TryNonNegativeDouble(Get(obj, "jank_time_ms"), out parsedJankTime);
                        _latestJankTimeMs = hasJankTime ? parsedJankTime : 0;
                        double parsedStutter = 0;
                        _nativeStutterSeen = hasJankTime
                            && _latestHasFrameObservation
                            && parsedJankTime <= _latestFrameObservationMs
                            && (_latestJank <= 0 || parsedJankTime > 0)
                            && TryNonNegativeDouble(Get(obj, "stutter_percent"), out parsedStutter)
                            && parsedStutter <= 100.0;
                        _latestStutterPercent = _nativeStutterSeen ? parsedStutter : 0;
                    }
                    else
                    {
                        _nativeJankSeen = false;
                        _nativeStutterSeen = false;
                        _latestJank = 0;
                        _latestBigJank = 0;
                        _latestJankTimeMs = 0;
                        _latestStutterPercent = 0;
                    }
                    _pendingFrameSamples.Enqueue(new PerfSample
                    {
                        Timestamp = _latestFpsAt.Value,
                        HasFps = true,
                        Fps = fpsValue,
                        FpsUpdated = true,
                        HasAverageFps = _latestAverageFps.HasValue,
                        AverageFps = _latestAverageFps ?? fpsValue,
                        HasFrameTime = _latestFrameTimeMs.HasValue,
                        FrameTimeMs = _latestFrameTimeMs ?? 0,
                        HasFrameTimeMean = _latestFrameTimeMeanMs.HasValue,
                        FrameTimeMeanMs = _latestFrameTimeMeanMs ?? 0,
                        HasFrameTimeP95 = _latestFrameTimeP95Ms.HasValue,
                        FrameTimeP95Ms = _latestFrameTimeP95Ms ?? 0,
                        HasFrameTimeMax = _latestFrameTimeMaxMs.HasValue,
                        FrameTimeMaxMs = _latestFrameTimeMaxMs ?? 0,
                        HasJank = _nativeJankSeen,
                        Jank = _latestJank,
                        BigJank = _latestBigJank,
                        HasStutter = _nativeStutterSeen,
                        StutterPercent = _latestStutterPercent,
                        JankTimeMs = _latestJankTimeMs,
                        HasFrameObservation = _latestHasFrameObservation,
                        FrameCount = _latestFrameCount,
                        FrameObservationMs = _latestFrameObservationMs,
                        NoPresentFrames = noPresentFrames,
                        ResumeGapMs = resumeGapMs,
                        HasRefreshRate = _latestRefreshRateHz.HasValue,
                        RefreshRateHz = _latestRefreshRateHz ?? 0,
                        OrderedFrames = _latestOrderedFrames,
                        ApproximateFrameMetrics = _latestApproximateFrameMetrics,
                        SourceDegraded = _latestSourceDegraded,
                        DuplicateFrameTimestamps = duplicateFrameTimestamps,
                        OutOfOrderFrameTimestamps = outOfOrderFrameTimestamps,
                        InvalidFrameIntervals = invalidFrameIntervals,
                        FrameRingBufferOverrun = frameRingBufferOverrun,
                        HasFrameTargetVerification = hasFrameTargetVerification,
                        FrameTargetVerified = frameTargetVerified,
                        SurfaceOwnerPid = surfaceOwnerPid,
                        SurfaceOwnerUid = surfaceOwnerUid,
                        FpsScope = _latestFpsScope,
                        FrameSource = _latestFrameSource,
                        HasFrameSourceElapsed = hasSourceElapsed,
                        FrameSourceElapsedSec = hasSourceElapsed ? parsedSourceElapsed : 0,
                        HasFrameSourceSequence = hasSourceSequence,
                        FrameSourceSequence = hasSourceSequence ? parsedSourceSequence : 0,
                        FrameSourceSequenceDiscontinuity = frameSourceSequenceDiscontinuity,
                        MissingFrameSourceWindows = missingFrameSourceWindows,
                        HasFreshnessMetadata = true,
                        Source = _latestFpsSource
                    });
                    SignalSampleLoop();
                }
                else if (kind == "memory")
                {
                    object memoryObject = Get(obj, "Memory") ?? Get(obj, "value");
                    string memoryUnit = Convert.ToString(Get(obj, "unit") ?? "");
                    if (memoryObject == null)
                    {
                        memoryObject = Get(obj, "physFootprint");
                        if (string.IsNullOrWhiteSpace(memoryUnit)) memoryUnit = "B";
                    }
                    double? memoryValue = TryParseMemory(memoryObject, memoryUnit);
                    if (memoryValue.HasValue)
                    {
                        DateTime receivedAt = DateTime.Now;
                        double? rssValue = TryParseMemory(Get(obj, "rss_value"), memoryUnit);
                        _processDataSeen = true;
                        _latestMemory = memoryValue.Value;
                        _latestMemoryAt = receivedAt;
                        _latestMemorySequence++;
                        _latestMemoryMetric = Convert.ToString(Get(obj, "metric") ?? "");
                        _latestMemorySource = Convert.ToString(Get(obj, "source") ?? "");
                        _latestMemoryRss = rssValue;
                        PerfSample sample = CreateCurrentStateSampleLocked(receivedAt, isAndroid);
                        sample.MemoryUpdated = true;
                        _pendingProcessSamples.Enqueue(sample);
                        SignalSampleLoop();
                    }
                }
                else if (kind == "cpu")
                {
                    double? cpuValue = ParseCpu(obj);
                    double? normalizedCpuValue = ParseCpuNormalized(obj);
                    List<double> coreValues = ParseCpuCoreValues(obj);
                    bool validCoreValues = coreValues != null && coreValues.Count > 0 && IsValidCpuCoreValues(coreValues);
                    if (cpuValue.HasValue || normalizedCpuValue.HasValue || validCoreValues)
                    {
                        DateTime receivedAt = DateTime.Now;
                        if (cpuValue.HasValue)
                        {
                            _processDataSeen = true;
                            _latestCpu = cpuValue.Value;
                            _latestCpuAt = receivedAt;
                            _latestCpuSequence++;
                            _latestCpuSource = Convert.ToString(Get(obj, "source") ?? "");
                        }
                        if (normalizedCpuValue.HasValue)
                        {
                            _latestCpuNormalized = normalizedCpuValue.Value;
                            _latestCpuNormalizedAt = receivedAt;
                            _latestCpuNormalizedSequence++;
                            _latestCpuNormalizedSource = Convert.ToString(Get(obj, "normalized_source") ?? Get(obj, "source") ?? "");
                        }
                        if (validCoreValues)
                        {
                            _latestCpuCorePercents = coreValues;
                            _latestCpuCoreAt = receivedAt;
                            _latestCpuCoreSequence++;
                            _latestCpuCoreSource = Convert.ToString(Get(obj, "core_source") ?? Get(obj, "source") ?? "");
                            _latestCpuCoreScope = Convert.ToString(Get(obj, "core_scope") ?? "device");
                        }
                        PerfSample sample = CreateCurrentStateSampleLocked(receivedAt, isAndroid);
                        sample.CpuUpdated = cpuValue.HasValue;
                        sample.CpuNormalizedUpdated = normalizedCpuValue.HasValue;
                        sample.CpuCoreUpdated = validCoreValues;
                        _pendingProcessSamples.Enqueue(sample);
                        SignalSampleLoop();
                    }
                }
                else if (kind == "temperature")
                {
                    Dictionary<string, double> values = ParseTemperatureValues(Get(obj, "values"));
                    if (values.Count > 0)
                    {
                        DateTime receivedAt = DateTime.Now;
                        PerfSample sample = CreateCurrentStateSampleLocked(receivedAt, isAndroid);
                        sample.HasTemperature = true;
                        sample.TemperatureCelsius = values;
                        sample.TemperatureUpdated = true;
                        sample.TemperatureSource = Convert.ToString(Get(obj, "source") ?? "");
                        sample.TemperatureScope = Convert.ToString(Get(obj, "scope") ?? "device");
                        sample.Source = sample.TemperatureSource;
                        _pendingProcessSamples.Enqueue(sample);
                        SignalSampleLoop();
                    }
                }
                else
                {
                    int level;
                    string platform = Convert.ToString(Get(obj, "platform") ?? "").Trim().ToLowerInvariant();
                    string name = Convert.ToString(Get(obj, "state") ?? "").Trim().ToLowerInvariant();
                    if (TryThermalStateLevel(Get(obj, "value"), out level) && ThermalStateName(platform, level) == name)
                    {
                        DateTime receivedAt = DateTime.Now;
                        PerfSample sample = CreateCurrentStateSampleLocked(receivedAt, isAndroid);
                        sample.HasThermalState = true;
                        sample.ThermalStateLevel = level;
                        sample.ThermalStateName = name;
                        sample.ThermalStateUpdated = true;
                        sample.ThermalStateSource = Convert.ToString(Get(obj, "source") ?? "");
                        sample.ThermalStateScope = Convert.ToString(Get(obj, "scope") ?? "device");
                        _latestThermalState = level;
                        _latestThermalStateAt = receivedAt;
                        _latestThermalStateName = name;
                        _latestThermalStateSource = sample.ThermalStateSource;
                        _latestThermalStateScope = sample.ThermalStateScope;
                        sample.Source = sample.ThermalStateSource;
                        _pendingProcessSamples.Enqueue(sample);
                        SignalSampleLoop();
                    }
                }
            }
        }

        private PerfSample CreateCurrentStateSampleLocked(DateTime now, bool isAndroid)
        {
            double fpsFreshSeconds = isAndroid ? AndroidFpsFreshSeconds : IosFpsFreshSeconds;
            bool hasFreshFps = _latestFps.HasValue
                && _latestFpsAt.HasValue
                && (now - _latestFpsAt.Value).TotalSeconds <= fpsFreshSeconds;
            bool hasFreshMemory = _targetConfirmed && _latestMemory.HasValue
                && _latestMemoryAt.HasValue
                && (now - _latestMemoryAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
            bool hasFreshCpu = _targetConfirmed && _latestCpu.HasValue
                && _latestCpuAt.HasValue
                && (now - _latestCpuAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
            bool hasFreshCpuNormalized = _targetConfirmed && _latestCpuNormalized.HasValue
                && _latestCpuNormalizedAt.HasValue
                && (now - _latestCpuNormalizedAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
            bool hasFreshCpuCoreUsage = _targetConfirmed && _latestCpuCorePercents != null
                && _latestCpuCorePercents.Count > 0
                && _latestCpuCoreAt.HasValue
                && (now - _latestCpuCoreAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
            bool hasFreshThermalState = _latestThermalState.HasValue
                && _latestThermalStateAt.HasValue
                && (now - _latestThermalStateAt.Value).TotalSeconds <= ProcessMetricFreshSeconds;
            bool hasMemoryRss = hasFreshMemory && _latestMemoryRss.HasValue;
            return new PerfSample
            {
                Timestamp = now,
                HasFps = hasFreshFps,
                Fps = hasFreshFps ? _latestFps.Value : 0,
                FpsUpdated = false,
                HasAverageFps = hasFreshFps && _latestAverageFps.HasValue,
                AverageFps = hasFreshFps && _latestAverageFps.HasValue ? _latestAverageFps.Value : 0,
                HasFrameTime = hasFreshFps && _latestFrameTimeMs.HasValue,
                FrameTimeMs = hasFreshFps && _latestFrameTimeMs.HasValue ? _latestFrameTimeMs.Value : 0,
                HasFrameTimeMean = hasFreshFps && _latestFrameTimeMeanMs.HasValue,
                FrameTimeMeanMs = hasFreshFps && _latestFrameTimeMeanMs.HasValue ? _latestFrameTimeMeanMs.Value : 0,
                HasFrameTimeP95 = hasFreshFps && _latestFrameTimeP95Ms.HasValue,
                FrameTimeP95Ms = hasFreshFps && _latestFrameTimeP95Ms.HasValue ? _latestFrameTimeP95Ms.Value : 0,
                HasFrameTimeMax = hasFreshFps && _latestFrameTimeMaxMs.HasValue,
                FrameTimeMaxMs = hasFreshFps && _latestFrameTimeMaxMs.HasValue ? _latestFrameTimeMaxMs.Value : 0,
                HasJank = hasFreshFps && _nativeJankSeen,
                Jank = hasFreshFps && _nativeJankSeen ? _latestJank : 0,
                BigJank = hasFreshFps && _nativeJankSeen ? _latestBigJank : 0,
                HasStutter = hasFreshFps && _nativeStutterSeen,
                StutterPercent = hasFreshFps && _nativeStutterSeen ? _latestStutterPercent : 0,
                JankTimeMs = hasFreshFps && _nativeStutterSeen ? _latestJankTimeMs : 0,
                HasFrameObservation = false,
                FrameCount = 0,
                FrameObservationMs = 0,
                HasRefreshRate = hasFreshFps && _latestRefreshRateHz.HasValue,
                RefreshRateHz = hasFreshFps && _latestRefreshRateHz.HasValue ? _latestRefreshRateHz.Value : 0,
                OrderedFrames = hasFreshFps && _latestOrderedFrames,
                ApproximateFrameMetrics = hasFreshFps && _latestApproximateFrameMetrics,
                SourceDegraded = hasFreshFps && _latestSourceDegraded,
                HasFrameTargetVerification = hasFreshFps && _latestHasFrameTargetVerification,
                FrameTargetVerified = hasFreshFps && _latestFrameTargetVerified,
                SurfaceOwnerPid = hasFreshFps ? _latestSurfaceOwnerPid : 0,
                SurfaceOwnerUid = hasFreshFps ? _latestSurfaceOwnerUid : 0,
                FpsScope = hasFreshFps ? _latestFpsScope : "",
                FrameSource = hasFreshFps ? _latestFrameSource : "",
                HasMemory = hasFreshMemory,
                MemoryMb = hasFreshMemory ? _latestMemory.Value : 0,
                MemoryUpdated = false,
                MemoryMetric = hasFreshMemory ? _latestMemoryMetric : "",
                MemorySource = hasFreshMemory ? _latestMemorySource : "",
                HasMemoryRss = hasMemoryRss,
                MemoryRssMb = hasMemoryRss ? _latestMemoryRss.Value : 0,
                HasCpu = hasFreshCpu,
                CpuPercent = hasFreshCpu ? _latestCpu.Value : 0,
                CpuUpdated = false,
                CpuSource = hasFreshCpu ? _latestCpuSource : "",
                HasCpuNormalized = hasFreshCpuNormalized,
                CpuNormalizedPercent = hasFreshCpuNormalized ? _latestCpuNormalized.Value : 0,
                CpuNormalizedUpdated = false,
                CpuNormalizedSource = hasFreshCpuNormalized ? _latestCpuNormalizedSource : "",
                HasCpuCoreUsage = hasFreshCpuCoreUsage,
                CpuCorePercents = hasFreshCpuCoreUsage ? new List<double>(_latestCpuCorePercents) : new List<double>(),
                CpuCoreCount = hasFreshCpuCoreUsage ? _latestCpuCorePercents.Count : 0,
                CpuCoreUpdated = false,
                CpuCoreSource = hasFreshCpuCoreUsage ? _latestCpuCoreSource : "",
                CpuCoreScope = hasFreshCpuCoreUsage ? _latestCpuCoreScope : "",
                HasThermalState = hasFreshThermalState,
                ThermalStateLevel = hasFreshThermalState ? _latestThermalState.Value : 0,
                ThermalStateName = hasFreshThermalState ? _latestThermalStateName : "",
                ThermalStateSource = hasFreshThermalState ? _latestThermalStateSource : "",
                ThermalStateScope = hasFreshThermalState ? _latestThermalStateScope : "",
                HasFreshnessMetadata = true,
                Source = hasFreshFps ? _latestFpsSource : ""
            };
        }

        private static object Get(Dictionary<string, object> obj, string key)
        {
            return obj.ContainsKey(key) ? obj[key] : null;
        }

        private static double AsDouble(object value)
        {
            if (value == null) return 0;
            if (value is double) return (double)value;
            if (value is long) return (long)value;
            double parsed;
            double.TryParse(Convert.ToString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
            return parsed;
        }

        private static bool TryAsDouble(object value, out double parsed)
        {
            parsed = 0;
            if (value == null) return false;
            if (value is JsonElement json)
            {
                if (json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out parsed)) return true;
                if (json.ValueKind == JsonValueKind.String)
                {
                    return double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
                }
                return false;
            }
            if (value is double)
            {
                parsed = (double)value;
                return true;
            }
            if (value is long)
            {
                parsed = (long)value;
                return true;
            }
            return double.TryParse(Convert.ToString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryNonNegativeDouble(object value, out double parsed)
        {
            if (!TryAsDouble(value, out parsed)) return false;
            return !double.IsNaN(parsed) && !double.IsInfinity(parsed) && parsed >= 0;
        }

        private static bool TryNonNegativeInt(object value, out int parsed)
        {
            parsed = 0;
            double number;
            if (!TryAsDouble(value, out number) || double.IsNaN(number) || double.IsInfinity(number)) return false;
            if (number < 0 || number > int.MaxValue || Math.Abs(number - Math.Round(number)) > 0.000001) return false;
            parsed = (int)Math.Round(number);
            return true;
        }

        private static bool TryPositiveLong(object value, out long parsed)
        {
            parsed = 0;
            double number;
            if (!TryAsDouble(value, out number) || double.IsNaN(number) || double.IsInfinity(number)) return false;
            if (number <= 0 || number > long.MaxValue || Math.Abs(number - Math.Round(number)) > 0.000001) return false;
            parsed = (long)Math.Round(number);
            return true;
        }

        private static bool AsBool(object value)
        {
            if (value is bool) return (bool)value;
            bool parsed;
            return value != null && bool.TryParse(Convert.ToString(value), out parsed) && parsed;
        }

        private static double? ParseValidFrameTime(object value)
        {
            double parsed;
            return TryAsDouble(value, out parsed) && IsValidFrameTimeMs(parsed) ? parsed : (double?)null;
        }

        private static double? TryParseMemory(object value, string explicitUnit)
        {
            if (value == null) return null;
            if (value is double || value is long)
            {
                double number = AsDouble(value);
                if (double.IsNaN(number) || double.IsInfinity(number) || number <= 0) return null;
                return MemoryNumberToMb(number, explicitUnit);
            }
            string text = Convert.ToString(value);
            Match match = Regex.Match(text ?? "", @"([-+]?[0-9]*\.?[0-9]+)\s*([kmgt]?i?b|b)?", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            double n = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (double.IsNaN(n) || double.IsInfinity(n) || n <= 0) return null;
            string unit = match.Groups[2].Value.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(unit)) unit = (explicitUnit ?? "").Trim().ToLowerInvariant();
            return MemoryNumberToMb(n, unit);
        }

        private static double MemoryNumberToMb(double value, string unit)
        {
            string normalized = (unit ?? "").Trim().ToLowerInvariant();
            if (normalized == "b") return value / 1024 / 1024;
            if (normalized == "kb" || normalized == "kib") return value / 1024;
            if (normalized == "gb" || normalized == "gib") return value * 1024;
            if (normalized == "tb" || normalized == "tib") return value * 1024 * 1024;
            if (normalized == "mb" || normalized == "mib") return value;
            return value > 1024 * 1024 ? value / 1024 / 1024 : value;
        }

        private static double? ParseCpu(Dictionary<string, object> obj)
        {
            object cpuObject =
                Get(obj, "cpu_usage") ??
                Get(obj, "value") ??
                Get(obj, "CPU") ??
                Get(obj, "cpu") ??
                Get(obj, "usage") ??
                Get(obj, "total") ??
                Get(obj, "process") ??
                Get(obj, "app");
            double value;
            if (!TryAsDouble(cpuObject, out value)) return null;
            return NormalizeCpuRawPercent(value);
        }

        private static double? ParseCpuNormalized(Dictionary<string, object> obj)
        {
            object normalizedObject =
                Get(obj, "normalized_value") ??
                Get(obj, "normalized_cpu") ??
                Get(obj, "normalized");
            double value;
            if (!TryAsDouble(normalizedObject, out value)) return null;
            return NormalizeCpuRawPercent(value);
        }

        private static List<double> ParseCpuCoreValues(Dictionary<string, object> obj)
        {
            object coreObject = Get(obj, "core_values") ?? Get(obj, "cores");
            if (coreObject is JsonElement json)
            {
                if (json.ValueKind != JsonValueKind.Array) return null;
                List<double> values = new List<double>();
                foreach (JsonElement item in json.EnumerateArray())
                {
                    double value;
                    if (!TryAsDouble(item, out value)) return null;
                    values.Add(value);
                }
                return values;
            }
            IEnumerable<object> enumerable = coreObject as IEnumerable<object>;
            if (enumerable == null) return null;
            List<double> parsed = new List<double>();
            foreach (object item in enumerable)
            {
                double value;
                if (!TryAsDouble(item, out value)) return null;
                parsed.Add(value);
            }
            return parsed;
        }

        private static Dictionary<string, double> ParseTemperatureValues(object valuesObject)
        {
            Dictionary<string, double> parsed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            JsonElement json;
            if (valuesObject is JsonElement)
            {
                json = (JsonElement)valuesObject;
                if (json.ValueKind != JsonValueKind.Object) return parsed;
                foreach (JsonProperty property in json.EnumerateObject())
                {
                    double value;
                    if (TryAsDouble(property.Value, out value) && IsValidTemperatureCelsius(value))
                    {
                        parsed[property.Name] = value;
                    }
                }
                return parsed;
            }
            Dictionary<string, object> values = valuesObject as Dictionary<string, object>;
            if (values == null) return parsed;
            foreach (KeyValuePair<string, object> pair in values)
            {
                double value;
                if (TryAsDouble(pair.Value, out value) && IsValidTemperatureCelsius(value))
                {
                    parsed[pair.Key] = value;
                }
            }
            return parsed;
        }

        private static bool IsValidCpuCoreValues(List<double> values)
        {
            return values != null
                && values.Count > 0
                && values.All(delegate(double value)
                {
                    return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
                });
        }

        private static double? NormalizeCpuRawPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return null;
            return value;
        }

        private static bool IsValidFps(double fps)
        {
            return !double.IsNaN(fps) && !double.IsInfinity(fps) && fps >= 0 && fps <= MaximumReasonableFps;
        }

        private static bool IsValidTemperatureCelsius(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= -20 && value <= 120;
        }

        private static bool TryThermalStateLevel(object value, out int level)
        {
            level = 0;
            int parsed;
            if (!TryNonNegativeInt(value, out parsed) || parsed > 6) return false;
            level = parsed;
            return true;
        }

        private static string ThermalStateName(string platform, int level)
        {
            if (string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase))
            {
                if (level == 0) return "none";
                if (level == 1) return "light";
                if (level == 2) return "moderate";
                if (level == 3) return "severe";
                if (level == 4) return "critical";
                if (level == 5) return "emergency";
                if (level == 6) return "shutdown";
                return "";
            }
            if (level == 0) return "nominal";
            if (level == 1) return "fair";
            if (level == 2) return "serious";
            if (level == 3) return "critical";
            return "";
        }

        private static bool IsValidFrameTimeMs(double frameTimeMs)
        {
            return !double.IsNaN(frameTimeMs) && !double.IsInfinity(frameTimeMs) && frameTimeMs > 0 && frameTimeMs <= MaximumReasonableFrameTimeMs;
        }

        private static bool IsValidRefreshRate(double refreshRateHz)
        {
            return !double.IsNaN(refreshRateHz) && !double.IsInfinity(refreshRateHz) && refreshRateHz > 0 && refreshRateHz <= 1000.0;
        }

        private bool IsGenerationCurrent(int generation)
        {
            lock (_lock)
            {
                return generation == _captureGeneration;
            }
        }

        private static void Raise<T>(Action<T> handler, T value)
        {
            if (handler != null) handler(value);
        }

        private void FailAndStop(string message)
        {
            FailAndStop(message, "capture_failure");
        }

        private void FailAndStop(string message, string reason)
        {
            string diagnosticMessage = WithDiagnosticsLogPath(message);
            WriteDiagnosticsEvent("capture_failure", diagnosticMessage, reason);
            Stop(reason);
            Raise(Failed, diagnosticMessage);
        }

        private string WithDiagnosticsLogPath(string message)
        {
            string value = message ?? "";
            string path = LastDiagnosticsLogPath;
            return string.IsNullOrWhiteSpace(path) || value.IndexOf("详细日志：", StringComparison.Ordinal) >= 0
                ? value
                : value + "；详细日志：" + path;
        }

        private static async Task WaitForExitAsync(Process process)
        {
            while (!process.HasExited)
            {
                await Task.Delay(120);
            }
        }

        private void RecordStderr(int generation, string line)
        {
            lock (_lock)
            {
                if (generation != _captureGeneration) return;
                _stderrTail.Enqueue(Clip(line));
                while (_stderrTail.Count > 8) _stderrTail.Dequeue();
            }
        }

        private string StderrTail(int generation)
        {
            lock (_lock)
            {
                return generation == _captureGeneration ? string.Join(" | ", _stderrTail) : "";
            }
        }

        private void WriteDiagnosticsRunnerExit(string label, int exitCode, string stderr)
        {
            lock (_lock)
            {
                if (_diagnosticsLog != null) _diagnosticsLog.WriteRunnerExit(label, exitCode, stderr);
            }
        }

        private void WriteDiagnosticsStderr(string label, string line)
        {
            lock (_lock)
            {
                if (_diagnosticsLog != null) _diagnosticsLog.WriteRunnerStderr(label, line);
            }
        }

        private void WriteDiagnosticsEvent(string eventName, string message, string code)
        {
            lock (_lock)
            {
                if (_diagnosticsLog != null) _diagnosticsLog.WriteEvent(eventName, message, code);
            }
        }

        private void WritePidReboundDiagnostics(int oldPid, int newPid, string bundleId, string reason)
        {
            lock (_lock)
            {
                if (_diagnosticsLog != null) _diagnosticsLog.WritePidRebound(oldPid, newPid, bundleId, reason);
            }
        }

        private static string Clip(string text)
        {
            text = string.Join(" ", (text ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return text.Length <= 220 ? text : text.Substring(0, 220) + "...";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
