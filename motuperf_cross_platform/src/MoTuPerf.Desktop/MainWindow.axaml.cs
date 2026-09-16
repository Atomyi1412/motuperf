using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using MoTuPerf.Platform;

namespace MoTuPerf.Desktop
{
    public partial class MainWindow : Window
    {
        private bool _allowClose;
        private bool _closeFlowRunning;
        private bool _closeSavePending;
        private string _renderedFpsLegendSignature;
        private int _renderedCoreLegendCount = -1;
        private string _renderedTemperatureLegendSignature;
        private readonly SemaphoreSlim _fileActionGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _devicePickerGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _screenshotViewerGate = new SemaphoreSlim(1, 1);
        public MainWindow()
        {
            InitializeComponent();
            MainWindowViewModel viewModel = new MainWindowViewModel();
            viewModel.CaptureStoppedUnexpectedly += async delegate { await OfferSaveAsync(); };
            DataContext = viewModel;
            Opened += async delegate
            {
                await Task.Delay(800);
                await CheckForUpdatesAsync(false);
            };
            UpdateDataTabState();
            foreach (string name in new[] { "FrameChart", "FrameTimeChart", "MemoryChart", "CpuChart", "NormalizedCpuChart", "CoreCpuChart", "TemperatureChart", "ThermalStateChart" })
            {
                PerformanceChartControl chart = this.FindControl<PerformanceChartControl>(name);
                if (chart != null)
                {
                    chart.TimeSelected += ChartTimeSelected;
                    chart.ZoomRangeSelected += ChartZoomRangeSelected;
                }
            }
            viewModel.PropertyChanged += ViewModelPropertyChanged;
            UpdateSeriesLegends();
            Closed += delegate
            {
                viewModel.PropertyChanged -= ViewModelPropertyChanged;
                viewModel.Dispose();
                _fileActionGate.Dispose();
                _devicePickerGate.Dispose();
                _screenshotViewerGate.Dispose();
            };
        }

