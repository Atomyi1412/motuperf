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
        private TextBlock DeviceCountText;
        private Button AppleDriverButton;
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
            _deviceCountStatus = "正在检测设备...";
            StatusText.Text = _deviceCountStatus;
            AppleDriverButton.IsVisible = false;
            try
            {
                DeviceDiscoveryReport report = await _lookup.DiscoverDevicesAsync(token);
                if (!IsCurrentLoad(generation)) return;
                DeviceList.ItemsSource = report.Devices;
                _deviceCountStatus = report.Devices.Count == 0
                    ? (report.AppleDriverActionAvailable && !string.IsNullOrWhiteSpace(report.IosDiagnostic)
                        ? report.IosDiagnostic
                        : report.StatusMessage)
                    : "已检测到 " + report.Devices.Count + " 台设备。";
                StatusText.Text = _deviceCountStatus;
                AppleDriverButton.IsVisible = report.Devices.Count == 0 && report.AppleDriverActionAvailable;
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
                _deviceCountStatus = "设备检测失败：" + ex.Message;
                StatusText.Text = _deviceCountStatus;
                AppleDriverButton.IsVisible = false;
            }
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
            long generation;
            CancellationToken token = NewLoadToken(out generation);
            StatusText.Text = "正在读取 " + device.PickerLabel + " 的 APP 和进程...";
            AppList.ItemsSource = null;
            ProcessList.ItemsSource = null;
            try
            {
                Task<List<AppInfo>> appsTask = _lookup.ListAppsAsync(device, token);
                Task<List<ProcessInfo>> processesTask = _lookup.ListProcessesAsync(device, token);
                await Task.WhenAll(appsTask, processesTask);
                if (!IsCurrentLoad(generation, deviceUdid)) return;
                _apps.Clear();
                _apps.AddRange(appsTask.Result);
                _processes.Clear();
                _processes.AddRange(processesTask.Result);
                AppInfo selectedApp = FindInitialApp() ?? _apps.FirstOrDefault(delegate(AppInfo app) { return app.Recommended; }) ?? _apps.FirstOrDefault();
                ProcessInfo selectedProcess = FindInitialProcess() ?? _processes.FirstOrDefault(delegate(ProcessInfo process) { return process.Recommended; }) ?? _processes.FirstOrDefault();
                RunSelectionSync(delegate
                {
                    ApplyAppFilter();
                    AppList.SelectedItem = selectedApp;
                    ApplyProcessFilter();
                    ProcessList.SelectedItem = selectedProcess;
                });
                UpdateSelectedAppSummary(selectedApp ?? FindAppForProcess(selectedProcess));
                StatusText.Text = _deviceCountStatus;
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
            _launchInProgress = true;
            try
            {
                StatusText.Text = "正在启动 " + app.Name + "...";
                CancellationToken token = NewLoadToken(out _);
                ProcessResult result = await _lookup.LaunchAppAsync(device, app, token);
                if (result.ExitCode != 0)
                {
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
            finally { _launchInProgress = false; }
        }

        private void AppSearchChanged(object sender, TextChangedEventArgs e) { ApplyAppFilter(); }
        private void ProcessSearchChanged(object sender, TextChangedEventArgs e) { ApplyProcessFilter(); }
        private void ApplyAppFilter()
        {
            string query = (AppSearch.Text ?? "").Trim();
            AppInfo current = AppList.SelectedItem as AppInfo;
            List<AppInfo> items = string.IsNullOrWhiteSpace(query) ? _apps.ToList() : _apps.Where(delegate(AppInfo app)
            {
                return Contains(app.Name, query) || Contains(app.BundleId, query) || Contains(app.Version, query) || Contains(app.Reason, query);
            }).ToList();
            AppList.ItemsSource = items;
            AppInfo selected = current == null ? null : items.FirstOrDefault(delegate(AppInfo app) { return app.BundleId == current.BundleId; });
            if (selected != null) AppList.SelectedItem = selected;
        }
        private void ApplyProcessFilter()
        {
            string query = (ProcessSearch.Text ?? "").Trim();
            ProcessInfo current = ProcessList.SelectedItem as ProcessInfo;
            IEnumerable<ProcessInfo> source = _processes.Where(ProcessTargetMatcher.IsValidTarget);
            if (string.IsNullOrWhiteSpace(query))
            {
                DeviceInfo device = DeviceList.SelectedItem as DeviceInfo;
                AppInfo selectedApp = AppList.SelectedItem as AppInfo;
                string selectedBundle = FirstNonEmpty(
                    selectedApp == null ? "" : selectedApp.BundleId,
                    _initialSelection == null || _initialSelection.App == null ? "" : _initialSelection.App.BundleId);
                if (!DeviceLookupService.IsAndroid(device))
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
            selected = selected ?? items.FirstOrDefault(delegate(ProcessInfo process) { return process.Recommended; }) ?? items.FirstOrDefault();
            if (selected != null) ProcessList.SelectedItem = selected;
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
        private AppInfo FindInitialApp() { return _initialSelection == null || _initialSelection.App == null ? null : _apps.FirstOrDefault(delegate(AppInfo app) { return app.BundleId == _initialSelection.App.BundleId; }); }
        private ProcessInfo FindInitialProcess() { return _initialSelection == null || _initialSelection.Process == null ? null : _processes.FirstOrDefault(delegate(ProcessInfo process) { return process.Pid == _initialSelection.Process.Pid; }); }
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
            return _apps.FirstOrDefault(delegate(AppInfo app) { return !string.IsNullOrWhiteSpace(bundle) && string.Equals(app.BundleId, bundle, StringComparison.OrdinalIgnoreCase); });
        }

        private void UpdateSelectedAppSummary(AppInfo app)
        {
            if (SelectedAppName == null) return;
            SelectedAppName.Text = app == null ? "请选择 APP" : FirstNonEmpty(app.Name, app.BundleId);
            SelectedAppBundle.Text = app == null ? "" : app.BundleId;
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
                        if (selectedApp != null) AppList.SelectedItem = _apps.FirstOrDefault(delegate(AppInfo app) { return app.BundleId == selectedApp.BundleId; });
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
            return string.IsNullOrWhiteSpace(value as string);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }
    }
}
