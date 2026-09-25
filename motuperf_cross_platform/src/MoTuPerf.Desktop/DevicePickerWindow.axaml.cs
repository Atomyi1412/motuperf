using System;
using System.Collections.Generic;
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
        private Button DeviceDiagnosticsButton;
        private TextBlock AndroidStatusText;
        private TextBlock IosStatusText;
        private TextBlock HarmonyStatusText;
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
        private Grid ProcessPanel;
        private Grid LaunchPanel;
        private CheckBox AutoStartCheckBox;
        private bool _launchMode;
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
        private bool _closed;
        private IconPathConverter _iconConverter;
        private string _appleDriverButtonIdleText = "下载苹果设备驱动";
        private string _deviceCountStatus = "正在检测设备...";
        private DeviceDiagnosticsSnapshot _deviceDiagnostics = DeviceDiagnosticsFormatter.Loading();
        private string _loadedDeviceUdid = "";
        private string _preferredAppBundleId = "";
        private int _preferredHarmonyUserId = -1;

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
            DeviceDiagnosticsButton = this.FindControl<Button>("DeviceDiagnosticsButton");
            AndroidStatusText = this.FindControl<TextBlock>("AndroidStatusText");
            IosStatusText = this.FindControl<TextBlock>("IosStatusText");
            HarmonyStatusText = this.FindControl<TextBlock>("HarmonyStatusText");
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
            try
            {
                DeviceDiscoveryReport report = await _lookup.DiscoverDevicesAsync(token);
                if (!IsCurrentLoad(generation)) return;
                DeviceList.ItemsSource = report.Devices;
                ApplyDeviceDiagnostics(DeviceDiagnosticsFormatter.FromReport(report));
                AppleDriverButton.IsVisible = report.AppleDriverActionAvailable;
                _appleDriverButtonIdleText = report.AppleDriverMissing ? "下载苹果设备驱动" : "修复苹果设备驱动";
                AppleDriverButton.Content = _appleDriverButtonIdleText;
                DeviceInfo preferred = report.Devices.FirstOrDefault(delegate(DeviceInfo device)
                {
                    return _initialSelection != null && _initialSelection.Device != null && device.Udid == _initialSelection.Device.Udid;
                }) ?? report.Devices.FirstOrDefault(delegate(DeviceInfo device) { return device.Recommended; }) ?? report.Devices.FirstOrDefault();
                DeviceList.SelectedItem = preferred;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ApplyDeviceDiagnostics(DeviceDiagnosticsFormatter.Error("设备检测失败：" + ex.Message, ex.Message, ex.Message));
                AppleDriverButton.IsVisible = false;
            }
        }

        private void ApplyDeviceDiagnostics(DeviceDiagnosticsSnapshot snapshot)
        {
            _deviceDiagnostics = snapshot ?? DeviceDiagnosticsFormatter.Loading();
            _deviceCountStatus = _deviceDiagnostics.OverallStatus;
            DeviceCountText.Text = _deviceDiagnostics.OverallStatus;
            AndroidStatusText.Text = _deviceDiagnostics.AndroidSummary;
            IosStatusText.Text = _deviceDiagnostics.IosSummary;
            HarmonyStatusText.Text = _deviceDiagnostics.HarmonySummary;
            DeviceDiagnosticsButton.IsEnabled = _deviceDiagnostics.IsReady;
        }

        private async void ShowDeviceDiagnostics(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (!_deviceDiagnostics.IsReady) return;
            DeviceDiagnosticsSnapshot snapshot = _deviceDiagnostics;
            await new DeviceDiagnosticsWindow(snapshot).ShowDialog(this);
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
            UpdateSelectedAppSummary(app);
            if (!_launchMode) RunSelectionSync(ApplyProcessFilter);
        }

        private void ProcessSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_synchronizingSelections || _launchMode) return;
            ProcessInfo process = ProcessList == null ? null : ProcessList.SelectedItem as ProcessInfo;
            if (process == null) return;
            AppInfo app = FindAppForProcess(process);
            RunSelectionSync(delegate
            {
                if (app != null && !ReferenceEquals(AppList.SelectedItem, app)) AppList.SelectedItem = app;
            });
            UpdateSelectedAppSummary(app);
        }

        private void ShowProcessTab(object sender, Avalonia.Interactivity.RoutedEventArgs e) { SetLaunchMode(false); }
        private void ShowLaunchTab(object sender, Avalonia.Interactivity.RoutedEventArgs e) { SetLaunchMode(true); }
        private async void RefreshTargetLists(object sender, Avalonia.Interactivity.RoutedEventArgs e) { await LoadTargetsAsync(); }

        private async Task LoadTargetsAsync()
        {
            if (_closed) return;
            DeviceInfo device = DeviceList.SelectedItem as DeviceInfo;
            if (device == null)
            {
                CancelLoad();
                return;
            }
            string deviceUdid = device.Udid ?? "";
            AppInfo previousApp = string.Equals(_loadedDeviceUdid, deviceUdid, StringComparison.OrdinalIgnoreCase)
                ? AppList.SelectedItem as AppInfo
                : null;
            ProcessInfo previousProcess = string.Equals(_loadedDeviceUdid, deviceUdid, StringComparison.OrdinalIgnoreCase)
                ? ProcessList.SelectedItem as ProcessInfo
                : null;
            long generation;
            CancellationToken token = NewLoadToken(out generation);
            StatusText.Text = "正在读取 " + device.PickerLabel + " 的 APP 和进程...";
            AppList.ItemsSource = null;
            ProcessList.ItemsSource = null;
            try
            {
                List<AppInfo> loadedApps = null;
                List<ProcessInfo> loadedProcesses = null;
                Exception appsError = null;
                Exception processesError = null;
                string harmonyProcessInventoryError = "";
                if (DeviceLookupService.IsHarmony(device))
                {
                    try
                    {
                        HarmonyTargetInventory snapshot = await _lookup.ListTargetsAsync(device, token);
                        loadedApps = snapshot == null ? new List<AppInfo>() : snapshot.Apps;
                        loadedProcesses = snapshot == null ? new List<ProcessInfo>() : snapshot.Processes;
                        harmonyProcessInventoryError = snapshot == null ? "" : snapshot.ProcessInventoryError ?? "";
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
                if (!IsCurrentLoad(generation, deviceUdid)) return;
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
                if (DeviceLookupService.IsHarmony(device) && !string.IsNullOrWhiteSpace(_preferredAppBundleId))
                {
                    HarmonyTargetInventory latest = await WaitForHarmonyProcessAsync(
                        device,
                        new HarmonyTargetInventory
                        {
                            Apps = loadedApps,
                            Processes = loadedProcesses
                        },
                        loadedProcesses,
                        token,
                        generation,
                        deviceUdid,
                        _preferredHarmonyUserId);
                    if (latest != null)
                    {
                        loadedApps = latest.Apps ?? new List<AppInfo>();
                        loadedProcesses = latest.Processes ?? new List<ProcessInfo>();
                        harmonyProcessInventoryError = latest.ProcessInventoryError ?? "";
                    }
                }
                _apps.Clear();
                _apps.AddRange(loadedApps);
                _processes.Clear();
                _processes.AddRange(loadedProcesses);
                bool preserveHarmonyAppPreference = DeviceLookupService.IsHarmony(device)
                    && !string.IsNullOrWhiteSpace(_preferredAppBundleId);
                AppInfo selectedApp = FindAppByBundle(_preferredAppBundleId, _preferredHarmonyUserId);
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
                if (selectedProcess == null && (!DeviceLookupService.IsHarmony(device) || selectedApp == null))
                {
                    selectedProcess = FindInitialProcess()
                        ?? (previousProcess == null ? null : _processes.FirstOrDefault(delegate(ProcessInfo process)
                        {
                            return SameProcessSelection(process, previousProcess);
                        }))
                        ?? _processes.FirstOrDefault(delegate(ProcessInfo process) { return process.Recommended; })
                        ?? _processes.FirstOrDefault();
                }
                RunSelectionSync(delegate
                {
                    ApplyAppFilter();
                    AppList.SelectedItem = selectedApp;
                    ApplyProcessFilter();
                    ProcessList.SelectedItem = selectedProcess;
                });
                UpdateSelectedAppSummary(selectedApp ?? FindAppForProcess(selectedProcess));
                bool launchProcessMissing = DeviceLookupService.IsHarmony(device)
                    && !string.IsNullOrWhiteSpace(_preferredAppBundleId)
                    && FindHarmonyProcessForBundle(loadedProcesses, _preferredAppBundleId, _preferredHarmonyUserId) == null;
                StatusText.Text = launchProcessMissing
                    ? "APP 已启动，但暂未检测到匹配进程，请稍后刷新进程列表。"
                    : appsError != null
                        ? "应用清单暂不可用，已保留真实进程列表，请直接选择目标 PID。"
                        : processesError == null && string.IsNullOrWhiteSpace(harmonyProcessInventoryError)
                        ? _deviceCountStatus
                        : "应用列表已读取，但进程列表暂不可用"
                            + (string.IsNullOrWhiteSpace(harmonyProcessInventoryError) ? "，请刷新或检查 HDC 进程权限。" : "：" + harmonyProcessInventoryError + "。请刷新或检查 HDC 进程权限。");
                _loadedDeviceUdid = deviceUdid;
                _preferredAppBundleId = "";
                _preferredHarmonyUserId = -1;
                _ = HydrateIconsAndRefreshAsync(
                    device,
                    _apps.ToArray(),
                    _processes.ToArray(),
                    token,
                    generation,
                    deviceUdid);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StatusText.Text = "读取 APP/进程失败：" + ex.Message; }
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
                        : "bundle:" + bundle + ":user:" + process.HarmonyUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
                    : "bundle:" + bundle + ":user:" + process.HarmonyUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
                    HarmonyUserId = process.HarmonyUserId
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
            if (_launchInProgress || _driverDownloadInProgress) return;
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
                _preferredAppBundleId = app.BundleId ?? "";
                _preferredHarmonyUserId = app.HarmonyUserId;
                long launchGeneration;
                CancellationToken token = NewLoadToken(out launchGeneration);
                ProcessResult result = await _lookup.LaunchAppAsync(device, app, token);
                if (result.ExitCode != 0)
                {
                    _preferredAppBundleId = "";
                    if (DeviceLookupService.IsHarmony(device))
                    {
                        HarmonyRefreshResult refreshResult = await RefreshHarmonyProcessesAfterLaunchFailureAsync(device, app, token, launchGeneration);
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
                StatusText.Text = "APP 已启动，正在刷新进程...";
                await Task.Delay(1200);
                await LoadTargetsAsync();
                SetLaunchMode(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StatusText.Text = "启动 APP 失败：" + ex.Message; }
            finally
            {
                _launchInProgress = false;
                _preferredHarmonyUserId = -1;
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
            AppInfo refreshedApp = FindAppByBundle(app == null ? "" : app.BundleId, app == null ? -1 : app.HarmonyUserId) ?? app;
            RunSelectionSync(delegate
            {
                ApplyAppFilter();
                AppList.SelectedItem = refreshedApp;
                ApplyProcessFilter();
            });
            return TrySelectHarmonyRunningProcess(device, refreshedApp)
                ? HarmonyRefreshResult.Selected
                : HarmonyRefreshResult.Refreshed;
        }

        private void AppSearchChanged(object sender, TextChangedEventArgs e) { ApplyAppFilter(); }
        private void ProcessSearchChanged(object sender, TextChangedEventArgs e) { ApplyProcessFilter(); }
        private void ApplyAppFilter()
        {
            string query = (AppSearch.Text ?? "").Trim();
            AppInfo current = AppList.SelectedItem as AppInfo;
            List<AppInfo> items = string.IsNullOrWhiteSpace(query) ? _apps.ToList() : _apps.Where(delegate(AppInfo app)
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
            bool harmonyAppSelected = DeviceLookupService.IsHarmony(selectedDevice) && selectedAppForProcess != null;
            if (harmonyAppSelected)
                selected = matchingAppProcess == null
                    ? null
                    : items.FirstOrDefault(delegate(ProcessInfo process) { return process.Pid == matchingAppProcess.Pid; });
            else if (selected == null && matchingAppProcess != null)
                selected = items.FirstOrDefault(delegate(ProcessInfo process) { return process.Pid == matchingAppProcess.Pid; });
            bool keepHarmonyAppWithoutProcess = harmonyAppSelected && matchingAppProcess == null;
            if (!keepHarmonyAppWithoutProcess)
                selected = selected ?? items.FirstOrDefault(delegate(ProcessInfo process) { return process.Recommended; }) ?? items.FirstOrDefault();
            ProcessList.SelectedItem = selected;
        }

        private void Confirm(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            DeviceInfo device = DeviceList.SelectedItem as DeviceInfo;
            ProcessInfo process = ProcessList.SelectedItem as ProcessInfo;
            AppInfo app = AppList.SelectedItem as AppInfo;
            app = app ?? FindAppForProcess(process);
            if (device == null || !ProcessTargetMatcher.IsValidTarget(process))
            {
                StatusText.Text = "当前进程 PID 无效，请刷新进程列表后重新选择。";
                return;
            }
            Close(new DeviceSelection(device, app, process, AutoStartCheckBox.IsChecked == true));
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
        private static bool Contains(string value, string query) { return (value ?? "").IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0; }

        private void SetLaunchMode(bool launchMode)
        {
            _launchMode = launchMode;
            if (ProcessPanel != null) ProcessPanel.IsVisible = !launchMode;
            if (LaunchPanel != null) LaunchPanel.IsVisible = launchMode;
            if (ProcessTab != null) ProcessTab.Classes.Set("activeDialogTab", !launchMode);
            if (LaunchTab != null) LaunchTab.Classes.Set("activeDialogTab", launchMode);
            if (launchMode) ApplyAppFilter();
            else ApplyProcessFilter();
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
            if (DeviceLookupService.IsHarmony(process.Platform) && process.HarmonyUserId >= 0)
            {
                AppInfo userMatch = bundleMatches.FirstOrDefault(delegate(AppInfo app)
                {
                    return app.HarmonyUserId == process.HarmonyUserId;
                });
                if (userMatch != null) return userMatch;
            }
            // If HDC did not expose the process user, do not silently bind a
            // same-Bundle process to the first profile row. The user can
            // still select the real process explicitly from the process tab.
            return bundleMatches.Count == 1 ? bundleMatches[0] : null;
        }

        private AppInfo FindAppByBundle(string bundle)
        {
            return FindAppByBundle(bundle, -1);
        }

        private AppInfo FindAppByBundle(string bundle, int harmonyUserId)
        {
            return FindHarmonyAppByBundle(_apps, bundle, harmonyUserId);
        }

        internal static AppInfo FindHarmonyAppByBundle(
            IEnumerable<AppInfo> apps,
            string bundle,
            int harmonyUserId)
        {
            if (string.IsNullOrWhiteSpace(bundle)) return null;
            List<AppInfo> matches = (apps ?? Enumerable.Empty<AppInfo>())
                .Where(delegate(AppInfo app)
                {
                    return app != null && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            if (harmonyUserId >= 0)
            {
                // A requested profile is part of the target identity. If it
                // is no longer present after refresh, leave the selection
                // unresolved instead of silently switching profiles.
                return matches.FirstOrDefault(delegate(AppInfo app) { return app.HarmonyUserId == harmonyUserId; });
            }
            if (matches.Count == 1) return matches[0];
            return matches.Any(delegate(AppInfo app) { return DeviceLookupService.IsHarmony(app.Platform); })
                ? null
                : matches.FirstOrDefault();
        }

        private ProcessInfo FindProcessForApp(AppInfo app)
        {
            if (app == null) return null;
            ProcessInfo restored = _initialSelection == null ? null : _initialSelection.Process;
            bool restoresSameHarmonyTarget = DeviceLookupService.IsHarmony(app.Platform)
                && restored != null
                && restored.Pid == app.ProcessPid
                && (_initialSelection.App == null || SameAppSelection(app, _initialSelection.App));
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
                {
                    if (!restoresSameHarmonyTarget
                        || ProcessTargetMatcher.SameHarmonyProcessInstance(processOnly, restored))
                        return processOnly;
                    return null;
                }
                if (restoresSameHarmonyTarget) return null;
            }
            if (string.IsNullOrWhiteSpace(app.BundleId)) return null;
            if (DeviceLookupService.IsHarmony(app.Platform))
            {
                bool hasMultipleProfiles = app.HarmonyUserId >= 0
                    && _apps.Where(delegate(AppInfo candidate)
                    {
                        return candidate != null
                            && string.Equals(candidate.BundleId, app.BundleId, StringComparison.OrdinalIgnoreCase)
                            && candidate.HarmonyUserId >= 0;
                    })
                    .Select(delegate(AppInfo candidate) { return candidate.HarmonyUserId; })
                    .Distinct()
                    .Skip(1)
                    .Any();
                return FindHarmonyProcessForApp(_processes, app, !hasMultipleProfiles);
            }
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

        private static bool SameAppSelection(AppInfo left, AppInfo right)
        {
            if (left == null || right == null) return false;
            if (left.ProcessPid > 0 || right.ProcessPid > 0)
                return left.ProcessPid > 0 && left.ProcessPid == right.ProcessPid
                    && (string.IsNullOrWhiteSpace(left.ProcessName) || string.IsNullOrWhiteSpace(right.ProcessName)
                        || string.Equals(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(left.BundleId) || string.IsNullOrWhiteSpace(right.BundleId)
                || !string.Equals(left.BundleId, right.BundleId, StringComparison.OrdinalIgnoreCase)) return false;
            if (DeviceLookupService.IsHarmony(left.Platform) || DeviceLookupService.IsHarmony(right.Platform))
            {
                if (left.HarmonyUserId >= 0 || right.HarmonyUserId >= 0)
                    return left.HarmonyUserId >= 0 && right.HarmonyUserId >= 0
                        && left.HarmonyUserId == right.HarmonyUserId;
            }
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
            return FindHarmonyProcessForApp(processes, app, true);
        }

        internal static ProcessInfo FindHarmonyProcessForApp(
            IEnumerable<ProcessInfo> processes,
            AppInfo app,
            bool allowUnknownUser)
        {
            return app == null
                ? null
                : FindHarmonyProcessForBundle(processes, app.BundleId, app.HarmonyUserId, allowUnknownUser);
        }

        internal static ProcessInfo FindHarmonyProcessForBundle(IEnumerable<ProcessInfo> processes, string bundle, int harmonyUserId)
        {
            return FindHarmonyProcessForBundle(processes, bundle, harmonyUserId, true);
        }

        internal static ProcessInfo FindHarmonyProcessForBundle(
            IEnumerable<ProcessInfo> processes,
            string bundle,
            int harmonyUserId,
            bool allowUnknownUser)
        {
            if (string.IsNullOrWhiteSpace(bundle)) return null;
            List<ProcessInfo> matches = (processes ?? Enumerable.Empty<ProcessInfo>())
                .Where(ProcessTargetMatcher.IsValidTarget)
                .Where(delegate(ProcessInfo process)
                {
                    return DeviceLookupService.IsHarmony(process.Platform)
                        && (string.Equals(process.BundleId, bundle, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(process.OwnerBundleId, bundle, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
            if (matches.Count == 0) return null;

            if (harmonyUserId >= 0)
            {
                List<ProcessInfo> userMatches = matches.Where(delegate(ProcessInfo process)
                {
                    return process.HarmonyUserId == harmonyUserId;
                }).ToList();
                if (userMatches.Count > 0)
                    matches = userMatches;
                else if (!allowUnknownUser || matches.Count != 1 || matches[0].HarmonyUserId >= 0)
                    return null;
            }

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

        private async Task<HarmonyTargetInventory> WaitForHarmonyProcessAsync(
            DeviceInfo device,
            HarmonyTargetInventory initial,
            List<ProcessInfo> processes,
            CancellationToken token,
            long generation,
            string deviceUdid,
            int harmonyUserId)
        {
            HarmonyTargetInventory latestSnapshot = initial ?? new HarmonyTargetInventory();
            List<ProcessInfo> latest = processes ?? latestSnapshot.Processes ?? new List<ProcessInfo>();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (!IsCurrentLoad(generation, deviceUdid)
                    || FindHarmonyProcessForBundle(latest, _preferredAppBundleId, harmonyUserId) != null)
                    return latestSnapshot;
                await Task.Delay(500, token).ConfigureAwait(true);
                try
                {
                    latestSnapshot = await _lookup.ListTargetsAsync(device, token).ConfigureAwait(true);
                    latest = latestSnapshot == null ? new List<ProcessInfo>() : latestSnapshot.Processes ?? new List<ProcessInfo>();
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Startup can temporarily make the process view unreadable.
                    // Keep polling the same target instead of falling back to a
                    // different process or declaring another app selected.
                }
            }
            return latestSnapshot;
        }

        private void UpdateSelectedAppSummary(AppInfo app)
        {
            if (SelectedAppName == null) return;
            SelectedAppName.Text = app == null ? "请选择 APP" : FirstNonEmpty(app.Name, app.BundleId);
            SelectedAppBundle.Text = app == null ? "" : app.TargetIdentifier;
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
                        if (!_launchMode && selectedProcess != null) ProcessList.SelectedItem = _processes.FirstOrDefault(delegate(ProcessInfo process) { return process.Pid == selectedProcess.Pid; });
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
