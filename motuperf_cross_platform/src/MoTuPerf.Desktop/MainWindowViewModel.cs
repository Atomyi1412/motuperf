using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using CSharpIosPerfMonitor;
using MoTuPerf.Platform;

namespace MoTuPerf.Desktop
{
    public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
    {
        private const double ProcessMetricDisplayFreshSeconds = 6.0;
        private const double TemperatureDisplayFreshSeconds = 7.5;
        private const double IosFrameMetricDisplayFreshSeconds = 3.5;
        private const double AndroidFrameMetricDisplayFreshSeconds = 4.0;
        private const double IosThermalStateDisplayFreshSeconds = 2.5;
        private const double AndroidThermalStateDisplayFreshSeconds = 7.5;
        private PerfCollector _collector = new PerfCollector();
        private ScreenshotService _screenshots;
        private readonly CaptureUiBuffer _captureUi = new CaptureUiBuffer();
        private readonly DispatcherTimer _sampleRefreshTimer;
        private long _uiGeneration;
        private readonly DeviceLookupService _deviceLookup = new DeviceLookupService();
        private readonly ReleaseUpdateService _releaseUpdates = new ReleaseUpdateService();
        private readonly UpdateSettingsStore _updateSettings = new UpdateSettingsStore();
        private bool _capturing;
        private string _status = "未连接设备";
        private DeviceSelection _selection;
        private List<PerfSample> _sampleBuffer = new List<PerfSample>();
        private readonly List<ScreenshotItemViewModel> _screenshotItems = new List<ScreenshotItemViewModel>();
        private IReadOnlyList<PerfSample> _samples = Array.Empty<PerfSample>();
        private double? _selectedTime;
        private ScreenshotItemViewModel _selectedScreenshot;
        private double _viewStartTime;
        private double _viewEndTime;
        private bool _chartZoomEnabled;
        private bool _followLatest = true;
        private bool _parametersCollapsed;
        private DataPanelTab _dataPanelTab = DataPanelTab.Analysis;
        private bool _deviceInfoVisible;
        private bool _checkForUpdates;
        private string _skippedUpdateVersion;
        private CancellationTokenSource _captureCancellation;
        private DateTime _sessionStartedAt;
        private bool _selectionNeedsRefresh;
        private readonly Dictionary<string, DataMetricTileViewModel> _liveDataTiles = new Dictionary<string, DataMetricTileViewModel>(StringComparer.Ordinal);
        private readonly Dictionary<string, DataMetricTileViewModel> _selectedDataTiles = new Dictionary<string, DataMetricTileViewModel>(StringComparer.Ordinal);

        public MainWindowViewModel()
        {
            _screenshots = new ScreenshotService(Path.Combine(RuntimeTools.DataDirectory, "screenshots"));
            _checkForUpdates = _updateSettings.LoadCheckForUpdates();
            _skippedUpdateVersion = _updateSettings.LoadSkippedVersion();
            LiveDataTiles = CreateDataTiles(_liveDataTiles);
            SelectedDataTiles = CreateDataTiles(_selectedDataTiles);
            AnalysisDataRows = new ObservableCollection<AnalysisMetricRowViewModel>();
            Metrics = new ObservableCollection<MetricRowViewModel>
            {
                new MetricRowViewModel("Screenshot", "设备截屏", "--", "", "#4DB5E0"),
                new MetricRowViewModel("FPS", "帧率", "--", "帧/s", "#FF6B9A"),
                new MetricRowViewModel("Display FrameTime", "窗口最大帧间隔", "--", "ms", "#FF6B9A"),
                new MetricRowViewModel("Process Memory", "所选 PID 内存", "--", "MB", "#F3C44F"),
                new MetricRowViewModel("Process CPU Raw", "所选 PID 原始 CPU", "--", "%", "#91DB72"),
                new MetricRowViewModel("Device Temperature", "设备传感器温度", "--", "°C", "#F08A60"),
                new MetricRowViewModel("Thermal State", "系统热状态", "--", "0-6", "#C98BE8"),
            };
            foreach (MetricRowViewModel metric in Metrics)
            {
                metric.PropertyChanged += MetricChanged;
            }
            UpdateAnalysisData();
            ToggleCaptureCommand = new RelayCommand(ToggleCapture);

            _sampleRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _sampleRefreshTimer.Tick += delegate { ApplySamples(_captureUi.Drain(_uiGeneration)); };
        }

        private void BindCaptureCallbacks(long generation, PerfCollector collector, ScreenshotService screenshots)
        {
            collector.SampleReady += sample =>
            {
                if (_captureUi.Enqueue(generation, sample))
                    screenshots.CaptureDue(sample.ElapsedSec, (int)(sample.ElapsedSec * 1000), CancellationToken.None);
            };
            collector.Message += message => PostCaptureStatus(generation, message);
            collector.Failed += message =>
            {
                Dispatcher.UIThread.Post(delegate
                {
                    if (!_captureUi.IsCurrent(generation)) return;
                    StopCapture(message, "collector_failed");
                    CaptureStoppedUnexpectedly?.Invoke(message);
                });
            };
            screenshots.ScreenshotReady += screenshot => HandleScreenshot(screenshot, generation);
            screenshots.Failed += message =>
            {
                if (!_captureUi.IsCurrent(generation)) return;
                collector.RecordEvent("screenshot_failed", message, "screenshot_error");
                PostCaptureStatus(generation, message);
            };
        }

        private void PostCaptureStatus(long generation, string message)
        {
            Dispatcher.UIThread.Post(delegate { if (_captureUi.IsCurrent(generation)) Status = message; });
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<string> CaptureStoppedUnexpectedly;
        public string Version { get { return "v0.25.2"; } }
        public IReadOnlyList<AppThemeDefinition> ThemeOptions { get { return AppThemeManager.Themes; } }
        public string CurrentThemeName { get { return AppThemeManager.Current.DisplayName; } }
        public string CurrentThemePreviewColor { get { return AppThemeManager.Current.PreviewColor; } }
        public string ThemeSelectorToolTip { get { return "选择主题"; } }
        public string DataDirectory { get { return RuntimeTools.DataDirectory; } }
        public bool CheckForUpdates
        {
            get { return _checkForUpdates; }
            set
            {
                if (_checkForUpdates == value) return;
                _checkForUpdates = value;
                _updateSettings.Save(value, _skippedUpdateVersion);
                OnPropertyChanged();
            }
        }

        public void RefreshThemePresentation()
        {
            OnPropertyChanged(nameof(CurrentThemeName));
            OnPropertyChanged(nameof(CurrentThemePreviewColor));
            OnPropertyChanged(nameof(ThemeSelectorToolTip));
            foreach (MetricRowViewModel metric in Metrics) metric.RefreshThemeColors();
        }
        public string ThermalMetricTitle { get { return IsAndroidSelection ? "Thermal Status" : "Thermal State"; } }
        public string ThermalMetricLegend
        {
            get
            {
                return IsAndroidSelection
                    ? "0正常 / 1轻微 / 2中度 / 3严重 / 4临界 / 5紧急 / 6关机"
                    : "0正常 / 1升温 / 2严重 / 3临界";
            }
        }
        public int ThermalAxisMax { get { return IsAndroidSelection ? 6 : 3; } }
        public string ThermalWaitingText { get { return "等待 " + ThermalMetricTitle + " 数据"; } }
        public bool IsParametersCollapsed
        {
            get { return _parametersCollapsed; }
            set
            {
                if (_parametersCollapsed == value) return;
                _parametersCollapsed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ParametersWidth));
            }
        }
        public double ParametersWidth { get { return IsParametersCollapsed ? 42 : 398; } }
        public bool ShowLiveData { get { return _dataPanelTab == DataPanelTab.Live; } }
        public bool ShowSelectedData { get { return _dataPanelTab == DataPanelTab.Selected; } }
        public bool ShowAnalysisData { get { return _dataPanelTab == DataPanelTab.Analysis; } }
        public ObservableCollection<DataMetricTileViewModel> LiveDataTiles { get; private set; }
        public ObservableCollection<DataMetricTileViewModel> SelectedDataTiles { get; private set; }
        public ObservableCollection<AnalysisMetricRowViewModel> AnalysisDataRows { get; private set; }
        public string TimelineLabel
        {
            get { return "时间轴  " + (!_selectedTime.HasValue ? "0s" : _selectedTime.Value.ToString("0.0", CultureInfo.InvariantCulture) + "s"); }
        }
        public string TargetSummary
        {
            get
            {
                if (_selection == null || _selection.Process == null) return "未开始采集";
                string target = FirstNonEmpty(_selection.Process.Name, _selection.Process.BundleId);
                return (IsCapturing ? "采集中：" : "目标：") + "pid " + _selection.Process.Pid + (string.IsNullOrWhiteSpace(target) ? "" : " / " + target);
            }
        }
        public DeviceSelection CurrentSelection { get { return _selection; } }
        public bool CaptureSelectionNeedsPicker
        {
            get
            {
                return _selectionNeedsRefresh
                    || _selection == null
                    || _selection.Device == null
                    || !ProcessTargetMatcher.IsValidTarget(_selection.Process);
            }
        }
        public string DeviceSummary { get { return _selection == null ? "未选择设备" : _selection.Device.PickerLabel; } }
        public string ProcessSummary
        {
            get
            {
                if (_selection == null) return "未选择应用 / 进程";
                string app = _selection.App == null ? "未选择 APP" : FirstNonEmpty(_selection.App.Name, _selection.App.BundleId);
                return app + " / pid " + _selection.Process.Pid + " · " + _selection.Process.PickerName;
            }
        }
        public bool IsDeviceInfoVisible
        {
            get { return _deviceInfoVisible; }
            private set
            {
                if (_deviceInfoVisible == value) return;
                _deviceInfoVisible = value;
                OnPropertyChanged();
            }
        }
        public string DeviceInfoText
        {
            get
            {
                DeviceInfo device = _selection?.Device;
                AppInfo app = _selection?.App;
                ProcessInfo process = _selection?.Process;
                StringBuilder text = new StringBuilder();
                text.Append("设备名称：\n").Append(device == null ? "未选择设备" : device.ToString());
                text.Append("\n\n当前应用：\n");
                if (app == null)
                {
                    text.Append("未选择应用");
                }
                else
                {
                    text.Append(FirstNonEmpty(app.BundleId, app.Name, "-"));
                    if (!string.IsNullOrWhiteSpace(app.Name) && !string.Equals(app.Name, app.BundleId, StringComparison.Ordinal))
                        text.Append('\n').Append(app.Name);
                }
                text.Append("\n\n当前进程：\n");
                if (process == null)
                {
                    text.Append("未选择进程");
                }
                else
                {
                    text.Append(FirstNonEmpty(process.Name, process.BundleId, "-")).Append("\npid ").Append(process.Pid);
                }
                text.Append("\n\n设备详情：\n");
                AppendDeviceDetail(text, "CPU信息", device?.CpuInfo);
                AppendDeviceDetail(text, "GPU信息", device?.GpuInfo);
                AppendDeviceDetail(text, "系统信息", DeviceOsText(device));
                AppendDeviceDetail(text, "分辨率", device?.Resolution);
                AppendDeviceDetail(text, "平台", DevicePlatformName(device));
                AppendDeviceDetail(text, "连接方式", device?.ConnType);
                return text.ToString().TrimEnd();
            }
        }
        public string Status
        {
            get { return _status; }
            set { if (_status == value) return; _status = value; OnPropertyChanged(); }
        }
        public bool IsCapturing
        {
            get { return _capturing; }
            private set
            {
                if (_capturing == value) return;
                _capturing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CaptureText));
                OnPropertyChanged(nameof(CaptureLabel));
                OnPropertyChanged(nameof(TargetSummary));
                OnPropertyChanged(nameof(CanUseFileActions));
                OnPropertyChanged(nameof(CanChangeDevice));
                OnPropertyChanged(nameof(CanToggleCapture));
            }
        }
        private bool _fileOperationInProgress;
        private string _fileOperationText = "正在处理现场文件，请稍候...";
        private string _fileOperationDetailText = "正在处理曲线、截图与设备信息，请勿关闭软件";
        private double _fileOperationProgress;
        private bool _fileOperationIndeterminate = true;
        private bool _fileOperationProgressVisible;
        private string _fileOperationProgressText = "";
        private DateTime _fileOperationStartedAtUtc;
        public bool IsFileOperationInProgress { get { return _fileOperationInProgress; } private set { _fileOperationInProgress = value; } }
        public bool CanUseFileActions { get { return !IsCapturing && !IsFileOperationInProgress; } }
        public bool CanChangeDevice { get { return !IsCapturing && !IsFileOperationInProgress; } }
        public bool CanToggleCapture { get { return !IsFileOperationInProgress; } }
        public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual, CancellationToken token)
        {
            if (!manual && !CheckForUpdates) return new UpdateCheckResult { Message = "已关闭启动更新检查" };
            try { return await _releaseUpdates.CheckAsync(Version, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                PostStatus("检查更新失败：" + exception.Message);
                return new UpdateCheckResult { Message = "检查更新失败" };
            }
        }

        public async Task<string> DownloadUpdateAsync(UpdateAsset asset, IProgress<UpdateDownloadProgress> progress, CancellationToken token)
        {
            string directory = Path.Combine(RuntimeTools.DataDirectory, "downloads");
            return await _releaseUpdates.DownloadAsync(asset, directory, progress, token).ConfigureAwait(false);
        }

        public bool IsUpdateSkipped(string version)
        {
            return !string.IsNullOrWhiteSpace(version) && string.Equals(_skippedUpdateVersion, version, StringComparison.OrdinalIgnoreCase);
        }

        public void SkipUpdate(string version)
        {
            _skippedUpdateVersion = version;
            _updateSettings.Save(_checkForUpdates, _skippedUpdateVersion);
        }
        public string FileOperationText { get { return _fileOperationText; } }
        public string FileOperationDetailText { get { return _fileOperationDetailText; } }
        public double FileOperationProgress { get { return _fileOperationProgress; } private set { if (Math.Abs(_fileOperationProgress - value) < 0.01) return; _fileOperationProgress = value; OnPropertyChanged(); } }
        public bool IsFileOperationIndeterminate { get { return _fileOperationIndeterminate; } private set { if (_fileOperationIndeterminate == value) return; _fileOperationIndeterminate = value; OnPropertyChanged(); } }
        public bool IsFileOperationProgressVisible { get { return _fileOperationProgressVisible; } private set { if (_fileOperationProgressVisible == value) return; _fileOperationProgressVisible = value; OnPropertyChanged(); } }
        public string FileOperationProgressText { get { return _fileOperationProgressText; } private set { if (_fileOperationProgressText == value) return; _fileOperationProgressText = value; OnPropertyChanged(); } }

        // Keep the old names for callers and tests that only know about session loading.
        public bool IsSessionLoading { get { return IsFileOperationInProgress; } }
        public string SessionLoadingText { get { return FileOperationText; } }
        public bool BeginFileOperation(string title, string detail)
        {
            if (IsFileOperationInProgress) return false;
            _fileOperationText = string.IsNullOrWhiteSpace(title) ? "正在处理现场文件，请稍候..." : title;
            _fileOperationDetailText = string.IsNullOrWhiteSpace(detail) ? "正在处理曲线、截图与设备信息，请勿关闭软件" : detail;
            _fileOperationStartedAtUtc = DateTime.UtcNow;
            FileOperationProgress = 0;
            IsFileOperationIndeterminate = true;
            IsFileOperationProgressVisible = false;
            FileOperationProgressText = "";
            IsFileOperationInProgress = true;
            OnPropertyChanged(nameof(IsFileOperationInProgress));
            OnPropertyChanged(nameof(IsSessionLoading));
            OnPropertyChanged(nameof(CanUseFileActions));
            OnPropertyChanged(nameof(CanChangeDevice));
            OnPropertyChanged(nameof(CanToggleCapture));
            OnPropertyChanged(nameof(FileOperationText));
            OnPropertyChanged(nameof(FileOperationDetailText));
            OnPropertyChanged(nameof(SessionLoadingText));
            return true;
        }

        public void SetDataMigrationProgress(RuntimeDataMigrationProgress progress)
        {
            if (!IsFileOperationInProgress || progress == null) return;
            if (progress.Stage != "复制")
            {
                IsFileOperationIndeterminate = true;
                IsFileOperationProgressVisible = true;
                FileOperationProgressText = "正在" + progress.Stage + "，请稍候...";
                return;
            }
            IsFileOperationIndeterminate = false;
            FileOperationProgress = Math.Max(0, Math.Min(100, progress.Percent));
            IsFileOperationProgressVisible = true;
            if (progress.TotalBytes == 0)
            {
                FileOperationProgressText = "没有需要迁移的数据";
                return;
            }
            string estimate = "正在计算剩余时间";
            double elapsedSeconds = (DateTime.UtcNow - _fileOperationStartedAtUtc).TotalSeconds;
            if (progress.ProcessedBytes > 0 && elapsedSeconds > 0 && progress.ProcessedBytes < progress.TotalBytes)
            {
                double remainingSeconds = elapsedSeconds * (progress.TotalBytes - progress.ProcessedBytes) / progress.ProcessedBytes;
                estimate = "预计剩余：" + FormatMigrationDuration(remainingSeconds);
            }
            else if (progress.ProcessedBytes >= progress.TotalBytes)
            {
                estimate = "即将完成";
            }
            string failure = progress.FailedFiles > 0 ? "，失败 " + progress.FailedFiles + " 个" : "";
            FileOperationProgressText = "已复制 " + RuntimeDataManager.FormatSize(progress.ProcessedBytes)
                + " / " + RuntimeDataManager.FormatSize(progress.TotalBytes)
                + "（" + progress.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%） · " + estimate + failure;
        }

        private static string FormatMigrationDuration(double seconds)
        {
            if (seconds < 1) return "不到 1 秒";
            TimeSpan duration = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            if (duration.TotalMinutes < 1) return ((int)Math.Max(1, duration.TotalSeconds)) + " 秒";
            if (duration.TotalHours < 1) return ((int)duration.TotalMinutes) + " 分 " + duration.Seconds + " 秒";
            return ((int)duration.TotalHours) + " 小时 " + duration.Minutes + " 分";
        }

        public async Task<bool> ChangeDataDirectoryAsync(string directory)
        {
            if (IsCapturing) { Status = "请先停止采集再设置数据目录"; return false; }
            if (IsFileOperationInProgress) return false;
            if (!BeginFileOperation("正在迁移数据目录，请稍候...", "正在移动日志、截图和现场缓存，请勿关闭软件")) return false;
            try
            {
                string previous = RuntimeTools.DataDirectory;
                Progress<RuntimeDataMigrationProgress> progress = new Progress<RuntimeDataMigrationProgress>(SetDataMigrationProgress);
                string target = await RuntimeTools.ChangeUserDataDirectoryAsync(directory, CancellationToken.None, progress);
                _screenshots.SetRoot(Path.Combine(target, "screenshots"));
                foreach (ScreenshotItemViewModel item in _screenshotItems) item.RebasePath(previous, target);
                OnPropertyChanged(nameof(DataDirectory));
                Status = "数据目录已设置为：" + target;
                return true;
            }
            catch (Exception exception)
            {
                Status = "设置数据目录失败：" + exception.Message;
                return false;
            }
            finally
            {
                EndFileOperation();
            }
        }
        public void EndFileOperation()
        {
            if (!IsFileOperationInProgress) return;
            IsFileOperationInProgress = false;
            OnPropertyChanged(nameof(IsFileOperationInProgress));
            OnPropertyChanged(nameof(IsSessionLoading));
            OnPropertyChanged(nameof(CanUseFileActions));
            OnPropertyChanged(nameof(CanChangeDevice));
            OnPropertyChanged(nameof(CanToggleCapture));
            IsFileOperationIndeterminate = true;
            IsFileOperationProgressVisible = false;
            FileOperationProgress = 0;
            FileOperationProgressText = "";
        }
        public bool BeginSessionLoading()
        {
            return BeginFileOperation("正在打开现场文件，请稍候...", "正在读取曲线、截图与设备信息，请勿关闭软件");
        }
        public void EndSessionLoading()
        {
            EndFileOperation();
        }
        public string CaptureText { get { return IsCapturing ? "■  停止采集" : "▶  开始采集"; } }
        public string CaptureLabel { get { return IsCapturing ? "停止采集" : "开始采集"; } }
        public bool ShowFrameMetrics { get { return IsMetricEnabled("FPS"); } }
        public bool ShowFps { get { return IsMetricEnabled("FPS"); } }
        public bool ShowJank { get { return IsMetricEnabled("FPS"); } }
        public bool ShowBigJank { get { return IsMetricEnabled("FPS"); } }
        public bool ShowFrameTime { get { return IsMetricEnabled("Display FrameTime"); } }
        public bool ShowMemory { get { return IsMetricEnabled("Process Memory"); } }
        public bool ShowProcessCpu { get { return IsMetricEnabled("Process CPU Raw"); } }
        public bool ShowNormalizedCpu { get { return !IsIosSelection && IsMetricEnabled("Process CPU Raw"); } }
        public bool ShowCoreCpu { get { return !IsIosSelection && IsMetricEnabled("Process CPU Raw"); } }
        public bool ShowTemperature { get { return IsMetricEnabled("Device Temperature"); } }
        public bool ShowThermalState { get { return IsMetricEnabled("Thermal State"); } }
        public bool HasSessionData { get { return _sampleBuffer.Count > 0 || Screenshots.Count > 0; } }
        public IReadOnlyList<PerfSample> Samples
        {
            get { return _samples; }
            private set { _samples = value ?? Array.Empty<PerfSample>(); OnPropertyChanged(); }
        }
        public double? SelectedTime
        {
            get { return _selectedTime; }
            set
            {
                if (_selectedTime == value) return;
                _selectedTime = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TimelineLabel));
                SelectNearestScreenshot();
                UpdateSelectedMetricValues();
            }
        }
        public double ViewStartTime
        {
            get { return _viewStartTime; }
            private set
            {
                if (Math.Abs(_viewStartTime - value) < 0.0001) return;
                _viewStartTime = Math.Max(0, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsChartZoomed));
                OnPropertyChanged(nameof(ShowChartZoomReset));
                UpdateAnalysisData();
            }
        }
        public double ViewEndTime
        {
            get { return _viewEndTime; }
            private set
            {
                if (Math.Abs(_viewEndTime - value) < 0.0001) return;
                _viewEndTime = Math.Max(0, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsChartZoomed));
                OnPropertyChanged(nameof(ShowChartZoomReset));
                UpdateAnalysisData();
            }
        }
        public bool IsChartZoomEnabled
        {
            get { return _chartZoomEnabled; }
            set
            {
                if (_chartZoomEnabled == value) return;
                _chartZoomEnabled = value;
                OnPropertyChanged();
                if (!value) ResetChartZoom();
            }
        }
        public bool IsChartZoomed
        {
            get { return ViewEndTime > ViewStartTime + 0.01; }
        }
        public bool ShowChartZoomReset
        {
            get { return IsChartZoomed; }
        }
        public bool FollowLatest
        {
            get { return _followLatest; }
            private set
            {
                if (_followLatest == value) return;
                _followLatest = value;
                OnPropertyChanged();
            }
        }
        private IReadOnlyList<ScreenshotItemViewModel> _screenshotProjection = Array.Empty<ScreenshotItemViewModel>();
        public IReadOnlyList<ScreenshotItemViewModel> Screenshots
        {
            get { return _screenshotProjection; }
            private set { _screenshotProjection = value ?? Array.Empty<ScreenshotItemViewModel>(); OnPropertyChanged(); }
        }
        public bool HasScreenshots { get { return Screenshots.Count > 0; } }
        public string LatestScreenshotLabel
        {
            get { return Screenshots.Count == 0 ? "最新：--" : "最新：" + Screenshots[Screenshots.Count - 1].TimeLabel; }
        }
        public ScreenshotItemViewModel SelectedScreenshot
        {
            get { return _selectedScreenshot; }
            set
            {
                if (ReferenceEquals(_selectedScreenshot, value)) return;
                _selectedScreenshot = value;
                OnPropertyChanged();
                if (value != null && (!_selectedTime.HasValue || Math.Abs(_selectedTime.Value - value.ElapsedSec) > 0.001))
                {
                    _selectedTime = value.ElapsedSec;
                    OnPropertyChanged(nameof(SelectedTime));
                    OnPropertyChanged(nameof(TimelineLabel));
                    UpdateSelectedMetricValues();
                }
            }
        }
        public ObservableCollection<MetricRowViewModel> Metrics { get; private set; }
        public ICommand ToggleCaptureCommand { get; private set; }

        public void ApplySelection(DeviceSelection selection)
        {
            if (IsCapturing || selection == null || selection.Device == null) return;
            if (!ProcessTargetMatcher.IsValidTarget(selection.Process))
            {
                Status = "当前进程 PID 无效，请刷新进程列表后重新选择。";
                return;
            }
            _selection = selection;
            _selectionNeedsRefresh = false;
            Status = "已选择 " + selection.Device.PickerLabel + " / pid " + selection.Process.Pid;
            OnPropertyChanged(nameof(CurrentSelection));
            OnPropertyChanged(nameof(DeviceSummary));
            OnPropertyChanged(nameof(ProcessSummary));
            OnPropertyChanged(nameof(TargetSummary));
            OnPropertyChanged(nameof(DeviceInfoText));
            OnPropertyChanged(nameof(ShowNormalizedCpu));
            OnPropertyChanged(nameof(ShowCoreCpu));
            UpdateThermalMetricPresentation();
        }

        public bool IsAndroidSelection
        {
            get { return _selection != null && _selection.Device != null && DeviceLookupService.IsAndroid(_selection.Device); }
        }

        private bool IsIosSelection
        {
            get { return _selection != null && _selection.Device != null && !DeviceLookupService.IsAndroid(_selection.Device); }
        }

        private void UpdateThermalMetricPresentation()
        {
            MetricRowViewModel thermal = Metrics.First(delegate(MetricRowViewModel metric) { return metric.Name == "Thermal State"; });
            thermal.SetPresentation(
                ThermalMetricTitle,
                "系统热状态",
                IsAndroidSelection ? "0-6" : "0-3");
            OnPropertyChanged(nameof(ThermalMetricTitle));
            OnPropertyChanged(nameof(ThermalMetricLegend));
            OnPropertyChanged(nameof(ThermalAxisMax));
            OnPropertyChanged(nameof(ThermalWaitingText));
        }

        public void ToggleDeviceInfo()
        {
            IsDeviceInfoVisible = !IsDeviceInfoVisible;
            OnPropertyChanged(nameof(DeviceInfoText));
        }

        public void ToggleCapture()
        {
            if (IsCapturing)
            {
                StopCapture("已停止采集", "user_requested");
                return;
            }
            ProcessInfo process = _selection == null ? null : _selection.Process;
            int targetPid = process == null ? 0 : process.Pid;
            if (CaptureSelectionNeedsPicker || targetPid <= 0)
            {
                Status = _selectionNeedsRefresh
                    ? "现场文件中的设备和进程仅用于查看，请重新选择当前设备、应用和进程"
                    : "请先选择设备、应用和有效进程";
                return;
            }

            ResetMetricValues();
            ClearScreenshots();
            _sampleBuffer = new List<PerfSample>();
            Samples = Array.Empty<PerfSample>();
            SelectedTime = null;
            ViewStartTime = 0;
            ViewEndTime = 0;
            FollowLatest = true;
            ShowLiveDataTab();
            _sessionStartedAt = DateTime.Now;
            AppInfo app = _selection.App;
            DeviceInfo device = _selection.Device;
            CaptureConfig config = new CaptureConfig
            {
                Platform = device.Platform,
                Udid = device.Udid,
                ProductVersion = device.ProductVersion,
                BundleId = app == null ? process.BundleId : app.BundleId,
                TargetPid = targetPid,
                TargetName = process.Name,
                TargetStartAbsTime = process.StartAbsTime,
                TargetAndroidStartTimeTicks = process.AndroidStartTimeTicks,
                TargetCoalitionId = process.CoalitionId,
                TargetOwnerPid = process.OwnerPid,
                TargetOwnerName = process.OwnerName,
                CollectFps = true,
                CollectMemory = true,
                CollectCpu = true,
                CollectTemperature = true,
                CollectThermalState = true,
                CaptureScreenshots = IsMetricEnabled("Screenshot"),
                ScreenshotIntervalSec = 3
            };
            try
            {
                _collector.Dispose();
                _screenshots.Stop();
                _collector = new PerfCollector();
                _screenshots = new ScreenshotService(Path.Combine(RuntimeTools.DataDirectory, "screenshots"));
                _uiGeneration = _captureUi.Begin();
                long generation = _uiGeneration;
                BindCaptureCallbacks(generation, _collector, _screenshots);
                _captureCancellation = new CancellationTokenSource();
                CancellationToken captureToken = _captureCancellation.Token;
                _screenshots.Udid = config.Udid;
                _screenshots.Platform = config.Platform;
                _screenshots.ProductVersion = config.ProductVersion;
                _screenshots.Enabled = config.CaptureScreenshots;
                _screenshots.IntervalSec = 3;
                _screenshots.Reset(_captureCancellation.Token);
                _collector.Start(config);
                IsCapturing = true;
                _sampleRefreshTimer.Start();
                OnPropertyChanged(nameof(TargetSummary));
                _ = MonitorDeviceConnectionAsync(config, captureToken, generation, _collector);
            }
            catch (Exception ex)
            {
                StopCapture("采集启动失败：" + ex.Message + AddDiagnosticsLogHint(""), "start_failed");
            }
        }
        public void StopCapture(string status, string reason = "stop_requested")
        {
            _collector.Stop(reason);
            _sampleRefreshTimer.Stop();
            ApplySamples(_captureUi.Drain(_uiGeneration, int.MaxValue, close: true));
            _screenshots.Stop();
            CancelCaptureToken();
            IsCapturing = false;
            ShowAnalysisDataTab();
            Status = status;
            OnPropertyChanged(nameof(TargetSummary));
        }

        internal void ApplySample(PerfSample sample)
        {
            if (sample != null) ApplySamples(new[] { sample });
        }

        internal void ApplySamples(IReadOnlyList<PerfSample> batch)
        {
            if (batch.Count == 0) return;
            foreach (PerfSample sample in batch)
            {
                if (sample == null) continue;
                _sampleBuffer.Add(sample);
                if (sample.HasFps) SetMetric("FPS", sample.Fps, "0.##");
                if (sample.HasFrameTimeMax) SetMetric("Display FrameTime", sample.FrameTimeMaxMs, "0.##");
                if (sample.HasJank)
                {
                    SetMetric("Jank", sample.Jank, "0.##");
                    SetMetric("BigJank", sample.BigJank, "0.##");
                }
                if (sample.HasMemory) SetMetric("Process Memory", sample.MemoryMb, "0.##");
                if (sample.HasCpu) SetMetric("Process CPU Raw", sample.CpuPercent, "0.##");
                if (sample.HasCpuNormalized) SetMetric("Process CPU Normalized", sample.CpuNormalizedPercent, "0.##");
                if (sample.HasTemperature && sample.TemperatureCelsius.Count > 0) SetMetric("Device Temperature", sample.TemperatureCelsius.Values.Max(), "0.##");
                if (sample.HasThermalState) SetMetric("Thermal State", sample.ThermalStateLevel, "0");
            }
            Samples = new SampleSnapshot(_sampleBuffer, Samples as SampleSnapshot);
            OnPropertyChanged(nameof(HasSessionData));
            TrimStaleLatestMetricValues();
            UpdateDataPanels();
            UpdateAnalysisData();
        }

        private void SetMetric(string name, double value, string format)
        {
            MetricRowViewModel metric = Metrics.FirstOrDefault(delegate(MetricRowViewModel item) { return item.Name == name; });
            if (metric != null) metric.Value = value.ToString(format, CultureInfo.InvariantCulture);
        }
        private void MetricChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MetricRowViewModel.IsEnabled)) return;
            OnPropertyChanged(nameof(ShowFrameMetrics));
            OnPropertyChanged(nameof(ShowFps));
            OnPropertyChanged(nameof(ShowJank));
            OnPropertyChanged(nameof(ShowBigJank));
            OnPropertyChanged(nameof(ShowFrameTime));
            OnPropertyChanged(nameof(ShowMemory));
            OnPropertyChanged(nameof(ShowProcessCpu));
            OnPropertyChanged(nameof(ShowNormalizedCpu));
            OnPropertyChanged(nameof(ShowCoreCpu));
            OnPropertyChanged(nameof(ShowTemperature));
            OnPropertyChanged(nameof(ShowThermalState));
        }
        private bool IsMetricEnabled(string name)
        {
            MetricRowViewModel metric = Metrics.FirstOrDefault(delegate(MetricRowViewModel item) { return string.Equals(item.Name, name, StringComparison.Ordinal); });
            return metric != null && metric.IsEnabled;
        }
        public void SelectTimeFromChart(double elapsedSeconds)
        {
            FollowLatest = false;
            SelectedTime = elapsedSeconds;
        }

        public void ApplyChartZoom(double startTime, double endTime)
        {
            if (!IsChartZoomEnabled || _sampleBuffer.Count == 0) return;
            double maxTime = _sampleBuffer.Max(delegate(PerfSample sample) { return sample == null ? 0 : sample.ElapsedSec; });
            if (maxTime <= 0) return;
            double start = Math.Max(0, Math.Min(maxTime, Math.Min(startTime, endTime)));
            double end = Math.Max(0, Math.Min(maxTime, Math.Max(startTime, endTime)));
            if (end <= start + 0.01) return;
            if (start <= 0.01 && end >= maxTime - 0.01)
            {
                ResetChartZoom();
                return;
            }

            double minimumWindow = Math.Min(60, maxTime);
            if (end - start < minimumWindow)
            {
                double center = (start + end) / 2.0;
                start = center - minimumWindow / 2.0;
                end = center + minimumWindow / 2.0;
                if (start < 0)
                {
                    end -= start;
                    start = 0;
                }
                if (end > maxTime)
                {
                    start = Math.Max(0, start - (end - maxTime));
                    end = maxTime;
                }
            }
            ViewStartTime = start;
            ViewEndTime = end;
        }

        public void ResetChartZoom()
        {
            ViewStartTime = 0;
            ViewEndTime = 0;
        }
        public void SelectScreenshot(ScreenshotItemViewModel screenshot)
        {
            if (screenshot == null) return;
            FollowLatest = false;
            SelectedScreenshot = screenshot;
        }
        public void ShowLiveDataTab()
        {
            SetDataPanelTab(DataPanelTab.Live);
        }
        public void ShowSelectedDataTab()
        {
            SetDataPanelTab(DataPanelTab.Selected);
        }
        public void ShowAnalysisDataTab()
        {
            SetDataPanelTab(DataPanelTab.Analysis);
        }
        private void SetDataPanelTab(DataPanelTab tab)
        {
            if (_dataPanelTab == tab) return;
            _dataPanelTab = tab;
            OnPropertyChanged(nameof(ShowLiveData));
            OnPropertyChanged(nameof(ShowSelectedData));
            OnPropertyChanged(nameof(ShowAnalysisData));
        }
        private void ResetMetricValues()
        {
            foreach (MetricRowViewModel metric in Metrics)
            {
                metric.Value = "--";
                metric.SelectedValue = "--";
            }
            ResetDataTiles(_liveDataTiles);
            ResetDataTiles(_selectedDataTiles);
            UpdateAnalysisData();
        }
        private void UpdateSelectedMetricValues()
        {
            UpdateSelectedMetricValues(null, false);
        }
        private void UpdateSelectedMetricValues(PerfSample selectedSample, bool useProvidedSample)
        {
            PerfSample selected = selectedSample;
            if (!useProvidedSample && _selectedTime.HasValue && _sampleBuffer.Count > 0)
            {
                selected = FindNearestSample(Samples, _selectedTime);
            }
            foreach (MetricRowViewModel metric in Metrics) metric.SelectedValue = "--";
            if (selected != null)
            {
                SetSelectedMetric("FPS", selected.HasFps, selected.Fps, "0.##");
                SetSelectedMetric("Display FrameTime", selected.HasFrameTimeMax, selected.FrameTimeMaxMs, "0.##");
                SetSelectedMetric("Jank", selected.HasJank, selected.Jank, "0.##");
                SetSelectedMetric("BigJank", selected.HasJank, selected.BigJank, "0.##");
                PerfSample memory = NearestMetricSample(selected, delegate(PerfSample item) { return item.HasMemory; }, ProcessMetricDisplayFreshSeconds);
                PerfSample cpu = NearestMetricSample(selected, delegate(PerfSample item) { return item.HasCpu; }, ProcessMetricDisplayFreshSeconds);
                PerfSample normalizedCpu = NearestMetricSample(selected, delegate(PerfSample item) { return item.HasCpuNormalized; }, ProcessMetricDisplayFreshSeconds);
                PerfSample temperature = NearestMetricSample(selected, delegate(PerfSample item) { return HasTemperatureValues(item); }, TemperatureDisplayFreshSeconds);
                PerfSample thermal = NearestMetricSample(selected, delegate(PerfSample item) { return HasThermalStateValue(item); }, ThermalStateDisplayFreshSeconds());
                SetSelectedMetric("Process Memory", memory != null && memory.HasMemory, memory == null ? 0 : memory.MemoryMb, "0.##");
                SetSelectedMetric("Process CPU Raw", cpu != null && cpu.HasCpu, cpu == null ? 0 : cpu.CpuPercent, "0.##");
                SetSelectedMetric("Process CPU Normalized", normalizedCpu != null && normalizedCpu.HasCpuNormalized, normalizedCpu == null ? 0 : normalizedCpu.CpuNormalizedPercent, "0.##");
                SetSelectedMetric("Thermal State", thermal != null && HasThermalStateValue(thermal), thermal == null ? 0 : thermal.ThermalStateLevel, "0");
                SetSelectedMetric("Device Temperature", temperature != null && HasTemperatureValues(temperature), temperature == null ? 0 : temperature.TemperatureCelsius.Values.Max(), "0.##");
            }
            UpdateDataPanels();
        }

        private static ObservableCollection<DataMetricTileViewModel> CreateDataTiles(Dictionary<string, DataMetricTileViewModel> index)
        {
            ObservableCollection<DataMetricTileViewModel> tiles = new ObservableCollection<DataMetricTileViewModel>();
            AddDataTile(tiles, index, "pid", "PID");
            AddDataTile(tiles, index, "elapsed", "时长");
            AddDataTile(tiles, index, "cpu", "CPU Raw");
            AddDataTile(tiles, index, "memory", "内存");
            AddDataTile(tiles, index, "fps", "FPS");
            AddDataTile(tiles, index, "frame", "FrameTime");
            AddDataTile(tiles, index, "jank", "Jank");
            AddDataTile(tiles, index, "bigJank", "BigJank");
            AddDataTile(tiles, index, "temperature", "Temperature");
            AddDataTile(tiles, index, "thermalState", "Thermal State");
            return tiles;
        }

        private static void AddDataTile(ObservableCollection<DataMetricTileViewModel> tiles, Dictionary<string, DataMetricTileViewModel> index, string key, string label)
        {
            DataMetricTileViewModel tile = new DataMetricTileViewModel(label);
            tiles.Add(tile);
            index[key] = tile;
        }

        private static void ResetDataTiles(Dictionary<string, DataMetricTileViewModel> tiles)
        {
            foreach (DataMetricTileViewModel tile in tiles.Values) tile.Value = "--";
        }

        private void UpdateDataPanels()
        {
            PerfSample latest = _sampleBuffer.Count == 0 ? null : _sampleBuffer[_sampleBuffer.Count - 1];
            PerfSample selected = null;
            if (_selectedTime.HasValue && _sampleBuffer.Count > 0)
            {
                selected = FindNearestSample(Samples, _selectedTime);
            }
            UpdateDataPanel(_liveDataTiles, latest);
            UpdateDataPanel(_selectedDataTiles, selected);
        }

        private void UpdateAnalysisData()
        {
            IReadOnlyList<PerfSample> ordered = (Samples as SampleSnapshot)?.Ordered ?? Samples;
            double maxTime = ordered.Count == 0 ? 0 : ordered.Max(delegate(PerfSample sample) { return sample == null ? 0 : sample.ElapsedSec; });
            double start = ViewEndTime > ViewStartTime + 0.01 ? ViewStartTime : 0;
            double end = ViewEndTime > ViewStartTime + 0.01 ? ViewEndTime : maxTime;
            IReadOnlyList<PerfSample> visible = ordered
                .Where(delegate(PerfSample sample) { return sample != null && sample.ElapsedSec >= start && sample.ElapsedSec <= end; })
                .ToList();

            List<AnalysisMetricRowViewModel> rows = new List<AnalysisMetricRowViewModel>();
            AddAnalysisRow(rows, "FPS", "帧/s", visible.Where(delegate(PerfSample sample) { return sample.HasFps; }).Select(delegate(PerfSample sample) { return sample.Fps; }));
            AddAnalysisRow(rows, "FrameTime", "ms", visible.Where(FrameTimeChartProjection.IsChartSample).Select(delegate(PerfSample sample) { return sample.FrameTimeMaxMs; }));
            AddAnalysisRow(rows, "Jank", "次", visible.Where(delegate(PerfSample sample) { return sample.HasJank; }).Select(delegate(PerfSample sample) { return sample.Jank; }));
            AddAnalysisRow(rows, "BigJank", "次", visible.Where(delegate(PerfSample sample) { return sample.HasJank; }).Select(delegate(PerfSample sample) { return sample.BigJank; }));

            string preferredMemory = MetricStatistics.PreferredMemoryMetric(visible);
            AddAnalysisRow(rows, "Process Memory", "MB", visible.Where(delegate(PerfSample sample)
            {
                return sample.HasMemory && sample.MemoryMb > 0 && MemoryMetricMatches(sample.MemoryMetric, preferredMemory);
            }).Select(delegate(PerfSample sample) { return sample.MemoryMb; }));
            AddAnalysisRow(rows, "CPU Raw", "%", visible.Where(delegate(PerfSample sample) { return sample.HasCpu; }).Select(delegate(PerfSample sample) { return sample.CpuPercent; }));
            AddAnalysisRow(rows, "CPU Normalized", "%", visible.Where(delegate(PerfSample sample) { return sample.HasCpuNormalized; }).Select(delegate(PerfSample sample) { return sample.CpuNormalizedPercent; }));

            int coreCount = ordered.Where(delegate(PerfSample sample) { return sample.HasCpuCoreUsage; }).Select(delegate(PerfSample sample) { return sample.CpuCoreCount; }).DefaultIfEmpty(0).Max();
            for (int core = 0; core < coreCount; core++)
            {
                int index = core;
                AddAnalysisRow(rows, "CPU " + core.ToString(CultureInfo.InvariantCulture), "%", visible.Where(delegate(PerfSample sample)
                {
                    return sample.HasCpuCoreUsage && sample.CpuCorePercents != null && sample.CpuCorePercents.Count > index;
                }).Select(delegate(PerfSample sample) { return sample.CpuCorePercents[index]; }));
            }

            foreach (string sensor in PerformanceChartControl.TemperatureSensorNames(ordered))
            {
                string currentSensor = sensor;
                AddAnalysisRow(rows, "Temperature / " + sensor, "°C", visible.Where(delegate(PerfSample sample)
                {
                    double value;
                    return sample.HasTemperature && sample.TemperatureCelsius != null && sample.TemperatureCelsius.TryGetValue(currentSensor, out value)
                        && IsFinite(value) && value >= -20 && value <= 120;
                }).Select(delegate(PerfSample sample) { return sample.TemperatureCelsius[currentSensor]; }));
            }
            AddAnalysisRow(rows, ThermalMetricTitle, "级别", visible.Where(HasThermalStateValue).Select(delegate(PerfSample sample) { return (double)sample.ThermalStateLevel; }));

            AnalysisDataRows.Clear();
            foreach (AnalysisMetricRowViewModel row in rows) AnalysisDataRows.Add(row);
        }

        private static void AddAnalysisRow(List<AnalysisMetricRowViewModel> rows, string label, string unit, IEnumerable<double> values)
        {
            double[] valid = (values ?? Enumerable.Empty<double>()).Where(IsFinite).ToArray();
            rows.Add(new AnalysisMetricRowViewModel(label, unit, valid));
        }

        private static bool MemoryMetricMatches(string sampleMetric, string preferredMetric)
        {
            string sample = (sampleMetric ?? "").Trim().ToLowerInvariant();
            string preferred = (preferredMetric ?? "").Trim().ToLowerInvariant();
            if (sample == "footprint") sample = "physical_footprint";
            if (preferred == "footprint") preferred = "physical_footprint";
            return sample == preferred;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private void UpdateDataPanel(Dictionary<string, DataMetricTileViewModel> tiles, PerfSample sample)
        {
            if (sample == null)
            {
                ResetDataTiles(tiles);
                return;
            }
            PerfSample memory = NearestMetricSample(sample, delegate(PerfSample item) { return item.HasMemory; }, ProcessMetricDisplayFreshSeconds);
            PerfSample temperature = NearestMetricSample(sample, HasTemperatureValues, TemperatureDisplayFreshSeconds);
            PerfSample thermal = NearestMetricSample(sample, HasThermalStateValue, ThermalStateDisplayFreshSeconds());
            int targetPid = sample.TargetPid > 0
                ? sample.TargetPid
                : _selection != null && _selection.Process != null ? _selection.Process.Pid : 0;
            SetDataValue(tiles, "pid", targetPid <= 0 ? "--" : targetPid.ToString(CultureInfo.InvariantCulture));
            SetDataValue(tiles, "elapsed", FormatElapsed(sample.ElapsedSec));
            SetDataValue(tiles, "cpu", !sample.HasCpu ? "--" : sample.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%");
            SetDataValue(tiles, "memory", memory == null ? "--" : memory.MemoryMb.ToString("0", CultureInfo.InvariantCulture) + " MB");
            SetDataValue(tiles, "fps", !sample.HasFps ? "--" : sample.Fps.ToString("0.0", CultureInfo.InvariantCulture));
            SetDataValue(tiles, "frame", !sample.HasFrameTimeMax ? "--" : sample.FrameTimeMaxMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            SetDataValue(tiles, "jank", !sample.HasJank ? "--" : sample.Jank.ToString(CultureInfo.InvariantCulture));
            SetDataValue(tiles, "bigJank", !sample.HasJank ? "--" : sample.BigJank.ToString(CultureInfo.InvariantCulture));
            SetDataValue(tiles, "temperature", TemperatureSummary(temperature));
            SetDataValue(tiles, "thermalState", ThermalStateSummary(thermal));
        }

        private PerfSample NearestMetricSample(PerfSample reference, Func<PerfSample, bool> predicate, double maximumDistanceSeconds)
        {
            if (reference == null) return _sampleBuffer.LastOrDefault(predicate);
            IReadOnlyList<PerfSample> ordered = (Samples as SampleSnapshot)?.Ordered ?? Samples;
            return SampleSnapshot.Nearest(ordered, reference.ElapsedSec, predicate, maximumDistanceSeconds);
        }

        private double ThermalStateDisplayFreshSeconds()
        {
            return IsAndroidSelection ? AndroidThermalStateDisplayFreshSeconds : IosThermalStateDisplayFreshSeconds;
        }

        private static bool HasTemperatureValues(PerfSample sample)
        {
            return sample != null
                && sample.HasTemperature
                && sample.TemperatureCelsius != null
                && sample.TemperatureCelsius.Count > 0;
        }

        private static bool HasThermalStateValue(PerfSample sample)
        {
            return sample != null
                && sample.HasThermalState
                && sample.ThermalStateLevel >= 0
                && sample.ThermalStateLevel <= 6;
        }

        private static string TemperatureSummary(PerfSample sample)
        {
            if (sample == null || sample.TemperatureCelsius == null || sample.TemperatureCelsius.Count == 0) return "--";
            KeyValuePair<string, double> hottest = sample.TemperatureCelsius.OrderByDescending(delegate(KeyValuePair<string, double> pair) { return pair.Value; }).First();
            return hottest.Key + " " + hottest.Value.ToString("0.0", CultureInfo.InvariantCulture) + "°C";
        }

        private static string ThermalStateSummary(PerfSample sample)
        {
            if (sample == null || !sample.HasThermalState) return "--";
            string name = (sample.ThermalStateName ?? "").Trim().ToLowerInvariant();
            string label = name == "nominal" || name == "none" ? "正常"
                : name == "fair" ? "升温"
                : name == "light" ? "轻微"
                : name == "moderate" ? "中度"
                : name == "serious" || name == "severe" ? "严重"
                : name == "critical" ? "临界"
                : name == "emergency" ? "紧急"
                : name == "shutdown" ? "关机"
                : "未知";
            return sample.ThermalStateLevel.ToString(CultureInfo.InvariantCulture) + " " + label;
        }

        private static string FormatElapsed(double seconds)
        {
            TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return span.TotalMinutes >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)span.TotalMinutes, span.Seconds)
                : seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        private static void SetDataValue(Dictionary<string, DataMetricTileViewModel> tiles, string key, string value)
        {
            DataMetricTileViewModel tile;
            if (tiles.TryGetValue(key, out tile)) tile.Value = value;
        }
        private void SetSelectedMetric(string name, bool available, double value, string format)
        {
            if (!available) return;
            MetricRowViewModel metric = Metrics.FirstOrDefault(delegate(MetricRowViewModel item) { return item.Name == name; });
            if (metric != null) metric.SelectedValue = value.ToString(format, CultureInfo.InvariantCulture);
        }
        private void PostStatus(string message) { Dispatcher.UIThread.Post(delegate { Status = message; }); }
        private void HandleScreenshot(ScreenshotInfo screenshot, long generation)
        {
            if (screenshot == null || string.IsNullOrWhiteSpace(screenshot.Path)) return;
            Dispatcher.UIThread.Post(delegate
            {
                if (!_captureUi.IsCurrent(generation)) return;
                try
                {
                    ScreenshotItemViewModel item = new ScreenshotItemViewModel(screenshot, false);
                    _screenshotItems.Add(item);
                    Screenshots = _screenshotItems.ToArray();
                    OnPropertyChanged(nameof(HasScreenshots));
                    OnPropertyChanged(nameof(LatestScreenshotLabel));
                    OnPropertyChanged(nameof(HasSessionData));
                    _ = item.LoadImageAsync();
                }
                catch (Exception ex)
                {
                    Status = "截图读取失败：" + ex.Message;
                }
            });
        }
        private void SelectNearestScreenshot()
        {
            if (!_selectedTime.HasValue || Screenshots.Count == 0) return;
            ScreenshotItemViewModel nearest = Screenshots.OrderBy(delegate(ScreenshotItemViewModel item) { return Math.Abs(item.ElapsedSec - _selectedTime.Value); }).FirstOrDefault();
            if (!ReferenceEquals(_selectedScreenshot, nearest))
            {
                _selectedScreenshot = nearest;
                OnPropertyChanged(nameof(SelectedScreenshot));
            }
        }
        private void ClearScreenshots()
        {
            foreach (ScreenshotItemViewModel item in _screenshotItems) item.Dispose();
            _screenshotItems.Clear();
            Screenshots = Array.Empty<ScreenshotItemViewModel>();
            OnPropertyChanged(nameof(HasScreenshots));
            OnPropertyChanged(nameof(LatestScreenshotLabel));
            OnPropertyChanged(nameof(HasSessionData));
            _selectedScreenshot = null;
            OnPropertyChanged(nameof(SelectedScreenshot));
        }
        private void CancelCaptureToken()
        {
            CancellationTokenSource token = _captureCancellation;
            _captureCancellation = null;
            if (token == null) return;
            token.Cancel();
            token.Dispose();
        }
        private async Task MonitorDeviceConnectionAsync(CaptureConfig config, CancellationToken token, long generation, PerfCollector collector)
        {
            int offlineCount = 0;
            bool? previousOnline = true;
            collector.RecordEvent("device_monitor_started", "设备连接监测已启动", "started");
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(2000, token); }
                catch (OperationCanceledException) { return; }
                bool? online;
                try { online = await _deviceLookup.IsDeviceOnlineAsync(config.Udid, config.Platform, token); }
                catch (OperationCanceledException) { return; }
                catch { online = null; }
                if (online != previousOnline)
                    collector.RecordEvent("device_connection_changed", online.HasValue ? (online.Value ? "设备在线" : "设备离线") : "设备探测失败，连接状态未知", online.HasValue ? (online.Value ? "online" : "offline") : "unknown");
                previousOnline = online;
                if (!online.HasValue) { offlineCount = 0; continue; }
                offlineCount = online.Value ? 0 : offlineCount + 1;
                if (offlineCount < 2) continue;
                Dispatcher.UIThread.Post(delegate
                {
                    if (!IsCapturing || !_captureUi.IsCurrent(generation)) return;
                    string message = AddDiagnosticsLogHint("检测到设备已断开，采集已停止");
                    StopCapture(message, "device_disconnected");
                    CaptureStoppedUnexpectedly?.Invoke(message);
                });
                return;
            }
        }
        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values) if (!string.IsNullOrWhiteSpace(value)) return value;
            return "";
        }
        private string AddDiagnosticsLogHint(string message)
        {
            string value = message ?? "";
            string path = _collector.LastDiagnosticsLogPath;
            return string.IsNullOrWhiteSpace(path) || value.IndexOf("详细日志：", StringComparison.Ordinal) >= 0
                ? value
                : value + "；详细日志：" + path;
        }
        private static string DevicePlatformName(DeviceInfo device)
        {
            if (device == null) return "-";
            return DeviceLookupService.IsAndroid(device) ? "Android" : "iOS";
        }
        private static string DeviceOsText(DeviceInfo device)
        {
            if (device == null) return "-";
            string platform = DevicePlatformName(device);
            return string.IsNullOrWhiteSpace(device.ProductVersion) ? platform : platform + " " + device.ProductVersion;
        }
        private static void AppendDeviceDetail(StringBuilder text, string label, string value)
        {
            text.Append(label).Append('：').Append(string.IsNullOrWhiteSpace(value) ? "-" : value.Trim()).Append('\n');
        }
        public SessionDocument BuildSessionDocument()
        {
            SessionDocument document = new SessionDocument
            {
                Format = "motuperf-session",
                Version = 5,
                AppVersion = Version.TrimStart('v'),
                SavedAt = DateTime.Now,
                StartedAt = _sessionStartedAt == default && _sampleBuffer.Count > 0 ? _sampleBuffer[0].Timestamp : _sessionStartedAt,
                Device = _selection?.Device,
                App = _selection?.App,
                Process = _selection?.Process,
                SelectedBundleId = _selection?.App?.BundleId ?? _selection?.Process?.BundleId ?? "",
                SelectedTime = SelectedTime,
                FollowLatest = FollowLatest,
                ViewStartTime = 0,
                ViewEndTime = 0,
                CaptureScreenshots = IsMetricEnabled("Screenshot"),
                CaptureTemperature = true,
                CaptureThermalState = true,
                Samples = _sampleBuffer.ToList()
            };
            for (int index = 0; index < Screenshots.Count; index++)
            {
                ScreenshotItemViewModel item = Screenshots[index];
                document.Screenshots.Add(new SessionScreenshot
                {
                    Timestamp = item.Screenshot.Timestamp,
                    ElapsedSec = item.ElapsedSec,
                    OriginalPath = item.Path,
                    ArchivePath = "screenshots/" + index.ToString("00000", CultureInfo.InvariantCulture) + "-" + Path.GetFileName(item.Path),
                    Orientation = item.Screenshot.Orientation
                });
            }
            return document;
        }
        public void ApplySessionDocument(SessionDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            PreparedSessionData prepared = PrepareSessionData(document);
            ApplySessionDocumentCore(document, prepared, true, true);
        }
        public async Task ApplySessionDocumentAsync(SessionDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            PreparedSessionData prepared = await Task.Run(delegate { return PrepareSessionData(document); });
            ApplySessionDocumentCore(document, prepared, false, false);
            await AddScreenshotItemsAsync(prepared.Screenshots);
            CompleteScreenshotProjection();
            _ = LoadScreenshotImagesAsync();
        }
        private static PreparedSessionData PrepareSessionData(SessionDocument document)
        {
            List<PerfSample> samples = (document.Samples ?? new List<PerfSample>())
                .Where(delegate(PerfSample sample) { return sample != null; })
                .OrderBy(delegate(PerfSample sample) { return sample.ElapsedSec; })
                .ToList();
            List<SessionScreenshot> screenshots = (document.Screenshots ?? new List<SessionScreenshot>())
                .Where(delegate(SessionScreenshot screenshot) { return screenshot != null && File.Exists(screenshot.OriginalPath); })
                .OrderBy(delegate(SessionScreenshot screenshot) { return screenshot.ElapsedSec; })
                .ToList();
            return new PreparedSessionData
            {
                Samples = samples,
                Screenshots = screenshots,
                LatestMetricValues = BuildLatestMetricValues(samples, DeviceLookupService.IsAndroid(document.Device)),
                SelectedSample = FindNearestSample(samples, document.SelectedTime)
            };
        }
        private void ApplySessionDocumentCore(SessionDocument document, PreparedSessionData prepared, bool loadImages, bool addScreenshots)
        {
            if (IsCapturing) ToggleCapture();
            _sampleBuffer = new List<PerfSample>(prepared.Samples);
            Samples = new SampleSnapshot(_sampleBuffer);
            ClearScreenshots();
            if (addScreenshots)
            {
                foreach (SessionScreenshot screenshot in prepared.Screenshots)
                {
                    AddScreenshotItem(screenshot, loadImages);
                }
                Screenshots = _screenshotItems.ToArray();
            }
            _selection = document.Device == null || !ProcessTargetMatcher.IsValidTarget(document.Process)
                ? null
                : new DeviceSelection(document.Device, document.App, document.Process);
            _selectionNeedsRefresh = true;
            UpdateThermalMetricPresentation();
            _sessionStartedAt = document.StartedAt;
            _selectedTime = document.SelectedTime;
            ViewStartTime = 0;
            ViewEndTime = 0;
            FollowLatest = document.FollowLatest;
            OnPropertyChanged(nameof(Samples));
            OnPropertyChanged(nameof(CurrentSelection));
            OnPropertyChanged(nameof(DeviceSummary));
            OnPropertyChanged(nameof(ProcessSummary));
            OnPropertyChanged(nameof(DeviceInfoText));
            OnPropertyChanged(nameof(ShowNormalizedCpu));
            OnPropertyChanged(nameof(ShowCoreCpu));
            OnPropertyChanged(nameof(SelectedTime));
            OnPropertyChanged(nameof(TimelineLabel));
            OnPropertyChanged(nameof(HasScreenshots));
            OnPropertyChanged(nameof(HasSessionData));
            ShowAnalysisDataTab();
            if (addScreenshots) CompleteScreenshotProjection();
            ApplyLatestMetricValues(prepared.LatestMetricValues, prepared.SelectedSample);
            OnPropertyChanged(nameof(TargetSummary));
            Status = "现场数据已还原";
        }
        private void AddScreenshotItem(SessionScreenshot screenshot, bool loadImage)
        {
            if (screenshot == null) return;
            try
            {
                _screenshotItems.Add(new ScreenshotItemViewModel(
                    new ScreenshotInfo { Timestamp = screenshot.Timestamp, ElapsedSec = screenshot.ElapsedSec, Path = screenshot.OriginalPath, Orientation = screenshot.Orientation },
                    loadImage));
            }
            catch { }
        }
        private async Task AddScreenshotItemsAsync(IReadOnlyList<SessionScreenshot> screenshots)
        {
            ScreenshotItemViewModel[] items = await Task.Run(delegate
            {
                return screenshots.Where(delegate(SessionScreenshot screenshot) { return screenshot != null; })
                    .Select(delegate(SessionScreenshot screenshot)
                    {
                        return new ScreenshotItemViewModel(
                            new ScreenshotInfo { Timestamp = screenshot.Timestamp, ElapsedSec = screenshot.ElapsedSec, Path = screenshot.OriginalPath, Orientation = screenshot.Orientation },
                            false);
                    }).ToArray();
            });
            _screenshotItems.AddRange(items);
            Screenshots = _screenshotItems.ToArray();
            OnPropertyChanged(nameof(HasScreenshots));
            OnPropertyChanged(nameof(LatestScreenshotLabel));
            OnPropertyChanged(nameof(HasSessionData));
        }
        private void CompleteScreenshotProjection()
        {
            OnPropertyChanged(nameof(HasScreenshots));
            OnPropertyChanged(nameof(LatestScreenshotLabel));
            OnPropertyChanged(nameof(HasSessionData));
            SelectNearestScreenshot();
        }
        private async Task LoadScreenshotImagesAsync()
        {
            const int batchSize = 4;
            try
            {
                ScreenshotItemViewModel[] pending = Screenshots.ToArray();
                for (int start = 0; start < pending.Length; start += batchSize)
                {
                    ScreenshotItemViewModel[] batch = pending.Skip(start).Take(batchSize).ToArray();
                    await Task.WhenAll(batch.Select(delegate(ScreenshotItemViewModel screenshot) { return screenshot.LoadImageAsync(); }));
                    await Task.Delay(40);
                }
            }
            catch { }
        }
        private void ApplyLatestMetricValues(Dictionary<string, string> values, PerfSample selectedSample)
        {
            ResetMetricValues();
            foreach (KeyValuePair<string, string> value in values)
            {
                MetricRowViewModel metric = Metrics.FirstOrDefault(delegate(MetricRowViewModel item) { return item.Name == value.Key; });
                if (metric != null) metric.Value = value.Value;
            }
            UpdateSelectedMetricValues(selectedSample, true);
        }
        private static Dictionary<string, string> BuildLatestMetricValues(IReadOnlyList<PerfSample> samples, bool isAndroid)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            PerfSample latest = samples == null ? null : samples.Where(delegate(PerfSample sample) { return sample != null; }).OrderByDescending(delegate(PerfSample sample) { return sample.ElapsedSec; }).FirstOrDefault();
            if (latest == null) return values;
            double frameFreshness = isAndroid ? AndroidFrameMetricDisplayFreshSeconds : IosFrameMetricDisplayFreshSeconds;
            PerfSample fps = LatestMetricSample(samples, latest, delegate(PerfSample sample) { return sample.HasFps; }, frameFreshness);
            PerfSample frame = LatestMetricSample(samples, latest, delegate(PerfSample sample) { return sample.HasFrameTimeMax; }, frameFreshness);
            PerfSample jank = LatestMetricSample(samples, latest, delegate(PerfSample sample) { return sample.HasJank; }, frameFreshness);
            PerfSample memory = LatestMetricSample(samples, latest, delegate(PerfSample sample) { return sample.HasMemory; }, ProcessMetricDisplayFreshSeconds);
            PerfSample cpu = LatestMetricSample(samples, latest, delegate(PerfSample sample) { return sample.HasCpu; }, ProcessMetricDisplayFreshSeconds);
            PerfSample normalizedCpu = LatestMetricSample(samples, latest, delegate(PerfSample sample) { return sample.HasCpuNormalized; }, ProcessMetricDisplayFreshSeconds);
            PerfSample temperature = LatestMetricSample(samples, latest, HasTemperatureValues, TemperatureDisplayFreshSeconds);
            PerfSample thermal = LatestMetricSample(samples, latest, HasThermalStateValue, isAndroid ? AndroidThermalStateDisplayFreshSeconds : IosThermalStateDisplayFreshSeconds);
            if (fps != null) values["FPS"] = fps.Fps.ToString("0.##", CultureInfo.InvariantCulture);
            if (frame != null) values["Display FrameTime"] = frame.FrameTimeMaxMs.ToString("0.##", CultureInfo.InvariantCulture);
            if (jank != null)
            {
                values["Jank"] = jank.Jank.ToString("0.##", CultureInfo.InvariantCulture);
                values["BigJank"] = jank.BigJank.ToString("0.##", CultureInfo.InvariantCulture);
            }
            if (memory != null) values["Process Memory"] = memory.MemoryMb.ToString("0.##", CultureInfo.InvariantCulture);
            if (cpu != null) values["Process CPU Raw"] = cpu.CpuPercent.ToString("0.##", CultureInfo.InvariantCulture);
            if (normalizedCpu != null) values["Process CPU Normalized"] = normalizedCpu.CpuNormalizedPercent.ToString("0.##", CultureInfo.InvariantCulture);
            if (temperature != null) values["Device Temperature"] = temperature.TemperatureCelsius.Values.Max().ToString("0.##", CultureInfo.InvariantCulture);
            if (thermal != null) values["Thermal State"] = thermal.ThermalStateLevel.ToString("0", CultureInfo.InvariantCulture);
            return values;
        }

        private static PerfSample LatestMetricSample(IReadOnlyList<PerfSample> samples, PerfSample latest, Func<PerfSample, bool> predicate, double maximumDistanceSeconds)
        {
            if (samples == null || latest == null) return null;
            PerfSample candidate = samples.Where(delegate(PerfSample sample) { return sample != null && predicate(sample); })
                .OrderByDescending(delegate(PerfSample sample) { return sample.ElapsedSec; })
                .FirstOrDefault();
            return candidate == null || latest.ElapsedSec - candidate.ElapsedSec > maximumDistanceSeconds ? null : candidate;
        }

        private static PerfSample LatestMetricSample(IReadOnlyList<PerfSample> samples, PerfSample latest, Func<PerfSample, bool> predicate, Func<PerfSample, double> maximumDistanceSeconds)
        {
            if (samples == null || latest == null) return null;
            PerfSample candidate = samples.Where(delegate(PerfSample sample) { return sample != null && predicate(sample); })
                .OrderByDescending(delegate(PerfSample sample) { return sample.ElapsedSec; })
                .FirstOrDefault();
            return candidate == null || latest.ElapsedSec - candidate.ElapsedSec > maximumDistanceSeconds(candidate) ? null : candidate;
        }

        private static PerfSample FindNearestSample(IReadOnlyList<PerfSample> samples, double? elapsed)
        {
            if (!elapsed.HasValue) return null;
            if (samples is SampleSnapshot snapshot)
                return SampleSnapshot.Nearest(snapshot.Ordered, elapsed.Value, _ => true, double.PositiveInfinity);
            return samples.OrderBy(delegate(PerfSample sample) { return Math.Abs(sample.ElapsedSec - elapsed.Value); }).FirstOrDefault();
        }
        private sealed class PreparedSessionData
        {
            public List<PerfSample> Samples { get; set; }
            public List<SessionScreenshot> Screenshots { get; set; }
            public Dictionary<string, string> LatestMetricValues { get; set; }
            public PerfSample SelectedSample { get; set; }
        }
        private void UpdateMetricValuesFromLatestSamples()
        {
            ResetMetricValues();
            foreach (PerfSample sample in _sampleBuffer)
            {
                if (sample.HasFps) SetMetric("FPS", sample.Fps, "0.##");
                if (sample.HasFrameTimeMax) SetMetric("Display FrameTime", sample.FrameTimeMaxMs, "0.##");
                if (sample.HasJank) { SetMetric("Jank", sample.Jank, "0.##"); SetMetric("BigJank", sample.BigJank, "0.##"); }
                if (sample.HasMemory) SetMetric("Process Memory", sample.MemoryMb, "0.##");
                if (sample.HasCpu) SetMetric("Process CPU Raw", sample.CpuPercent, "0.##");
                if (sample.HasCpuNormalized) SetMetric("Process CPU Normalized", sample.CpuNormalizedPercent, "0.##");
                if (HasTemperatureValues(sample)) SetMetric("Device Temperature", sample.TemperatureCelsius.Values.Max(), "0.##");
                if (HasThermalStateValue(sample)) SetMetric("Thermal State", sample.ThermalStateLevel, "0");
            }
            TrimStaleLatestMetricValues();
            UpdateSelectedMetricValues();
        }

        private void TrimStaleLatestMetricValues()
        {
            IReadOnlyList<PerfSample> ordered = (Samples as SampleSnapshot)?.Ordered ?? Samples;
            PerfSample latest = ordered.Count == 0 ? null : ordered[ordered.Count - 1];
            if (latest == null) return;
            double frameFreshness = IsAndroidSelection ? AndroidFrameMetricDisplayFreshSeconds : IosFrameMetricDisplayFreshSeconds;
            ClearMetricIfStale("FPS", delegate(PerfSample sample) { return sample.HasFps; }, frameFreshness, latest);
            ClearMetricIfStale("Display FrameTime", delegate(PerfSample sample) { return sample.HasFrameTimeMax; }, frameFreshness, latest);
            ClearMetricIfStale("Jank", delegate(PerfSample sample) { return sample.HasJank; }, frameFreshness, latest);
            ClearMetricIfStale("BigJank", delegate(PerfSample sample) { return sample.HasJank; }, frameFreshness, latest);
            ClearMetricIfStale("Process Memory", delegate(PerfSample sample) { return sample.HasMemory; }, ProcessMetricDisplayFreshSeconds, latest);
            ClearMetricIfStale("Process CPU Raw", delegate(PerfSample sample) { return sample.HasCpu; }, ProcessMetricDisplayFreshSeconds, latest);
            ClearMetricIfStale("Process CPU Normalized", delegate(PerfSample sample) { return sample.HasCpuNormalized; }, ProcessMetricDisplayFreshSeconds, latest);
            ClearMetricIfStale("Device Temperature", HasTemperatureValues, TemperatureDisplayFreshSeconds, latest);
            ClearMetricIfStale("Thermal State", HasThermalStateValue, ThermalStateDisplayFreshSeconds(), latest);
        }

        private void ClearMetricIfStale(string name, Func<PerfSample, bool> predicate, double maximumDistanceSeconds, PerfSample latest)
        {
            IReadOnlyList<PerfSample> ordered = (Samples as SampleSnapshot)?.Ordered ?? Samples;
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                if (latest.ElapsedSec - ordered[i].ElapsedSec > maximumDistanceSeconds) break;
                if (predicate(ordered[i])) return;
            }
            MetricRowViewModel metric = Metrics.FirstOrDefault(delegate(MetricRowViewModel item) { return item.Name == name; });
            if (metric != null) metric.Value = "--";
        }
        public void Dispose() { StopCapture(Status, "window_closed"); ClearScreenshots(); _collector.Dispose(); }
        private void OnPropertyChanged([CallerMemberName] string propertyName = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)); }
    }

    internal enum DataPanelTab
    {
        Live,
        Selected,
        Analysis
    }

    public sealed class AnalysisMetricRowViewModel
    {
        public AnalysisMetricRowViewModel(string label, string unit, IEnumerable<double> values)
        {
            Label = label;
            Unit = unit ?? "";
            double[] valid = (values ?? Enumerable.Empty<double>()).Where(delegate(double value)
            {
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }).ToArray();
            HasData = valid.Length > 0;
            Maximum = HasData ? Format(valid.Max()) : "--";
            Minimum = HasData ? Format(valid.Min()) : "--";
            Average = HasData ? Format(valid.Average()) : "--";
        }

        public string Label { get; private set; }
        public string DisplayLabel { get { return string.IsNullOrWhiteSpace(Unit) ? Label : Label + " (" + Unit + ")"; } }
        public string Unit { get; private set; }
        public string Maximum { get; private set; }
        public string Minimum { get; private set; }
        public string Average { get; private set; }
        public bool HasData { get; private set; }

        private static string Format(double value)
        {
            return Math.Abs(value) >= 100 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    public sealed class DataMetricTileViewModel : INotifyPropertyChanged
    {
        private string _value = "--";
        public DataMetricTileViewModel(string label) { Label = label; }
        public event PropertyChangedEventHandler PropertyChanged;
        public string Label { get; private set; }
        public string Value
        {
            get { return _value; }
            set
            {
                string next = string.IsNullOrWhiteSpace(value) ? "--" : value;
                if (_value == next) return;
                _value = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }

    public sealed class ScreenshotItemViewModel : IDisposable, INotifyPropertyChanged
    {
        private const int ThumbnailLongEdge = 100;
        private bool _disposed;
        public ScreenshotItemViewModel(ScreenshotInfo screenshot)
            : this(screenshot, true)
        {
        }
        internal ScreenshotItemViewModel(ScreenshotInfo screenshot, bool loadImage)
        {
            Screenshot = screenshot ?? throw new ArgumentNullException(nameof(screenshot));
            if (loadImage) Image = ScreenshotImageLoader.Load(screenshot.Path, screenshot.Orientation, ThumbnailLongEdge);
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public ScreenshotInfo Screenshot { get; private set; }
        public Bitmap Image { get; private set; }
        public bool HasImage { get { return Image != null; } }
        public string Path { get { return Screenshot.Path; } }
        public double ElapsedSec { get { return Screenshot.ElapsedSec; } }
        public string TimeLabel { get { return ElapsedSec.ToString("0.0", CultureInfo.InvariantCulture) + "s"; } }
        public bool IsLandscape
        {
            get
            {
                int width = Image == null ? 0 : Image.PixelSize.Width;
                int height = Image == null ? 0 : Image.PixelSize.Height;
                return ScreenshotDisplayOrientation.IsLandscape(Screenshot.Orientation, width, height);
            }
        }
        public double CardWidth { get { return IsLandscape ? 116 : 76; } }
        public double ThumbnailViewportWidth { get { return IsLandscape ? 104 : 64; } }
        internal void RebasePath(string oldRoot, string newRoot)
        {
            if (Screenshot == null || string.IsNullOrWhiteSpace(Screenshot.Path)) return;
            string oldPath = System.IO.Path.GetFullPath(oldRoot ?? "").TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            string current = System.IO.Path.GetFullPath(Screenshot.Path);
            if (!current.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase)) return;
            Screenshot.Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(newRoot), current.Substring(oldPath.Length));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
        }
        public async Task LoadImageAsync()
        {
            if (_disposed || Image != null || string.IsNullOrWhiteSpace(Path)) return;
            Bitmap image;
            try
            {
                image = await Task.Run(delegate
                {
                    return ScreenshotImageLoader.Load(Path, Screenshot.Orientation, ThumbnailLongEdge);
                });
            }
            catch { return; }
            if (_disposed)
            {
                image.Dispose();
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(delegate
            {
                if (_disposed)
                {
                    image.Dispose();
                    return;
                }
                Image = image;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasImage)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLandscape)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardWidth)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailViewportWidth)));
            });
        }
        public Task<Bitmap> LoadFullImageAsync()
        {
            if (_disposed || string.IsNullOrWhiteSpace(Path)) return Task.FromResult<Bitmap>(null);
            return Task.Run(delegate
            {
                try { return ScreenshotImageLoader.Load(Path, Screenshot.Orientation); }
                catch { return null; }
            });
        }
        public void Dispose()
        {
            _disposed = true;
            Image?.Dispose();
            Image = null;
        }
    }

    public sealed class MetricRowViewModel : INotifyPropertyChanged
    {
        private string _value;
        private string _selectedValue = "--";
        private bool _isEnabled = true;
        public MetricRowViewModel(string name, string description, string value, string unit, string color)
        {
            Name = name;
            DisplayName = name;
            Description = description;
            _value = value;
            Unit = unit;
            Color = color;
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public string Name { get; private set; }
        public string DisplayName { get; private set; }
        public string Description { get; private set; }
        public string Value
        {
            get { return _value; }
            set
            {
                if (_value == value) return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
        public string SelectedValue
        {
            get { return _selectedValue; }
            set
            {
                if (_selectedValue == value) return;
                _selectedValue = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedValue)));
            }
        }
        public string Unit { get; private set; }
        public string Color { get; private set; }
        public string TextColor { get { return AppThemeManager.ColorValue(IsEnabled ? "Theme.TextPrimary" : "Theme.TextMuted"); } }
        public string ValueColor { get { return AppThemeManager.ColorValue(IsEnabled ? "Theme.TextPrimary" : "Theme.TextMuted"); } }
        public string UnitColor { get { return AppThemeManager.ColorValue(IsEnabled ? "Theme.TextSecondary" : "Theme.TextMuted"); } }
        public string DescriptionColor { get { return AppThemeManager.ColorValue("Theme.TextMuted"); } }

        public void RefreshThemeColors()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextColor)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueColor)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnitColor)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DescriptionColor)));
        }
        public bool IsEnabled
        {
            get { return _isEnabled; }
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnitColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DescriptionColor)));
            }
        }

        public void SetPresentation(string displayName, string description, string unit)
        {
            DisplayName = displayName;
            Description = description;
            Unit = unit;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Unit)));
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) { return true; }
        public void Execute(object parameter) { _execute(); }
    }
}