        private void ViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentThemeName)
                || e.PropertyName == nameof(MainWindowViewModel.CurrentThemePreviewColor))
            {
                PerformanceChartControl.RefreshThemeResources();
                _renderedFpsLegendSignature = null;
                _renderedCoreLegendCount = -1;
                _renderedTemperatureLegendSignature = null;
                UpdateSeriesLegends();
            }
            if (e.PropertyName == nameof(MainWindowViewModel.Samples)
                || e.PropertyName == nameof(MainWindowViewModel.ShowFps)
                || e.PropertyName == nameof(MainWindowViewModel.ShowJank)
                || e.PropertyName == nameof(MainWindowViewModel.ShowBigJank))
            {
                UpdateSeriesLegends();
            }
        }

        private void UpdateSeriesLegends()
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null) return;
            UpdateFpsLegend(viewModel);
            UpdateCoreCpuLegend(viewModel.Samples);
            UpdateTemperatureLegend(viewModel.Samples);
        }

        private void UpdateFpsLegend(MainWindowViewModel viewModel)
        {
            StackPanel panel = this.FindControl<StackPanel>("FpsLegendPanel");
            PerformanceChartControl chart = this.FindControl<PerformanceChartControl>("FrameChart");
            if (panel == null || chart == null) return;
            string signature = viewModel.ShowFps + "|" + viewModel.ShowJank + "|" + viewModel.ShowBigJank;
            if (string.Equals(signature, _renderedFpsLegendSignature, StringComparison.Ordinal)) return;
            _renderedFpsLegendSignature = signature;
            panel.Children.Clear();
            if (viewModel.ShowFps) panel.Children.Add(CreateLegendItem("FPS", "FPS", Brush.Parse("#FF6B9A"), chart));
            if (viewModel.ShowJank) panel.Children.Add(CreateLegendItem("Jank", "Jank", Brush.Parse("#4DB5E0"), chart));
            if (viewModel.ShowBigJank) panel.Children.Add(CreateLegendItem("BigJank", "BigJank", Brush.Parse("#91DB72"), chart));
        }

        private void SelectTheme(object sender, RoutedEventArgs e)
        {
            AppThemeDefinition option = (sender as Button)?.DataContext as AppThemeDefinition;
            if (option == null) return;
            AppThemeManager.Select(Application.Current, option.Id);
            (DataContext as MainWindowViewModel)?.RefreshThemePresentation();
            this.FindControl<Button>("ThemeSelectorButton")?.Flyout?.Hide();
        }

        private async void ShowDataCleanup(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null || viewModel.IsCapturing || viewModel.IsFileOperationInProgress) return;
            this.FindControl<Button>("SettingsButton")?.Flyout?.Hide();
            await new DataCleanupWindow(CSharpIosPerfMonitor.RuntimeTools.DataDirectory).ShowDialog(this);
        }

        private async void ShowDataDirectorySettings(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null || viewModel.IsCapturing || viewModel.IsFileOperationInProgress) return;
            this.FindControl<Button>("SettingsButton")?.Flyout?.Hide();
            string directory = await new DataDirectorySettingsWindow(viewModel.DataDirectory).ShowDialog<string>(this);
            if (string.IsNullOrWhiteSpace(directory) || string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(viewModel.DataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
            await viewModel.ChangeDataDirectoryAsync(directory);
        }

        private async void CheckForUpdatesManually(object sender, RoutedEventArgs e)
        {
            this.FindControl<Button>("SettingsButton")?.Flyout?.Hide();
            await CheckForUpdatesAsync(true);
        }

        private async Task CheckForUpdatesAsync(bool manual)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null || viewModel.IsCapturing || viewModel.IsFileOperationInProgress)
            {
                if (manual && viewModel != null) viewModel.Status = "请先停止采集或完成当前文件操作";
                return;
            }
            UpdateCheckResult result = await viewModel.CheckForUpdatesAsync(manual, CancellationToken.None);
            if (viewModel.IsCapturing || viewModel.IsFileOperationInProgress)
            {
                if (manual) viewModel.Status = "采集或文件操作已开始，更新提示已延后";
                return;
            }
            if (!result.HasUpdate || result.Manifest == null)
            {
                if (manual) viewModel.Status = result.Message ?? "当前已是最新版本";
                return;
            }
            if (!manual && !result.Manifest.Mandatory && viewModel.IsUpdateSkipped(result.Manifest.Version)) return;
            UpdatePromptChoice? choice = await new UpdatePromptWindow(result.Manifest).ShowDialog<UpdatePromptChoice?>(this);
            if (choice == UpdatePromptChoice.Skip)
            {
                viewModel.SkipUpdate(result.Manifest.Version);
                return;
            }
            if (choice != UpdatePromptChoice.Update) return;
            if (viewModel.IsCapturing || viewModel.IsFileOperationInProgress)
            {
                viewModel.Status = "采集或文件操作已开始，暂不能安装更新";
                return;
            }
            if (!await OfferSaveAsync()) return;
            await DownloadAndOpenUpdateAsync(viewModel, result.Manifest);
        }

        private async Task DownloadAndOpenUpdateAsync(MainWindowViewModel viewModel, UpdateManifest manifest)
        {
            if (viewModel.IsCapturing || viewModel.IsFileOperationInProgress)
            {
                viewModel.Status = "采集或文件操作已开始，已取消更新";
                return;
            }
            CancellationTokenSource cancellation = new CancellationTokenSource();
            UpdateDownloadWindow window = new UpdateDownloadWindow();
            window.CancelRequested += delegate { cancellation.Cancel(); };
            Task dialogTask = window.ShowDialog(this);
            try
            {
                System.Progress<UpdateDownloadProgress> progress = new System.Progress<UpdateDownloadProgress>(window.SetProgress);
                string path = await viewModel.DownloadUpdateAsync(manifest.Asset, progress, cancellation.Token);
                if (viewModel.IsCapturing || viewModel.IsFileOperationInProgress)
                {
                    window.Complete();
                    await dialogTask;
                    viewModel.Status = "采集或文件操作已开始，未启动安装程序";
                    return;
                }
                window.Complete();
                await dialogTask;
                viewModel.Status = "更新包已校验，正在打开安装程序";
                Process.Start(ReleaseUpdateService.CreateInstallerStartInfo(path, AppContext.BaseDirectory));
            }
            catch (OperationCanceledException)
            {
                window.Complete();
                await dialogTask;
                viewModel.Status = "已取消下载更新";
            }
            catch (Exception exception)
            {
                window.ShowFailure("下载失败：" + exception.Message);
                await dialogTask;
                viewModel.Status = "更新失败：" + exception.Message;
            }
            finally { cancellation.Dispose(); }
        }

        private void UpdateCoreCpuLegend(IReadOnlyList<CSharpIosPerfMonitor.PerfSample> samples)
        {
            StackPanel panel = this.FindControl<StackPanel>("CoreCpuLegendPanel");
            PerformanceChartControl chart = this.FindControl<PerformanceChartControl>("CoreCpuChart");
            if (panel == null || chart == null) return;
            int count = (samples ?? Array.Empty<CSharpIosPerfMonitor.PerfSample>())
                .Where(delegate(CSharpIosPerfMonitor.PerfSample sample) { return sample != null && sample.HasCpuCoreUsage && sample.CpuCorePercents != null; })
                .Select(delegate(CSharpIosPerfMonitor.PerfSample sample) { return Math.Min(sample.CpuCoreCount, sample.CpuCorePercents.Count); })
                .DefaultIfEmpty(0)
                .Max();
            if (count == _renderedCoreLegendCount) return;
            _renderedCoreLegendCount = count;
            panel.Children.Clear();
            if (count == 0)
            {
                panel.Children.Add(CreateWaitingLegend("等待逐核数据"));
            }
            else
            {
                for (int index = 0; index < count; index++)
                {
                    string name = PerformanceChartControl.CoreSeriesName(index);
                    panel.Children.Add(CreateLegendItem(name, name, PerformanceChartControl.CoreCpuBrush(index), count > 1 ? chart : null));
                }
            }
            SetLegendRowHeight("CoreCpuRow", count);
        }

        private void UpdateTemperatureLegend(IReadOnlyList<CSharpIosPerfMonitor.PerfSample> samples)
        {
            StackPanel panel = this.FindControl<StackPanel>("TemperatureLegendPanel");
            PerformanceChartControl chart = this.FindControl<PerformanceChartControl>("TemperatureChart");
            if (panel == null || chart == null) return;
            List<string> sensors = PerformanceChartControl.TemperatureSensorNames(samples);
            string signature = string.Join("\u001f", sensors);
            if (string.Equals(signature, _renderedTemperatureLegendSignature, StringComparison.Ordinal)) return;
            _renderedTemperatureLegendSignature = signature;
            panel.Children.Clear();
            if (sensors.Count == 0)
            {
                panel.Children.Add(CreateWaitingLegend("等待温度数据"));
            }
            else
            {
                for (int index = 0; index < sensors.Count; index++)
                {
                    string sensor = sensors[index];
                    panel.Children.Add(CreateLegendItem(sensor, PerformanceChartControl.TemperatureSeriesName(sensor), PerformanceChartControl.TemperatureBrush(index), sensors.Count > 1 ? chart : null));
                }
            }
            SetLegendRowHeight("TemperatureRow", sensors.Count);
        }

        private static TextBlock CreateWaitingLegend(string text)
        {
            return new TextBlock { Text = text, Foreground = Brush.Parse(AppThemeManager.ColorValue("Theme.TextMuted")), FontSize = 11 };
        }

        private static Control CreateLegendItem(string label, string seriesName, IBrush color, PerformanceChartControl chart)
        {
            StackPanel item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = chart == null ? new Cursor(StandardCursorType.Arrow) : new Cursor(StandardCursorType.Hand)
            };
            Border swatch = new Border
            {
                Width = 11,
                Height = 11,
                Background = color,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock labelText = new TextBlock
            {
                Text = label,
                Foreground = Brush.Parse(AppThemeManager.ColorValue("Theme.TextSecondary")),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            item.Children.Add(swatch);
            item.Children.Add(labelText);
            Action updateState = delegate
            {
                bool visible = chart == null || chart.IsSeriesVisible(seriesName);
                swatch.Background = visible ? color : Brush.Parse(AppThemeManager.ColorValue("Theme.TextMuted"));
                labelText.Foreground = visible ? Brush.Parse(AppThemeManager.ColorValue("Theme.TextSecondary")) : Brush.Parse(AppThemeManager.ColorValue("Theme.TextMuted"));
                item.Opacity = visible ? 1 : 0.72;
            };
            updateState();
            if (chart != null)
            {
                ToolTip.SetTip(item, "点击隐藏或显示 " + label + " 曲线");
                item.PointerReleased += delegate(object sender, PointerReleasedEventArgs e)
                {
                    if (e.InitialPressMouseButton != MouseButton.Left) return;
                    chart.ToggleSeries(seriesName);
                    updateState();
                    e.Handled = true;
                };
            }
            return item;
        }

        private void SetLegendRowHeight(string rowName, int itemCount)
        {
            Border row = this.FindControl<Border>(rowName);
            if (row == null) return;
            double height = Math.Max(156, 46 + Math.Max(1, itemCount) * 22);
            row.Height = height;
            row.MinHeight = height;
        }

        private async void OpenDevicePicker(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            await OpenDevicePickerAsync(false);
        }

        private async Task OpenDevicePickerAsync(bool startAfterSelection)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null || viewModel.IsCapturing) return;
            if (!await _devicePickerGate.WaitAsync(0)) return;

            try
            {
                DeviceSelection initialSelection = startAfterSelection && viewModel.CaptureSelectionNeedsPicker
                    ? null
                    : viewModel.CurrentSelection;
                DevicePickerWindow picker = new DevicePickerWindow(initialSelection);
                DeviceSelection selection = await picker.ShowDialog<DeviceSelection>(this);
                if (selection != null)
                {
                    viewModel.ApplySelection(selection);
                    if (startAfterSelection || selection.AutoStart) viewModel.ToggleCapture();
                }
            }
            finally { _devicePickerGate.Release(); }
        }

        private void TitleBarPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }

        private void TitleBarDoubleTapped(object sender, RoutedEventArgs e)
        {
            ToggleWindowState();
        }

        private void MinimizeWindow(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeWindow(object sender, RoutedEventArgs e)
        {
            ToggleWindowState();
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleWindowState()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CollapseParameters(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null) return;
            viewModel.IsParametersCollapsed = true;
        }

        private void ExpandParameters(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null) return;
            viewModel.IsParametersCollapsed = false;
        }

        private void ToggleDeviceInfo(object sender, RoutedEventArgs e)
        {
            (DataContext as MainWindowViewModel)?.ToggleDeviceInfo();
        }

        private void ShowLiveDataTab(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null) return;
            viewModel.ShowLiveDataTab();
            UpdateDataTabState();
        }

        private void ShowSelectedDataTab(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null) return;
            viewModel.ShowSelectedDataTab();
            UpdateDataTabState();
        }

        private void UpdateDataTabState()
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            Button live = this.FindControl<Button>("LiveDataTabButton");
            Button selected = this.FindControl<Button>("SelectedDataTabButton");
            if (viewModel == null || live == null || selected == null) return;
            live.Classes.Set("activeTab", viewModel.ShowLiveData);
            selected.Classes.Set("activeTab", viewModel.ShowSelectedData);
        }

        private void ChartTimeSelected(double elapsedSeconds)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel != null) viewModel.SelectTimeFromChart(elapsedSeconds);
        }

        private void ChartZoomRangeSelected(double startTime, double endTime)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel != null) viewModel.ApplyChartZoom(startTime, endTime);
        }

        private void ResetChartZoom(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel != null) viewModel.ResetChartZoom();
        }

        private void ScreenshotSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            ScreenshotItemViewModel selected = (sender as ListBox)?.SelectedItem as ScreenshotItemViewModel;
            if (viewModel != null && selected != null) viewModel.SelectScreenshot(selected);
        }

        private async void ScreenshotDoubleTapped(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null || viewModel.SelectedScreenshot == null) return;
            if (!await _screenshotViewerGate.WaitAsync(0)) return;
            try
            {
                ScreenshotViewerWindow viewer = new ScreenshotViewerWindow(
                    viewModel.Screenshots,
                    viewModel.SelectedScreenshot);
                viewer.ScreenshotSelected += viewModel.SelectScreenshot;
                await viewer.ShowDialog(this);
            }
            finally { _screenshotViewerGate.Release(); }
        }

        private async void ToggleCapture(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null) return;
            if (!viewModel.IsCapturing && viewModel.CaptureSelectionNeedsPicker)
            {
                viewModel.Status = "请先选择当前设备、应用和进程";
                await OpenDevicePickerAsync(true);
                return;
            }
            bool wasCapturing = viewModel.IsCapturing;
            viewModel.ToggleCapture();
            if (wasCapturing && !viewModel.IsCapturing) await OfferSaveAsync();
        }

        private async void OpenSession(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null) return;
            if (viewModel.IsCapturing) { viewModel.Status = "请先停止采集再打开现场文件"; return; }
            if (viewModel.IsFileOperationInProgress) return;
            if (!await _fileActionGate.WaitAsync(0)) return;
            try
            {
                IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "打开 MoTuPerf 现场文件",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("MoTuPerf 现场文件") { Patterns = new[] { "*.motuperf" } } }
                });
                if (files.Count == 0) return;
                string path = files[0].TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) throw new IOException("当前文件位置不能作为本地现场文件读取。");
                if (!viewModel.BeginSessionLoading()) return;
                viewModel.Status = "正在打开现场文件...";
                await Task.Yield();
                try
                {
                    string extraction = Path.Combine(CSharpIosPerfMonitor.RuntimeTools.DataDirectory, "opened", Path.GetFileNameWithoutExtension(path) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), "screenshots");
                    CSharpIosPerfMonitor.SessionDocument document = await Task.Run(delegate { return CSharpIosPerfMonitor.SessionArchiveService.Load(path, extraction); });
                    await viewModel.ApplySessionDocumentAsync(document);
                    viewModel.Status = "已打开现场：" + Path.GetFileName(path);
                }
                 finally { viewModel.EndFileOperation(); }
            }
            catch (Exception ex) { viewModel.Status = "打开失败：" + ex.Message; }
            finally { _fileActionGate.Release(); }
        }

        private async void SaveSession(object sender, RoutedEventArgs e) { await SaveSessionAsync(); }
        private async Task<bool> SaveSessionAsync()
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null || !viewModel.HasSessionData) { if (viewModel != null) viewModel.Status = "没有可保存的现场数据"; return false; }
            if (viewModel.IsCapturing) { viewModel.Status = "请先停止采集再保存现场文件"; return false; }
            if (viewModel.IsFileOperationInProgress) return false;
            if (!await _fileActionGate.WaitAsync(0)) return false;
            try
            {
                IStorageFile file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "保存 MoTuPerf 现场文件",
                    SuggestedFileName = "motuperf-session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".motuperf",
                    DefaultExtension = "motuperf",
                    FileTypeChoices = new[] { new FilePickerFileType("MoTuPerf 现场文件") { Patterns = new[] { "*.motuperf" } } }
                });
                if (file == null) return false;
                string path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) throw new IOException("当前文件位置不能作为本地现场文件保存。");
                if (!viewModel.BeginFileOperation("正在保存现场文件，请稍候...", "正在写入曲线、设备信息和截图，请勿关闭软件")) return false;
                try
                {
                    viewModel.Status = "正在保存现场文件...";
                    await Task.Yield();
                    CSharpIosPerfMonitor.SessionDocument document = viewModel.BuildSessionDocument();
                    await Task.Run(delegate { CSharpIosPerfMonitor.SessionArchiveService.Save(path, document); });
                    _closeSavePending = false;
                    viewModel.Status = "已保存现场：" + Path.GetFileName(path);
                    return true;
                }
                finally { viewModel.EndFileOperation(); }
            }
            catch (Exception ex) { viewModel.Status = "保存失败：" + ex.Message; return false; }
            finally { _fileActionGate.Release(); }
        }

        private async void ExportCsv(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null || !viewModel.HasSessionData) { if (viewModel != null) viewModel.Status = "没有可导出的采集数据"; return; }
            if (viewModel.IsCapturing) { viewModel.Status = "请先停止采集再导出数据"; return; }
            if (viewModel.IsFileOperationInProgress) return;
            if (!await _fileActionGate.WaitAsync(0)) return;
            try
            {
                CSharpIosPerfMonitor.CsvExportMode? mode = await new ExportCsvDialogWindow().ShowDialog<CSharpIosPerfMonitor.CsvExportMode?>(this);
                if (!mode.HasValue) return;
                bool rawDetail = mode.Value == CSharpIosPerfMonitor.CsvExportMode.RawDetail;
                IStorageFile file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = rawDetail ? "导出原始明细 CSV" : "导出秒级汇总 CSV",
                    SuggestedFileName = (rawDetail ? "motuperf-raw-" : "motuperf-") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv",
                    DefaultExtension = "csv",
                    FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } } }
                });
                if (file == null) return;
                string path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) throw new IOException("当前文件位置不能作为本地 CSV 保存。");
                if (!viewModel.BeginFileOperation(
                    rawDetail ? "正在导出原始明细，请稍候..." : "正在导出秒级汇总，请稍候...",
                    "正在整理曲线、设备信息和统计数据，请勿关闭软件")) return;
                try
                {
                    viewModel.Status = rawDetail ? "正在导出原始明细 CSV..." : "正在导出秒级汇总 CSV...";
                    await Task.Yield();
                    CSharpIosPerfMonitor.SessionDocument document = viewModel.BuildSessionDocument();
                    await Task.Run(delegate { CSharpIosPerfMonitor.CsvExportService.WriteToFile(document, mode.Value, path); });
                    viewModel.Status = (rawDetail ? "已导出原始明细：" : "已导出秒级汇总：") + Path.GetFileName(path);
                }
                finally { viewModel.EndFileOperation(); }
            }
            catch (Exception ex) { viewModel.Status = "导出失败：" + ex.Message; }
            finally { _fileActionGate.Release(); }
        }

        private async void ShowHelp(object sender, RoutedEventArgs e) { await new HelpWindow().ShowDialog(this); }

        private async void ShowChangelog(object sender, RoutedEventArgs e) { await new ChangelogWindow().ShowDialog(this); }

        private async void WindowClosing(object sender, WindowClosingEventArgs e)
        {
            if (_allowClose) return;
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            if (viewModel == null) return;
            if (viewModel.IsFileOperationInProgress)
            {
                e.Cancel = true;
                viewModel.Status = "文件操作正在进行，请等待完成后再关闭软件";
                return;
            }
            if (_closeFlowRunning) { e.Cancel = true; return; }
            if (!viewModel.IsCapturing && !_closeSavePending) return;
            e.Cancel = true;
            _closeFlowRunning = true;
            try
            {
                if (viewModel.IsCapturing)
                {
                    bool close = await ConfirmAsync("采集仍在进行", "关闭软件会停止当前采集，是否继续关闭？", "继续关闭", "取消");
                    if (!close) return;
                    viewModel.ToggleCapture();
                }
                _closeSavePending = true;
                if (!await OfferSaveAsync()) return;
                _allowClose = true;
                Close();
            }
            finally { _closeFlowRunning = false; }
        }

        private async Task<bool> OfferSaveAsync()
        {
            MainWindowViewModel viewModel = DataContext as MainWindowViewModel;
            return await SessionSaveDecision.CanContinueAsync(viewModel != null && viewModel.HasSessionData,
                () => new ConfirmDialogWindow("保存采集数据", "是否保存本次采集现场？", "保存", "不保存").ShowDialog<bool?>(this),
                SaveSessionAsync);
        }

        private async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
        {
            return await new ConfirmDialogWindow(title, message, accept, cancel).ShowDialog<bool>(this);
        }

        private static void WriteTextAtomic(string path, string content)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, content ?? "", new UTF8Encoding(true)); File.Move(temp, fullPath, true); }
            catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } throw; }
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }
    }
}
