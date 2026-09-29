using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CSharpIosPerfMonitor;
using MoTuPerf.Platform;

namespace MoTuPerf.Desktop
{
    public partial class DevicePickerWindow : Window
    {
        private enum HarmonyRefreshResult
        {
            Failed,
            Refreshed,
            Selected
        }

        private TextBlock DeviceCountText;
        private Button AppleDriverButton;
        private Button HarmonyHdcButton;
        private Button DeviceDiagnosticsButton;
        private TextBlock StatusText;
        private ComboBox DeviceList;
        private ListBox AppList;
        private ListBox ProcessList;
        private TextBox AppSearch;
        private TextBox ProcessSearch;
        private TextBlock SelectedAppName;
        private TextBlock SelectedAppBundle;
        private TextBlock SelectedAppGlyph;
        private Image SelectedAppIcon;
        private Button ProcessTab;
        private Button LaunchTab;
        private CheckBox HarmonyShowAllCheckBox;
        private Grid ProcessPanel;
        private Grid LaunchPanel;
        private CheckBox AutoStartCheckBox;
        private bool _launchMode;
        private bool _showAllHarmonyTargets;
        private bool _synchronizingSelections;
        private readonly DeviceLookupService _lookup = new DeviceLookupService();
        private readonly DeviceSelection _initialSelection;
        private readonly List<AppInfo> _apps = new List<AppInfo>();
        private readonly List<ProcessInfo> _processes = new List<ProcessInfo>();
        private CancellationTokenSource _loadCancellation;
        private long _loadGeneration;
        private CancellationTokenSource _driverDownloadCancellation;
        private readonly AppleDriverDownloadService _driverDownload = new AppleDriverDownloadService();
        private bool _driverDownloadInProgress;
        private bool _launchInProgress;
        private bool _targetsLoading;
        private bool _closed;
        private IconPathConverter _iconConverter;
        private string _appleDriverButtonIdleText = "下载苹果设备驱动";
        private string _deviceCountStatus = "正在检测设备...";
        private DeviceDiagnosticsSnapshot _deviceDiagnostics = DeviceDiagnosticsFormatter.Loading();
        private string _loadedDeviceUdid = "";
        private string _preferredAppBundleId = "";
        private int _preferredHarmonyUserId = -1;
        private int _preferredHarmonyAppIndex = -1;
        private string _pendingHarmonyLaunchBundleId = "";
        private int _pendingHarmonyLaunchUserId = -1;
        private int _pendingHarmonyLaunchAppIndex = -1;
        private string _launchTargetDeviceUdid = "";

        public DevicePickerWindow()
            : this(null)
        {
        }

        public DevicePickerWindow(DeviceSelection initialSelection)
        {
            _initialSelection = initialSelection;
            InitializeComponent();
            Opened += async delegate { await LoadDevicesAsync(); };
            Closed += delegate
            {
                _closed = true;
                CancelLoad();
                CancelDriverDownload();
                if (SelectedAppIcon != null) SelectedAppIcon.Source = null;
                _iconConverter?.Dispose();
            };
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            DeviceCountText = this.FindControl<TextBlock>("DeviceCountText");
            AppleDriverButton = this.FindControl<Button>("AppleDriverButton");
            HarmonyHdcButton = this.FindControl<Button>("HarmonyHdcButton");
            DeviceDiagnosticsButton = this.FindControl<Button>("DeviceDiagnosticsButton");
            StatusText = DeviceCountText;
            DeviceList = this.FindControl<ComboBox>("DeviceList");
            AppList = this.FindControl<ListBox>("AppList");
            ProcessList = this.FindControl<ListBox>("ProcessList");
            AppSearch = this.FindControl<TextBox>("AppSearch");
            ProcessSearch = this.FindControl<TextBox>("ProcessSearch");
            SelectedAppName = this.FindControl<TextBlock>("SelectedAppName");
            SelectedAppBundle = this.FindControl<TextBlock>("SelectedAppBundle");
            SelectedAppGlyph = this.FindControl<TextBlock>("SelectedAppGlyph");
            SelectedAppIcon = this.FindControl<Image>("SelectedAppIcon");
            ProcessTab = this.FindControl<Button>("ProcessTab");
            LaunchTab = this.FindControl<Button>("LaunchTab");
            HarmonyShowAllCheckBox = this.FindControl<CheckBox>("HarmonyShowAllCheckBox");
            ProcessPanel = this.FindControl<Grid>("ProcessPanel");
            LaunchPanel = this.FindControl<Grid>("LaunchPanel");
            AutoStartCheckBox = this.FindControl<CheckBox>("AutoStartCheckBox");
            _iconConverter = Resources["IconPathConverter"] as IconPathConverter;
            SetLaunchMode(false);
        }
        private async void RefreshDevices(object sender, Avalonia.Interactivity.RoutedEventArgs e) { await LoadDevicesAsync(); }

        private void DialogTitlePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }

        private async Task LoadDevicesAsync()
        {
            if (_closed) return;
            long generation;
            CancellationToken token = NewLoadToken(out generation);
            ApplyDeviceDiagnostics(DeviceDiagnosticsFormatter.Loading());
            AppleDriverButton.IsVisible = false;
            HarmonyHdcButton.IsVisible = false;
            try
            {
                DeviceDiscoveryReport report = await _lookup.DiscoverDevicesAsync(token);
                if (!IsCurrentLoad(generation)) return;
                DeviceList.ItemsSource = report.Devices;
                ApplyDeviceDiagnostics(DeviceDiagnosticsFormatter.FromReport(report));
                AppleDriverButton.IsVisible = report.AppleDriverActionAvailable;
                HarmonyHdcButton.IsVisible = _deviceDiagnostics.HarmonyHdcActionAvailable;
                _appleDriverButtonIdleText = report.AppleDriverMissing ? "下载苹果设备驱动" : "修复苹果设备驱动";
                AppleDriverButton.Content = _appleDriverButtonIdleText;
                DeviceInfo preferred = report.Devices.FirstOrDefault(delegate(DeviceInfo device)
                {
                    return _initialSelection != null && _initialSelection.Device != null && device.Udid == _initialSelection.Device.Udid;
                }) ?? report.Devices.FirstOrDefault(delegate(DeviceInfo device) { return device.Recommended; }) ?? report.Devices.FirstOrDefault();
                DeviceList.SelectedItem = preferred;
                UpdateHarmonyTargetFilterVisibility(preferred, true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ApplyDeviceDiagnostics(DeviceDiagnosticsFormatter.Error("设备检测失败：" + ex.Message, ex.Message, ex.Message));
                AppleDriverButton.IsVisible = false;
                HarmonyHdcButton.IsVisible = false;
            }
        }

        private void ApplyDeviceDiagnostics(DeviceDiagnosticsSnapshot snapshot)
        {
            _deviceDiagnostics = snapshot ?? DeviceDiagnosticsFormatter.Loading();
            _deviceCountStatus = _deviceDiagnostics.OverallStatus;
            DeviceCountText.Text = _deviceDiagnostics.OverallStatus;
            DeviceDiagnosticsButton.IsEnabled = _deviceDiagnostics.IsReady;
        }

        private async void ShowDeviceDiagnostics(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (!_deviceDiagnostics.IsReady) return;
            DeviceDiagnosticsSnapshot snapshot = _deviceDiagnostics;
            DeviceDiagnosticsWindow window = new DeviceDiagnosticsWindow(snapshot);
            window.RecheckRequested += delegate { _ = LoadDevicesAsync(); };
            await window.ShowDialog(this);
        }

        private async void OpenHarmonyHdcSetup(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            HarmonyHdcSetupWindow window = new HarmonyHdcSetupWindow();
            window.RecheckRequested += delegate { _ = LoadDevicesAsync(); };
            await window.ShowDialog(this);
        }

        private async void DownloadAppleDriver(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_driverDownloadInProgress) return;
            _driverDownloadInProgress = true;
            _driverDownloadCancellation = new CancellationTokenSource();
            AppleDriverButton.IsEnabled = false;
            AppleDriverButton.Content = "正在下载...";
            try
            {
                string downloadDirectory = Path.Combine(RuntimeTools.DataDirectory, "downloads");
                Progress<double> progress = new Progress<double>(delegate(double value)
                {
                    StatusText.Text = value < 0
                        ? "正在下载苹果设备驱动..."
                        : "正在下载苹果设备驱动... " + (value * 100).ToString("0") + "%";
                });
                AppleDriverDownloadResult result = await _driverDownload.DownloadAsync(
                    downloadDirectory, progress, _driverDownloadCancellation.Token);
                bool install = await new ConfirmDialogWindow(
                    "苹果设备驱动已下载",
                    "安装包已下载完成，是否现在启动安装？安装完成后请重新插拔 iPhone 或 iPad，再点击刷新。",
                    "现在安装",
                    "稍后安装").ShowDialog<bool>(this);
                if (!install)
                {
                    StatusText.Text = "驱动安装包已保存，可稍后手动安装。";
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = result.FilePath,
                    UseShellExecute = true
                });
                StatusText.Text = "已启动苹果设备驱动安装程序，完成后请重新插拔设备并刷新。";
            }
            catch (OperationCanceledException) { StatusText.Text = "苹果设备驱动下载已取消。"; }
            catch (Exception ex)
            {
                StatusText.Text = "苹果设备驱动下载失败：" + ex.Message;
            }
            finally
            {
                AppleDriverButton.IsEnabled = true;
                AppleDriverButton.Content = _appleDriverButtonIdleText;
                _driverDownloadInProgress = false;
                if (_driverDownloadCancellation != null)
                {
                    _driverDownloadCancellation.Dispose();
                    _driverDownloadCancellation = null;
                }
            }
        }

        private async void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e) { await LoadTargetsAsync(); }
        private void AppSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_synchronizingSelections) return;
            AppInfo app = AppList == null ? null : AppList.SelectedItem as AppInfo;
            if (app == null) return;
            if (_launchInProgress) CancelLoad();
            ClearPendingHarmonyLaunchTarget();
            UpdateSelectedAppSummary(app);
            if (!_launchMode) RunSelectionSync(ApplyProcessFilter);
        }

        private void ProcessSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_synchronizingSelections || _launchMode) return;
            ProcessInfo process = ProcessList == null ? null : ProcessList.SelectedItem as ProcessInfo;
            if (process == null) return;
            if (_launchInProgress) CancelLoad();
            AppInfo app = FindAppForProcess(process);
            RunSelectionSync(delegate
            {
                if ((app != null || DeviceLookupService.IsHarmony(process.Platform))
                    && !ReferenceEquals(AppList.SelectedItem, app)) AppList.SelectedItem = app;
            });
            ClearPendingHarmonyLaunchTarget();
            UpdateSelectedAppSummary(app);
        }

        private void ShowProcessTab(object sender, Avalonia.Interactivity.RoutedEventArgs e) { SetLaunchMode(false); }
        private void ShowLaunchTab(object sender, Avalonia.Interactivity.RoutedEventArgs e) { SetLaunchMode(true); }
        private async void RefreshTargetLists(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            await LoadTargetsAsync();
        }

        private async Task<bool> LoadTargetsAsync()
        {
            if (_closed) return false;
            DeviceInfo device = DeviceList.SelectedItem as DeviceInfo;
            if (device == null)
            {
                CancelLoad();
                _targetsLoading = false;
                ClearPendingHarmonyLaunchTarget();
                UpdateHarmonyTargetFilterVisibility(null, true);
                return false;
            }
            string deviceUdid = device.Udid ?? "";
            UpdateHarmonyTargetFilterVisibility(device, false);
            if (!string.Equals(_launchTargetDeviceUdid, deviceUdid, StringComparison.OrdinalIgnoreCase))
                ClearPendingHarmonyLaunchTarget();
            string harmonyTargetBundleId = !string.IsNullOrWhiteSpace(_preferredAppBundleId)
                ? _preferredAppBundleId : _pendingHarmonyLaunchBundleId;
            int harmonyTargetUserId = !string.IsNullOrWhiteSpace(_preferredAppBundleId)
                ? _preferredHarmonyUserId : _pendingHarmonyLaunchUserId;
            int harmonyTargetAppIndex = !string.IsNullOrWhiteSpace(_preferredAppBundleId)
                ? _preferredHarmonyAppIndex : _pendingHarmonyLaunchAppIndex;
            bool waitForLaunch = !string.IsNullOrWhiteSpace(_preferredAppBundleId);
            AppInfo previousApp = string.Equals(_loadedDeviceUdid, deviceUdid, StringComparison.OrdinalIgnoreCase)
                ? AppList.SelectedItem as AppInfo
                : null;
            ProcessInfo previousProcess = string.Equals(_loadedDeviceUdid, deviceUdid, StringComparison.OrdinalIgnoreCase)
                ? ProcessList.SelectedItem as ProcessInfo
                : null;
            long generation;
            CancellationToken token = NewLoadToken(out generation);
            _targetsLoading = true;
            StatusText.Text = "正在读取 " + device.PickerLabel + " 的 APP 和进程...";
            RunSelectionSync(delegate
            {
                AppList.ItemsSource = null;
                ProcessList.ItemsSource = null;
            });
            try
            {
                List<AppInfo> loadedApps = null;
                List<ProcessInfo> loadedProcesses = null;
                Exception appsError = null;
                Exception processesError = null;
                string harmonyProcessInventoryError = "";
                string harmonyUserInventoryError = "";
                if (DeviceLookupService.IsHarmony(device))
                {
                    try
                    {
                        HarmonyTargetInventory snapshot = await _lookup.ListTargetsAsync(device, token);
                        loadedApps = snapshot == null ? new List<AppInfo>() : snapshot.Apps;
                        loadedProcesses = snapshot == null ? new List<ProcessInfo>() : snapshot.Processes;
                        harmonyProcessInventoryError = snapshot == null ? "" : snapshot.ProcessInventoryError ?? "";
                        harmonyUserInventoryError = snapshot == null ? "" : snapshot.UserInventoryError ?? "";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // A Harmony snapshot is atomic from the picker's point
                        // of view. Do not present a partial app/process pair
                        // from different HDC rounds after a snapshot failure.
                        appsError = ex;
                        processesError = ex;
                    }
                }
                else
                {
                    Task<List<AppInfo>> appsTask = _lookup.ListAppsAsync(device, token);
                    Task<List<ProcessInfo>> processesTask = _lookup.ListProcessesAsync(device, token);
                    try { loadedApps = await appsTask; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { appsError = ex; }
                    try { loadedProcesses = await processesTask; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { processesError = ex; }
                }
                if (!IsCurrentLoad(generation, deviceUdid)) return false;
                loadedApps = loadedApps ?? new List<AppInfo>();
                loadedProcesses = loadedProcesses ?? new List<ProcessInfo>();
                if (appsError != null && processesError != null)
                    throw new IOException("读取 APP 和进程失败：" + FirstNonEmpty(appsError.Message, processesError.Message));
                if (appsError != null)
                {
                    if (!DeviceLookupService.IsHarmony(device) || loadedProcesses.Count == 0)
                        throw new IOException("读取 APP 失败：" + appsError.Message);
                    loadedApps = HarmonyAppsFromProcesses(loadedProcesses);
                }
                else if (loadedApps.Count == 0 && DeviceLookupService.IsHarmony(device) && loadedProcesses.Count > 0)
                {
                    loadedApps = HarmonyAppsFromProcesses(loadedProcesses);
                }
                if (DeviceLookupService.IsHarmony(device) && waitForLaunch)
                {
                    HarmonyTargetInventory latest = await WaitForHarmonyProcessAsync(
                        device,
                        new HarmonyTargetInventory
                        {
                            Apps = loadedApps,
                            Processes = loadedProcesses,
                            ProcessInventoryError = harmonyProcessInventoryError,
                            UserInventoryError = harmonyUserInventoryError
                        },
                        token,
                        generation,
                        deviceUdid,
                        harmonyTargetBundleId,
                        harmonyTargetUserId,
                        harmonyTargetAppIndex);
                    if (!IsCurrentLoad(generation, deviceUdid)) return false;
                    if (latest != null)
                    {
                        loadedApps = latest.Apps ?? new List<AppInfo>();
                        loadedProcesses = latest.Processes ?? new List<ProcessInfo>();
                        harmonyProcessInventoryError = latest.ProcessInventoryError ?? "";
                        harmonyUserInventoryError = latest.UserInventoryError ?? "";
                    }
                }
                _apps.Clear();
                _apps.AddRange(loadedApps);
                _processes.Clear();
                _processes.AddRange(loadedProcesses);
                MarkHarmonyPickerUserScopeVisibility(_apps, _processes);
                bool preserveHarmonyAppPreference = DeviceLookupService.IsHarmony(device)
                    && !string.IsNullOrWhiteSpace(harmonyTargetBundleId);
                AppInfo selectedApp = FindAppByBundle(harmonyTargetBundleId, harmonyTargetUserId, harmonyTargetAppIndex);
                if (!preserveHarmonyAppPreference)
                {
                    selectedApp = selectedApp
                        ?? FindInitialApp()
                        ?? (previousApp == null ? null : _apps.FirstOrDefault(delegate(AppInfo app) { return SameAppSelection(app, previousApp); }))
                        ?? _apps.FirstOrDefault(delegate(AppInfo app) { return app.Recommended; })
                        ?? _apps.FirstOrDefault(delegate(AppInfo app) { return !string.IsNullOrWhiteSpace(app.BundleId); })
                        ?? _apps.FirstOrDefault();
                }
                ProcessInfo selectedProcess = FindProcessForApp(selectedApp);
                if (preserveHarmonyAppPreference)
                    selectedProcess = FindHarmonyProcessForBundle(loadedProcesses, harmonyTargetBundleId,
                        harmonyTargetUserId, harmonyTargetAppIndex);
                if (DeviceLookupService.IsHarmony(device) && !preserveHarmonyAppPreference && !_launchMode)
                {
                    ProcessInfo restoredProcess = previousProcess == null ? null : _processes.FirstOrDefault(process => SameProcessSelection(process, previousProcess));
                    restoredProcess = restoredProcess ?? FindInitialProcess();
                    if (restoredProcess != null)
                    {
                        selectedProcess = restoredProcess;
                        selectedApp = FindAppForProcess(restoredProcess);
                    }
                }
                if (ShouldAutoSelectFallbackProcess(
                    DeviceLookupService.IsHarmony(device),
                    preserveHarmonyAppPreference,
                    selectedApp,
                    selectedProcess))
                {
                    selectedProcess = FindInitialProcess()
                        ?? (previousProcess == null ? null : _processes.FirstOrDefault(delegate(ProcessInfo process)
                        {
                            return SameProcessSelection(process, previousProcess);
                        }))
                        ?? _processes.FirstOrDefault(delegate(ProcessInfo process) { return process.Recommended; })
                        ?? _processes.FirstOrDefault();
                }
                bool launchProcessMissing = DeviceLookupService.IsHarmony(device)
                    && !string.IsNullOrWhiteSpace(harmonyTargetBundleId)
                    && selectedProcess == null;
                if (launchProcessMissing)
                {
                    _pendingHarmonyLaunchBundleId = harmonyTargetBundleId;
                    _pendingHarmonyLaunchUserId = harmonyTargetUserId;
                    _pendingHarmonyLaunchAppIndex = harmonyTargetAppIndex;
                }
                else if (DeviceLookupService.IsHarmony(device) && !string.IsNullOrWhiteSpace(harmonyTargetBundleId))
                {
                    ClearPendingHarmonyLaunchTarget();
                }
                RunSelectionSync(delegate
                {
                    ApplyAppFilter();
                    AppList.SelectedItem = selectedApp;
                    ApplyProcessFilter();
                    ProcessList.SelectedItem = selectedProcess;
                });
                UpdateSelectedAppSummary(selectedApp ?? FindAppForProcess(selectedProcess));
                StatusText.Text = launchProcessMissing
                    ? string.IsNullOrWhiteSpace(harmonyProcessInventoryError)
                        ? "APP 已启动，但暂未检测到唯一匹配进程，请刷新或手动选择真实 PID。"
                        : harmonyProcessInventoryError
                        : appsError != null
                            ? "应用清单暂不可用，已保留真实进程列表，请直接选择目标 PID。"
                            : processesError == null
                                && string.IsNullOrWhiteSpace(harmonyProcessInventoryError)
                                && !string.IsNullOrWhiteSpace(harmonyUserInventoryError)
                            ? "应用和进程已读取，用户资料暂不可用；已保留用户范围未知，不会默认使用 user 0。"
                            : processesError == null && string.IsNullOrWhiteSpace(harmonyProcessInventoryError)
                            && string.IsNullOrWhiteSpace(harmonyUserInventoryError)
                        ? _deviceCountStatus
                        : "应用列表已读取，但进程列表暂不可用"
                            + (string.IsNullOrWhiteSpace(harmonyProcessInventoryError)
                                ? string.IsNullOrWhiteSpace(harmonyUserInventoryError)
                                    ? "，请刷新或检查 HDC 进程权限。"
                                    : "：" + harmonyUserInventoryError + ""
                                : "：" + harmonyProcessInventoryError + "。请刷新或检查 HDC 进程权限。");
                _loadedDeviceUdid = deviceUdid;
                _preferredAppBundleId = "";
                _preferredHarmonyUserId = -1;
                _preferredHarmonyAppIndex = -1;
                _ = HydrateIconsAndRefreshAsync(
                    device,
                    _apps.ToArray(),
                    _processes.ToArray(),
                    token,
                    generation,
                    deviceUdid);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                if (IsCurrentLoad(generation, deviceUdid)) StatusText.Text = "读取 APP/进程失败：" + ex.Message;
                return false;
            }
            finally
            {
                if (IsCurrentLoad(generation, deviceUdid)) _targetsLoading = false;
            }
        }

        internal static List<AppInfo> HarmonyAppsFromProcesses(IEnumerable<ProcessInfo> processes)
        {
            List<AppInfo> apps = new List<AppInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ProcessInfo> validProcesses = (processes ?? Enumerable.Empty<ProcessInfo>())
                .Where(delegate(ProcessInfo process)
                {
                    return ProcessTargetMatcher.IsValidTarget(process)
                        && DeviceLookupService.IsHarmony(process.Platform);
                })
                .ToList();
            Dictionary<string, int> processCounts = validProcesses
                .GroupBy(delegate(ProcessInfo process)
                {
                    string bundle = FirstNonEmpty(process.BundleId, process.OwnerBundleId);
                    return string.IsNullOrWhiteSpace(bundle)
                        ? "pid:" + process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "bundle:" + bundle + ":user:" + process.HarmonyUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + ":app:" + process.HarmonyAppIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    delegate(IGrouping<string, ProcessInfo> group) { return group.Key; },
                    delegate(IGrouping<string, ProcessInfo> group) { return group.Count(); },
                    StringComparer.OrdinalIgnoreCase);
            foreach (ProcessInfo process in validProcesses)
            {
                string bundle = FirstNonEmpty(process.BundleId, process.OwnerBundleId);
                string key = string.IsNullOrWhiteSpace(bundle)
                    ? "pid:" + process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "bundle:" + bundle + ":user:" + process.HarmonyUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":app:" + process.HarmonyAppIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!seen.Add(key)) continue;
                bool uniqueProcess = processCounts[key] == 1;
                AppInfo app = new AppInfo
                {
                    BundleId = bundle,
                    Name = FirstNonEmpty(process.DisplayName, process.Name, bundle),
                    ProcessPid = uniqueProcess ? process.Pid : 0,
                    ProcessName = uniqueProcess ? process.Name : "",
                    Platform = "harmony",
                    Recommended = process.Recommended,
                    Reason = string.IsNullOrWhiteSpace(bundle)
                        ? "运行中的鸿蒙进程 · PID " + process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "运行中的鸿蒙应用",
                    IsRunning = true,
                    IsProcessOnly = true,
                    HarmonyUserId = process.HarmonyUserId,
                    HarmonyAppIndex = process.HarmonyAppIndex
                };
                if (process.HarmonyUserId >= 0) app.HarmonyUserIds.Add(process.HarmonyUserId);
                apps.Add(app);
            }
            return apps.OrderByDescending(delegate(AppInfo app) { return app.Recommended; })
                .ThenBy(delegate(AppInfo app) { return app.BundleId; })
                .ToList();
        }

        private async void LaunchSelectedApp(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_launchInProgress || _targetsLoading || _driverDownloadInProgress) return;
            DeviceInfo device = DeviceList.SelectedItem as DeviceInfo;
            AppInfo app = AppList.SelectedItem as AppInfo;
            if (device == null || app == null)
            {
                StatusText.Text = "请先选择设备和 APP";
                return;
            }
            if (app.IsProcessOnly && DeviceLookupService.IsHarmony(device) && !app.CanAttemptLaunch)
            {
                if (!TrySelectHarmonyRunningProcess(device, app))
                    StatusText.Text = "该鸿蒙目标只有运行中进程，请在选择进程页手动选择真实 PID。";
                return;
            }
            _launchInProgress = true;
            try
            {
                StatusText.Text = "正在启动 " + app.Name + "...";
                ClearPendingHarmonyLaunchTarget();
                _launchTargetDeviceUdid = device.Udid ?? "";
                _preferredAppBundleId = app.BundleId ?? "";
                _preferredHarmonyUserId = app.HarmonyUserId;
                _preferredHarmonyAppIndex = app.HarmonyAppIndex;
                long launchGeneration;
                CancellationToken token = NewLoadToken(out launchGeneration);
                if (DeviceLookupService.IsHarmony(device))
                {
                    _processes.Clear();
                    RunSelectionSync(delegate { ProcessList.ItemsSource = null; });
                }
                ProcessResult result = await _lookup.LaunchAppAsync(device, app, token);
                if (!IsCurrentLoad(launchGeneration, device.Udid)) return;
                if (result.ExitCode != 0)
                {
                    _preferredAppBundleId = "";
                    ClearPendingHarmonyLaunchTarget();
                    if (DeviceLookupService.IsHarmony(device))
                    {
                        HarmonyRefreshResult refreshResult = await RefreshHarmonyProcessesAfterLaunchFailureAsync(device, app, token, launchGeneration);
                        if (!IsCurrentLoad(launchGeneration, device.Udid)) return;
                        if (refreshResult == HarmonyRefreshResult.Selected)
                            return;
                        StatusText.Text = refreshResult == HarmonyRefreshResult.Failed
                            ? "启动失败，且刷新鸿蒙目标列表失败；未使用旧进程，请刷新设备后重新选择。"
                            : "启动失败，已刷新鸿蒙目标列表，但暂未找到唯一匹配进程；请在进程页选择真实 PID。";
                        return;
                    }
                    if (TrySelectHarmonyRunningProcess(device, app)) return;
                    StatusText.Text = IosLookupService.IsDeveloperModeDisabled(result)
                        ? "启动失败：请在 iOS 设置中开启开发者模式，重启并解锁设备后重试。"
                        : "启动失败：" + FirstNonEmptyLine(result.Stderr, result.Stdout);
                    return;
                }
                if (DeviceLookupService.IsHarmony(device))
                {
                    _pendingHarmonyLaunchBundleId = app.BundleId ?? "";
                    _pendingHarmonyLaunchUserId = app.HarmonyUserId;
                    _pendingHarmonyLaunchAppIndex = app.HarmonyAppIndex;
                }
                StatusText.Text = "APP 已启动，正在刷新进程...";
                await Task.Delay(1200, token);
                if (!IsCurrentLoad(launchGeneration, device.Udid)) return;
                long refreshGeneration = _loadGeneration + 1;
                bool refreshed = await LoadTargetsAsync();
                if (CanFinalizeHarmonyLaunchRefresh(
                    refreshed,
                    IsCurrentLoad(refreshGeneration, device.Udid),
                    _preferredAppBundleId)) SetLaunchMode(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_closed && ReferenceEquals(DeviceList.SelectedItem, device)) StatusText.Text = "启动 APP 失败：" + ex.Message;
            }
            finally
            {
                // A cancelled/failed post-launch refresh must not leave the
                // old Bundle/user target armed for the next manual refresh.
                // A successful LoadTargetsAsync clears the preference after
                // it has applied the complete current snapshot.
                if (!string.IsNullOrWhiteSpace(_preferredAppBundleId))
                    ClearPendingHarmonyLaunchTarget();
                _launchInProgress = false;
            }
        }

        private bool TrySelectHarmonyRunningProcess(DeviceInfo device, AppInfo app)
        {
            if (!DeviceLookupService.IsHarmony(device) || app == null) return false;
            ProcessInfo process = FindProcessForApp(app);
            if (process == null) return false;
            RunSelectionSync(delegate
            {
                SetLaunchMode(false);
                ProcessList.SelectedItem = process;
            });
            UpdateSelectedAppSummary(app);
            StatusText.Text = "启动入口不可用，已切换到唯一匹配进程 PID "
                + process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "，可以直接开始采集。";
            return true;
        }

        private async Task<HarmonyRefreshResult> RefreshHarmonyProcessesAfterLaunchFailureAsync(
            DeviceInfo device,
            AppInfo app,
            CancellationToken token,
            long generation)
        {
            if (!IsCurrentLoad(generation, device == null ? "" : device.Udid)) return HarmonyRefreshResult.Failed;
            HarmonyTargetInventory snapshot;
            try
            {
                snapshot = await _lookup.ListTargetsAsync(device, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return HarmonyRefreshResult.Failed;
            }
            if (!IsCurrentLoad(generation, device == null ? "" : device.Udid)) return HarmonyRefreshResult.Failed;
            List<AppInfo> latestApps = snapshot == null ? new List<AppInfo>() : snapshot.Apps ?? new List<AppInfo>();
            List<ProcessInfo> latestProcesses = snapshot == null ? new List<ProcessInfo>() : snapshot.Processes ?? new List<ProcessInfo>();
            _apps.Clear();
            _apps.AddRange(latestApps);
            _processes.Clear();
            _processes.AddRange(latestProcesses);
            MarkHarmonyPickerUserScopeVisibility(_apps, _processes);
            // Only the refreshed snapshot may authorize the next selection.
            // Falling back to the pre-launch object can leave an app row that
            // is no longer present in the current inventory, together with a
            // stale PID or Ability list from the previous snapshot.
            AppInfo refreshedApp = ResolveHarmonyRefreshApp(latestApps, app);
            RunSelectionSync(delegate
            {
                ApplyAppFilter();
                AppList.SelectedItem = refreshedApp;
                ApplyProcessFilter();
                ProcessList.SelectedItem = refreshedApp == null
                    ? null
                    : FindHarmonyProcessForApp(latestProcesses, refreshedApp);
            });
            if (refreshedApp == null)
            {
                // ApplyProcessFilter may select a recommended fallback when no
                // app is selected. A failed launch must leave that choice
                // empty so the user cannot accidentally collect another app.
                RunSelectionSync(delegate { ProcessList.SelectedItem = null; });
                UpdateSelectedAppSummary(null);
                return HarmonyRefreshResult.Refreshed;
            }
            return TrySelectHarmonyRunningProcess(device, refreshedApp)
                ? HarmonyRefreshResult.Selected
                : HarmonyRefreshResult.Refreshed;
        }

        internal static AppInfo ResolveHarmonyRefreshApp(
            IEnumerable<AppInfo> refreshedApps,
            AppInfo requestedApp)
        {
            if (requestedApp == null) return null;
            return FindHarmonyAppByBundle(
                refreshedApps,
                requestedApp.BundleId,
                requestedApp.HarmonyUserId,
                requestedApp.HarmonyAppIndex);
        }

        private void AppSearchChanged(object sender, TextChangedEventArgs e) { RunSelectionSync(ApplyAppFilter); }
        private void ProcessSearchChanged(object sender, TextChangedEventArgs e) { RunSelectionSync(ApplyProcessFilter); }
        private void HarmonyShowAllTargetsChanged(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _showAllHarmonyTargets = HarmonyShowAllCheckBox != null && HarmonyShowAllCheckBox.IsChecked == true;
            RunSelectionSync(delegate
            {
                ApplyAppFilter();
                ApplyProcessFilter();
            });
        }

        private void UpdateHarmonyTargetFilterVisibility(DeviceInfo device, bool reset)
        {
            bool visible = DeviceLookupService.IsHarmony(device);
            if (reset && !visible) _showAllHarmonyTargets = false;
            if (HarmonyShowAllCheckBox == null) return;
            HarmonyShowAllCheckBox.IsVisible = visible;
            if (reset && !visible) HarmonyShowAllCheckBox.IsChecked = false;
        }
        private void ApplyAppFilter()
        {
            string query = (AppSearch.Text ?? "").Trim();
            AppInfo current = AppList.SelectedItem as AppInfo;
            DeviceInfo selectedDevice = DeviceList.SelectedItem as DeviceInfo;
            IEnumerable<AppInfo> source = _apps;
            if (DeviceLookupService.IsHarmony(selectedDevice) && !_showAllHarmonyTargets
                && string.IsNullOrWhiteSpace(query))
                source = source.Where(IsHarmonyDefaultAppTarget);
            List<AppInfo> items = string.IsNullOrWhiteSpace(query) ? source.ToList() : source.Where(delegate(AppInfo app)
            {
                return Contains(app.Name, query) || Contains(app.BundleId, query) || Contains(app.TargetIdentifier, query)
                    || Contains(app.Version, query) || Contains(app.Reason, query)
                    || app.ProcessPid.ToString().Contains(query);
            }).ToList();
            AppList.ItemsSource = items;
            AppInfo selected = current == null ? null : items.FirstOrDefault(delegate(AppInfo app) { return SameAppSelection(app, current); });
            if (selected != null) AppList.SelectedItem = selected;
        }
        private void ApplyProcessFilter()
        {
            string query = (ProcessSearch.Text ?? "").Trim();
            ProcessInfo current = ProcessList.SelectedItem as ProcessInfo;
            DeviceInfo selectedDevice = DeviceList.SelectedItem as DeviceInfo;
            IEnumerable<ProcessInfo> source = _processes.Where(ProcessTargetMatcher.IsValidTarget);
            if (DeviceLookupService.IsHarmony(selectedDevice) && !_showAllHarmonyTargets
                && string.IsNullOrWhiteSpace(query))
                source = source.Where(process => IsHarmonyDefaultProcessTarget(process, _apps));
            if (string.IsNullOrWhiteSpace(query))
            {
                DeviceInfo device = DeviceList.SelectedItem as DeviceInfo;
                AppInfo selectedApp = AppList.SelectedItem as AppInfo;
                string selectedBundle = FirstNonEmpty(
                    selectedApp == null ? "" : selectedApp.BundleId,
                    _initialSelection == null || _initialSelection.App == null ? "" : _initialSelection.App.BundleId);
                if (DeviceLookupService.IsIos(device))
                {
                    source = source.Where(delegate(ProcessInfo process)
                    {
                        return ProcessTargetMatcher.IsIosDefaultPickerProcess(process, selectedBundle);
                    });
                }
            }
            else
            {
                source = source.Where(delegate(ProcessInfo process)
                {
                    return Contains(process.Name, query) || Contains(process.DisplayName, query) || Contains(process.BundleId, query)
                        || Contains(process.OwnerBundleId, query) || Contains(process.Reason, query) || process.Pid.ToString().Contains(query);
                });
            }
            List<ProcessInfo> items = source.OrderByDescending(delegate(ProcessInfo process) { return process.Recommended; }).ToList();
            ProcessList.ItemsSource = items;
            ProcessInfo selected = current == null ? null : items.FirstOrDefault(delegate(ProcessInfo process) { return process.Pid == current.Pid; });
            AppInfo selectedAppForProcess = AppList.SelectedItem as AppInfo;
            ProcessInfo matchingAppProcess = FindProcessForApp(selectedAppForProcess);
            if (DeviceLookupService.IsHarmony(selectedDevice))
            {
                ProcessList.SelectedItem = SelectHarmonyProcessForView(items, current, selectedAppForProcess,
                    _pendingHarmonyLaunchBundleId, _pendingHarmonyLaunchUserId, _pendingHarmonyLaunchAppIndex);
                return;
            }
            if (selected == null && matchingAppProcess != null)
                selected = items.FirstOrDefault(delegate(ProcessInfo process) { return process.Pid == matchingAppProcess.Pid; });
            selected = selected ?? items.FirstOrDefault(delegate(ProcessInfo process) { return process.Recommended; }) ?? items.FirstOrDefault();
            ProcessList.SelectedItem = selected;
        }

        internal static bool IsHarmonyDefaultAppTarget(AppInfo app)
        {
            if (app == null || !DeviceLookupService.IsHarmony(app.Platform)) return true;
            if (app.Recommended) return true;
            if (app.IsSystemApp || app.IsPreInstallApp || IsSystemInstallSource(app.InstallSource))
                return app.HasLaunchEntry && !app.HasNonUiLaunchEntry;
            if (app.IsProcessOnly) return false;
            if (app.HasLaunchEntry && !app.HasNonUiLaunchEntry) return true;
            if (app.IsRunning || app.ProcessPid > 0) return true;
            // A record made only from a non-UI Ability is a service/extension
            // target. It remains available through "显示全部目标" but should
            // not be the first thing a beginner sees.
            if (app.HasNonUiLaunchEntry && !app.HasLaunchEntry) return false;
            // The device may omit launch metadata for a real user app. Keep
            // such rows visible because the user can still select the app or
            // search its Bundle; only explicit system evidence is filtered by
            // default.
            return true;
        }

        internal static bool IsHarmonyDefaultProcessTarget(ProcessInfo process, IEnumerable<AppInfo> apps)
        {
            if (process == null || !ProcessTargetMatcher.IsValidTarget(process)) return false;
            if (!DeviceLookupService.IsHarmony(process.Platform)) return true;
            if (process.Recommended || process.ForegroundApplication) return true;
            AppInfo owner = FindHarmonyAppForProcess(apps, process);
            return owner != null && IsHarmonyDefaultAppTarget(owner);
        }

        private static bool IsSystemInstallSource(string source)
        {
            string normalized = (source ?? "").Trim().Replace("_", "-").ToLowerInvariant();
            return normalized == "system" || normalized == "system-app" || normalized == "systemapp"
                || normalized == "pre-installed" || normalized == "preinstall" || normalized == "pre-installed-app";
        }

        internal static ProcessInfo SelectHarmonyProcessForView(
            IEnumerable<ProcessInfo> processes, ProcessInfo current, AppInfo app,
            string pendingBundleId = "", int pendingUserId = -1, int pendingAppIndex = -1)
        {
            var items = (processes ?? Enumerable.Empty<ProcessInfo>())
                .Where(ProcessTargetMatcher.IsValidTarget).ToList();
            if (!string.IsNullOrWhiteSpace(pendingBundleId))
                return FindHarmonyProcessForBundle(items, pendingBundleId, pendingUserId, pendingAppIndex);
            ProcessInfo preserved = items.FirstOrDefault(process => SameProcessSelection(process, current));
            if (preserved != null && (app == null || MatchesHarmonyAppProcess(app, preserved))) return preserved;
            if (current != null && items.Any(process => process.Pid == current.Pid && !SameProcessSelection(process, current)))
                return null;
            if (app != null) return FindHarmonyProcessForApp(items, app);
            return items.FirstOrDefault(process => process.Recommended) ?? items.FirstOrDefault();
        }

        private void Confirm(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            DeviceInfo device = DeviceList.SelectedItem as DeviceInfo;
            ProcessInfo process = ProcessList.SelectedItem as ProcessInfo;
            AppInfo app = AppList.SelectedItem as AppInfo;
            if (_launchInProgress || _targetsLoading)
            {
                StatusText.Text = "正在读取或匹配目标进程，请稍候。";
                return;
            }
            if (DeviceLookupService.IsHarmony(device))
            {
                process = ResolveHarmonyConfirmedProcess(_launchMode, app, process, _processes);
                app = ResolveHarmonyConfirmedApp(_launchMode, app, process, _apps);
                if (_launchMode && process == null)
                {
                    StatusText.Text = "当前 APP 暂未检测到唯一匹配进程，请启动 APP 后刷新，或在进程页手动选择真实 PID。";
                    return;
                }
            }
            else
                app = app ?? FindAppForProcess(process);
            if (device == null || !ProcessTargetMatcher.IsValidTarget(process))
            {
                StatusText.Text = "当前进程 PID 无效，请刷新进程列表后重新选择。";
                return;
            }
            Close(new DeviceSelection(device, app, process, AutoStartCheckBox.IsChecked == true));
        }

        internal static bool ShouldAutoSelectFallbackProcess(
            bool isHarmony,
            bool preserveHarmonyAppPreference,
            AppInfo selectedApp,
            ProcessInfo selectedProcess)
        {
            return selectedProcess == null
                && (!isHarmony || (!preserveHarmonyAppPreference && selectedApp == null));
        }

        internal static ProcessInfo ResolveHarmonyConfirmedProcess(
            bool launchMode, AppInfo selectedApp, ProcessInfo selectedProcess, IEnumerable<ProcessInfo> processes)
        {
            var items = (processes ?? Enumerable.Empty<ProcessInfo>()).Where(ProcessTargetMatcher.IsValidTarget).ToList();
            return launchMode
                ? FindHarmonyProcessForApp(items, selectedApp)
                : items.FirstOrDefault(process => SameProcessSelection(process, selectedProcess));
        }

        internal static AppInfo ResolveHarmonyConfirmedApp(
            bool launchMode,
            AppInfo selectedApp,
            ProcessInfo process,
            IEnumerable<AppInfo> apps)
        {
            return launchMode ? selectedApp : FindHarmonyAppForProcess(apps, process);
        }

        internal static bool CanFinalizeHarmonyLaunchRefresh(
            bool refreshSucceeded,
            bool refreshIsCurrent,
            string preferredAppBundleId)
        {
            // LoadTargetsAsync clears the preferred Bundle only after it has
            // applied a complete current snapshot. A cancelled or failed
            // refresh must stay out of process mode and let the caller clear
            // the stale launch preference in finally.
            return refreshSucceeded
                && refreshIsCurrent
                && string.IsNullOrWhiteSpace(preferredAppBundleId);
        }

        private void Cancel(object sender, Avalonia.Interactivity.RoutedEventArgs e) { Close(null); }
        private AppInfo FindInitialApp() { return _initialSelection == null || _initialSelection.App == null ? null : _apps.FirstOrDefault(delegate(AppInfo app) { return SameAppSelection(app, _initialSelection.App); }); }
        private ProcessInfo FindInitialProcess()
        {
            if (_initialSelection == null || _initialSelection.Process == null) return null;
            return _processes.FirstOrDefault(delegate(ProcessInfo process)
            {
                return SameProcessSelection(process, _initialSelection.Process);
            });
        }
        private CancellationToken NewLoadToken(out long generation)
        {
            CancelLoad();
            generation = _loadGeneration;
            _loadCancellation = new CancellationTokenSource();
            return _loadCancellation.Token;
        }
        private void CancelLoad()
        {
            _loadGeneration++;
            if (_loadCancellation == null) return;
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
            _loadCancellation = null;
        }
        private bool IsCurrentLoad(long generation, string expectedUdid = null)
        {
            if (_closed || generation != _loadGeneration) return false;
            if (expectedUdid == null) return true;
            DeviceInfo current = DeviceList == null ? null : DeviceList.SelectedItem as DeviceInfo;
            return current != null && string.Equals(current.Udid ?? "", expectedUdid ?? "", StringComparison.OrdinalIgnoreCase);
        }
        private void CancelDriverDownload()
        {
            if (_driverDownloadCancellation == null) return;
            _driverDownloadCancellation.Cancel();
        }
        private void ClearPendingHarmonyLaunchTarget()
        {
            _pendingHarmonyLaunchBundleId = "";
            _pendingHarmonyLaunchUserId = -1;
            _pendingHarmonyLaunchAppIndex = -1;
            _preferredAppBundleId = "";
            _preferredHarmonyUserId = -1;
            _preferredHarmonyAppIndex = -1;
            _launchTargetDeviceUdid = "";
        }
        private static bool Contains(string value, string query) { return (value ?? "").IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0; }

        private void SetLaunchMode(bool launchMode)
        {
            _launchMode = launchMode;
            if (ProcessPanel != null) ProcessPanel.IsVisible = !launchMode;
            if (LaunchPanel != null) LaunchPanel.IsVisible = launchMode;
            if (ProcessTab != null) ProcessTab.Classes.Set("activeDialogTab", !launchMode);
            if (LaunchTab != null) LaunchTab.Classes.Set("activeDialogTab", launchMode);
            RunSelectionSync(delegate
            {
                if (launchMode) ApplyAppFilter();
                else ApplyProcessFilter();
            });
        }

        private void RunSelectionSync(Action action)
        {
            bool previous = _synchronizingSelections;
            _synchronizingSelections = true;
            try { action(); }
            finally { _synchronizingSelections = previous; }
        }

        private AppInfo FindAppForProcess(ProcessInfo process)
        {
            if (process == null) return null;
            if (DeviceLookupService.IsHarmony(process.Platform))
                return FindHarmonyAppForProcess(_apps, process);
            string bundle = FirstNonEmpty(process.BundleId, process.OwnerBundleId);
            AppInfo pidMatch = _apps.FirstOrDefault(delegate(AppInfo app)
            {
                if (app == null) return false;
                if (app.ProcessPid == process.Pid
                    && (string.IsNullOrWhiteSpace(app.ProcessName)
                        || string.Equals(app.ProcessName, process.Name, StringComparison.OrdinalIgnoreCase))) return true;
                return false;
            });
            if (pidMatch != null) return pidMatch;
            List<AppInfo> bundleMatches = _apps.Where(delegate(AppInfo app)
            {
                return app != null && !string.IsNullOrWhiteSpace(bundle)
                    && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            return bundleMatches.Count == 1 ? bundleMatches[0] : null;
        }

        internal static AppInfo FindHarmonyAppForProcess(IEnumerable<AppInfo> apps, ProcessInfo process)
        {
            List<AppInfo> matches = (apps ?? Enumerable.Empty<AppInfo>())
                .Where(app => MatchesHarmonyAppProcess(app, process))
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        internal static bool MatchesHarmonyAppProcess(AppInfo app, ProcessInfo process)
        {
            if (app == null || !ProcessTargetMatcher.IsValidTarget(process)
                || !DeviceLookupService.IsHarmony(app.Platform)
                || !DeviceLookupService.IsHarmony(process.Platform)
                || app.HarmonyUserId != process.HarmonyUserId
                || app.HarmonyAppIndex != process.HarmonyAppIndex) return false;
            if (app.ProcessPid > 0 && (app.ProcessPid != process.Pid
                || (!string.IsNullOrWhiteSpace(app.ProcessName)
                    && !string.Equals(app.ProcessName, process.Name, StringComparison.Ordinal)))) return false;
            if (string.IsNullOrWhiteSpace(app.BundleId)) return app.ProcessPid > 0;
            return string.Equals(app.BundleId, FirstNonEmpty(process.BundleId, process.OwnerBundleId), StringComparison.OrdinalIgnoreCase);
        }

        private AppInfo FindAppByBundle(string bundle)
        {
            return FindAppByBundle(bundle, -1);
        }

        private AppInfo FindAppByBundle(string bundle, int harmonyUserId)
        {
            return FindHarmonyAppByBundle(_apps, bundle, harmonyUserId);
        }

        private AppInfo FindAppByBundle(string bundle, int harmonyUserId, int harmonyAppIndex)
        {
            return FindHarmonyAppByBundle(_apps, bundle, harmonyUserId, harmonyAppIndex);
        }

        internal static AppInfo FindHarmonyAppByBundle(
            IEnumerable<AppInfo> apps,
            string bundle,
            int harmonyUserId,
            int harmonyAppIndex = -1)
        {
            if (string.IsNullOrWhiteSpace(bundle)) return null;
            List<AppInfo> matches = (apps ?? Enumerable.Empty<AppInfo>())
                .Where(delegate(AppInfo app)
                {
                    return app != null && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase);
                })
                .Where(delegate(AppInfo app) { return app.HarmonyAppIndex == harmonyAppIndex; })
                .ToList();
            if (harmonyUserId >= 0)
            {
                // A requested profile is part of the target identity. If it
                // is no longer present after refresh, leave the selection
                // unresolved instead of silently switching profiles.
                return matches.FirstOrDefault(delegate(AppInfo app) { return app.HarmonyUserId == harmonyUserId; });
            }
            if (matches.Any(app => DeviceLookupService.IsHarmony(app.Platform)))
            {
                var unknown = matches.Where(app => app.HarmonyUserId < 0).ToList();
                return unknown.Count == 1 ? unknown[0] : null;
            }
            return matches.FirstOrDefault();
        }

        private ProcessInfo FindProcessForApp(AppInfo app)
        {
            if (app == null) return null;
            ProcessInfo restored = _initialSelection == null ? null : _initialSelection.Process;
            bool restoresSameHarmonyTarget = DeviceLookupService.IsHarmony(app.Platform)
                && restored != null
                && restored.Pid == app.ProcessPid
                && (_initialSelection.App == null || SameAppSelection(app, _initialSelection.App));
            if (DeviceLookupService.IsHarmony(app.Platform))
            {
                ProcessInfo match = FindHarmonyProcessForApp(_processes, app);
                return !restoresSameHarmonyTarget || ProcessTargetMatcher.SameHarmonyProcessInstance(match, restored)
                    ? match : null;
            }
            if (app.ProcessPid > 0)
            {
                ProcessInfo processOnly = _processes.FirstOrDefault(delegate(ProcessInfo process)
                {
                    return ProcessTargetMatcher.IsValidTarget(process)
                        && process.Pid == app.ProcessPid
                        && (string.IsNullOrWhiteSpace(app.ProcessName)
                            || string.Equals(process.Name, app.ProcessName, StringComparison.OrdinalIgnoreCase));
                });
                if (processOnly != null)
                    return processOnly;
            }
            if (string.IsNullOrWhiteSpace(app.BundleId)) return null;
            return FindProcessForBundle(_processes, app.BundleId);
        }

        private static bool SameProcessSelection(ProcessInfo current, ProcessInfo selected)
        {
            if (current == null || selected == null || current.Pid != selected.Pid) return false;
            if (DeviceLookupService.IsHarmony(current.Platform) || DeviceLookupService.IsHarmony(selected.Platform))
                return ProcessTargetMatcher.SameHarmonyProcessInstance(current, selected);
            return string.IsNullOrWhiteSpace(selected.Name)
                || string.Equals(current.Name, selected.Name, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool SameAppSelection(AppInfo left, AppInfo right)
        {
            if (left == null || right == null) return false;
            if ((DeviceLookupService.IsHarmony(left.Platform) || DeviceLookupService.IsHarmony(right.Platform))
                && (left.HarmonyUserId != right.HarmonyUserId
                    || left.HarmonyAppIndex != right.HarmonyAppIndex
                    || !string.Equals(left.BundleId, right.BundleId, StringComparison.OrdinalIgnoreCase))) return false;
            if (left.ProcessPid > 0 || right.ProcessPid > 0)
                return left.ProcessPid > 0 && left.ProcessPid == right.ProcessPid
                    && (string.IsNullOrWhiteSpace(left.ProcessName) || string.IsNullOrWhiteSpace(right.ProcessName)
                        || string.Equals(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(left.BundleId) || string.IsNullOrWhiteSpace(right.BundleId)
                || !string.Equals(left.BundleId, right.BundleId, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static ProcessInfo FindProcessForBundle(IEnumerable<ProcessInfo> processes, string bundle)
        {
            if (string.IsNullOrWhiteSpace(bundle)) return null;
            return (processes ?? Enumerable.Empty<ProcessInfo>())
                .Where(ProcessTargetMatcher.IsValidTarget)
                .Where(delegate(ProcessInfo process)
                {
                    return string.Equals(process.BundleId, bundle, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(process.OwnerBundleId, bundle, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(delegate(ProcessInfo process) { return string.Equals(process.Name, bundle, StringComparison.OrdinalIgnoreCase); })
                .ThenByDescending(delegate(ProcessInfo process) { return process.Recommended; })
                .FirstOrDefault();
        }

        internal static ProcessInfo FindHarmonyProcessForBundle(IEnumerable<ProcessInfo> processes, string bundle)
        {
            return FindHarmonyProcessForBundle(processes, bundle, -1);
        }

        internal static ProcessInfo FindHarmonyProcessForApp(IEnumerable<ProcessInfo> processes, AppInfo app)
        {
            if (app == null) return null;
            var matches = (processes ?? Enumerable.Empty<ProcessInfo>())
                .Where(process => MatchesHarmonyAppProcess(app, process)).ToList();
            if (app.ProcessPid > 0) return matches.Count == 1 ? matches[0] : null;
            return FindHarmonyProcessForBundle(matches, app.BundleId, app.HarmonyUserId, app.HarmonyAppIndex);
        }

        internal static ProcessInfo FindHarmonyProcessForBundle(
            IEnumerable<ProcessInfo> processes,
            string bundle,
            int harmonyUserId,
            int harmonyAppIndex = -1)
        {
            if (string.IsNullOrWhiteSpace(bundle)) return null;
            List<ProcessInfo> matches = (processes ?? Enumerable.Empty<ProcessInfo>())
                .Where(ProcessTargetMatcher.IsValidTarget)
                .Where(delegate(ProcessInfo process)
                {
                    return DeviceLookupService.IsHarmony(process.Platform)
                        && process.HarmonyUserId == harmonyUserId
                        && process.HarmonyAppIndex == harmonyAppIndex
                        && (string.Equals(process.BundleId, bundle, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(process.OwnerBundleId, bundle, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
            if (matches.Count == 0) return null;

            // A plain Bundle match is safe only when it identifies the main
            // process, a unique recommended process, or the sole candidate.
            // Worker/render/service processes otherwise need an explicit user
            // selection to avoid collecting the wrong PID.
            List<ProcessInfo> exact = matches.Where(delegate(ProcessInfo process)
            {
                return string.Equals(process.Name, bundle, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            if (exact.Count == 1) return exact[0];
            List<ProcessInfo> recommended = matches.Where(delegate(ProcessInfo process) { return process.Recommended; }).ToList();
            if (recommended.Count == 1) return recommended[0];
            return matches.Count == 1 ? matches[0] : null;
        }

        private Task<HarmonyTargetInventory> WaitForHarmonyProcessAsync(
            DeviceInfo device,
            HarmonyTargetInventory initial,
            CancellationToken token,
            long generation,
            string deviceUdid,
            string bundleId,
            int harmonyUserId,
            int harmonyAppIndex)
        {
            return WaitForHarmonyProcessAsync(initial, bundleId, harmonyUserId,
                readToken => _lookup.ListTargetsAsync(device, readToken),
                () => IsCurrentLoad(generation, deviceUdid), token,
                harmonyAppIndex: harmonyAppIndex);
        }

        internal static async Task<HarmonyTargetInventory> WaitForHarmonyProcessAsync(
            HarmonyTargetInventory initial,
            string bundleId,
            int harmonyUserId,
            Func<CancellationToken, Task<HarmonyTargetInventory>> readSnapshot,
            Func<bool> isCurrent,
            CancellationToken token,
            int maxWaitMs = 20000,
            int retryDelayMs = 500,
            int harmonyAppIndex = -1)
        {
            if (readSnapshot == null) throw new ArgumentNullException(nameof(readSnapshot));
            if (isCurrent == null) throw new ArgumentNullException(nameof(isCurrent));
            if (maxWaitMs <= 0) throw new ArgumentOutOfRangeException(nameof(maxWaitMs));
            if (retryDelayMs < 0) throw new ArgumentOutOfRangeException(nameof(retryDelayMs));
            token.ThrowIfCancellationRequested();
            if (!isCurrent()) return null;

            HarmonyTargetInventory initialSnapshot = initial ?? new HarmonyTargetInventory();
            List<ProcessInfo> initialProcesses = initialSnapshot.Processes ?? new List<ProcessInfo>();
            if (FindHarmonyProcessForBundle(initialProcesses, bundleId, harmonyUserId, harmonyAppIndex) != null)
                return initialSnapshot;

            HarmonyTargetInventory latestSnapshot = null;
            Stopwatch stopwatch = Stopwatch.StartNew();
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(maxWaitMs);
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!isCurrent()) return null;
                    if (stopwatch.ElapsedMilliseconds >= maxWaitMs)
                        return TimedOutHarmonySnapshot(latestSnapshot ?? initialSnapshot);

                    try
                    {
                        await Task.Delay(retryDelayMs, deadline.Token).ConfigureAwait(true);
                        token.ThrowIfCancellationRequested();
                        if (!isCurrent()) return null;
                        deadline.Token.ThrowIfCancellationRequested();
                        HarmonyTargetInventory candidate = await readSnapshot(deadline.Token).ConfigureAwait(true);
                        token.ThrowIfCancellationRequested();
                        if (!isCurrent()) return null;
                        if (deadline.IsCancellationRequested || stopwatch.ElapsedMilliseconds >= maxWaitMs)
                            return TimedOutHarmonySnapshot(latestSnapshot ?? initialSnapshot);
                        latestSnapshot = candidate ?? EmptyHarmonySnapshot("鸿蒙进程刷新没有返回有效快照，请刷新后重试。", latestSnapshot ?? initialSnapshot);
                        List<ProcessInfo> latest = latestSnapshot.Processes ?? new List<ProcessInfo>();
                        if (FindHarmonyProcessForBundle(latest, bundleId, harmonyUserId, harmonyAppIndex) != null)
                            return latestSnapshot;
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!isCurrent()) return null;
                        return TimedOutHarmonySnapshot(latestSnapshot ?? initialSnapshot);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!isCurrent()) return null;
                        latestSnapshot = EmptyHarmonySnapshot("刷新鸿蒙进程失败：" + FirstNonEmptyLine(exception.Message), latestSnapshot ?? initialSnapshot);
                    }

                    if (stopwatch.ElapsedMilliseconds >= maxWaitMs)
                        return TimedOutHarmonySnapshot(latestSnapshot ?? initialSnapshot);
                }
            }
            token.ThrowIfCancellationRequested();
            return isCurrent() ? latestSnapshot ?? TimedOutHarmonySnapshot(initialSnapshot) : null;
        }

        private static HarmonyTargetInventory EmptyHarmonySnapshot(string diagnostic, HarmonyTargetInventory previous)
        {
            string message = (diagnostic ?? "鸿蒙进程刷新失败。").Trim();
            return new HarmonyTargetInventory
            {
                ProcessInventoryError = message.Length > 240 ? message.Substring(0, 240) + "..." : message,
                UserInventoryError = previous == null ? "" : previous.UserInventoryError ?? ""
            };
        }

        private static HarmonyTargetInventory TimedOutHarmonySnapshot(HarmonyTargetInventory previous)
        {
            return EmptyHarmonySnapshot("等待鸿蒙目标进程超时，请刷新进程列表后重试。", previous);
        }

        private void UpdateSelectedAppSummary(AppInfo app)
        {
            if (SelectedAppName == null) return;
            SelectedAppName.Text = app == null ? "请选择 APP" : FirstNonEmpty(app.Name, app.BundleId);
            SelectedAppBundle.Text = app == null ? "" : app.PickerTargetIdentifier;
            ToolTip.SetTip(SelectedAppName, SelectedAppName.Text);
            ToolTip.SetTip(SelectedAppBundle, SelectedAppBundle.Text);
            string glyph = AppIconGlyph(app == null ? "" : app.IconKey);
            SelectedAppGlyph.Text = glyph;
            bool hasIcon = app != null && !string.IsNullOrWhiteSpace(app.IconPath) && File.Exists(app.IconPath);
            SelectedAppIcon.Source = null;
            if (hasIcon)
            {
                try
                {
                    SelectedAppIcon.Source = _iconConverter == null ? null : _iconConverter.Get(app.IconPath);
                    SelectedAppGlyph.IsVisible = false;
                }
                catch { SelectedAppGlyph.IsVisible = true; }
            }
            else
            {
                SelectedAppGlyph.IsVisible = true;
            }
        }

        internal static void MarkHarmonyPickerUserScopeVisibility(
            IEnumerable<AppInfo> apps,
            IEnumerable<ProcessInfo> processes)
        {
            MarkHarmonyAppUserScopeVisibility(apps);
            MarkHarmonyProcessUserScopeVisibility(processes);
        }

        private static void MarkHarmonyAppUserScopeVisibility(IEnumerable<AppInfo> apps)
        {
            List<AppInfo> items = (apps ?? Enumerable.Empty<AppInfo>())
                .Where(item => item != null
                    && DevicePlatformNames.IsHarmony(item.Platform)
                    && !string.IsNullOrWhiteSpace(item.BundleId))
                .ToList();
            foreach (IGrouping<string, AppInfo> group in items.GroupBy(HarmonyPickerScopeKey, StringComparer.OrdinalIgnoreCase))
            {
                bool show = group.Select(item => item.HarmonyUserId).Distinct().Count() > 1;
                foreach (AppInfo item in group) item.ShowHarmonyUserScope = show;
            }
        }

        private static void MarkHarmonyProcessUserScopeVisibility(IEnumerable<ProcessInfo> processes)
        {
            List<ProcessInfo> items = (processes ?? Enumerable.Empty<ProcessInfo>())
                .Where(item => item != null
                    && DevicePlatformNames.IsHarmony(item.Platform)
                    && !string.IsNullOrWhiteSpace(item.BundleId))
                .ToList();
            foreach (IGrouping<string, ProcessInfo> group in items.GroupBy(HarmonyPickerScopeKey, StringComparer.OrdinalIgnoreCase))
            {
                bool show = group.Select(item => item.HarmonyUserId).Distinct().Count() > 1;
                foreach (ProcessInfo item in group) item.ShowHarmonyUserScope = show;
            }
        }

        private static string HarmonyPickerScopeKey(AppInfo item)
        {
            return (item.BundleId ?? "") + "\u001F" + item.HarmonyAppIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string HarmonyPickerScopeKey(ProcessInfo item)
        {
            return (item.BundleId ?? "") + "\u001F" + item.HarmonyAppIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task HydrateIconsAndRefreshAsync(
            DeviceInfo device,
            IList<AppInfo> apps,
            IList<ProcessInfo> processes,
            CancellationToken token,
            long generation,
            string deviceUdid)
        {
            try
            {
                await _lookup.HydrateAppIconsAsync(device, apps, processes, 48, token);
                if (!IsCurrentLoad(generation, deviceUdid)) return;
                await Dispatcher.UIThread.InvokeAsync(delegate
                {
                    if (!IsCurrentLoad(generation, deviceUdid)) return;
                    AppInfo selectedApp = AppList.SelectedItem as AppInfo;
                    ProcessInfo selectedProcess = ProcessList.SelectedItem as ProcessInfo;
                    RunSelectionSync(delegate
                    {
                        ApplyAppFilter();
                        if (!_launchMode) ApplyProcessFilter();
                        if (selectedApp != null) AppList.SelectedItem = _apps.FirstOrDefault(delegate(AppInfo app) { return SameAppSelection(app, selectedApp); });
                        if (!_launchMode && selectedProcess != null) ProcessList.SelectedItem = _processes.FirstOrDefault(process => SameProcessSelection(process, selectedProcess));
                    });
                    AppInfo summaryApp = _launchMode
                        ? AppList.SelectedItem as AppInfo
                        : FindAppForProcess(ProcessList.SelectedItem as ProcessInfo) ?? AppList.SelectedItem as AppInfo;
                    UpdateSelectedAppSummary(summaryApp);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) await Dispatcher.UIThread.InvokeAsync(delegate { StatusText.Text = "图标读取失败，已保留文字列表：" + ex.Message; });
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values) if (!string.IsNullOrWhiteSpace(value)) return value;
            return "";
        }

        private static string AppIconGlyph(string key)
        {
            string icon = (key ?? "").ToLowerInvariant();
            if (icon == "wechat") return "微";
            if (icon == "tencent") return "T";
            if (icon == "android") return "A";
            return "•";
        }
        private static string FirstNonEmptyLine(params string[] values)
        {
            foreach (string value in values)
            {
                string line = (value ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            }
            return "未知错误";
        }
    }

    public sealed class IconPathConverter : Avalonia.Data.Converters.IValueConverter, IDisposable
    {
        private readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string path = value as string;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
            try { return Get(path); }
            catch { return null; }
        }

        internal Bitmap Get(string path)
        {
            string fullPath = Path.GetFullPath(path ?? "");
            Bitmap bitmap;
            if (_cache.TryGetValue(fullPath, out bitmap)) return bitmap;
            bitmap = new Bitmap(fullPath);
            _cache[fullPath] = bitmap;
            return bitmap;
        }

        public void Dispose()
        {
            foreach (Bitmap bitmap in _cache.Values) bitmap.Dispose();
            _cache.Clear();
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }
    }

    public sealed class IconGlyphConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string icon = (value as string ?? "").ToLowerInvariant();
            if (icon == "wechat") return "微";
            if (icon == "tencent") return "T";
            if (icon == "android") return "A";
            return "•";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }
    }

    public sealed class IconPathVisibilityConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool exists = value is string path && !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            return string.Equals(parameter as string, "fallback", StringComparison.OrdinalIgnoreCase) ? !exists : exists;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }
    }

    public sealed class EmptyTextVisibilityConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool empty = string.IsNullOrWhiteSpace(value as string);
            return string.Equals(parameter as string, "notEmpty", StringComparison.OrdinalIgnoreCase) ? !empty : empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }
    }
}
