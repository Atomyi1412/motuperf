using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace CSharpIosPerfMonitor
{
    public sealed class MainWindow : Window
    {
        private const string AppIconAsset = "assets/motu-icon.png";
        private const string AppLogoAsset = "assets/motu-logo-full.png";
        private const double FreshIosProcessSnapshotSeconds = 30.0;
        private const double ChartRowHeight = 156.0;

        private static readonly SolidColorBrush AppBackground = Brush(15, 16, 24);
        private static readonly SolidColorBrush TopBackground = Brush(25, 25, 35);
        private static readonly SolidColorBrush PanelBackground = Brush(30, 31, 43);
        private static readonly SolidColorBrush CardBackground = Brush(42, 43, 58);
        private static readonly SolidColorBrush CardActiveBackground = Brush(55, 56, 76);
        private static readonly SolidColorBrush BorderBrushDark = Brush(66, 68, 86);
        private static readonly SolidColorBrush TextPrimary = Brush(235, 236, 244);
        private static readonly SolidColorBrush TextSecondary = Brush(172, 174, 190);
        private static readonly SolidColorBrush AccentBlue = Brush(20, 118, 255);
        private static readonly SolidColorBrush AccentPink = Brush(255, 124, 154);
        private static readonly SolidColorBrush AccentCyan = Brush(126, 222, 238);
        private static readonly SolidColorBrush AccentGreen = Brush(176, 224, 134);
        private static readonly SolidColorBrush AccentGold = Brush(245, 194, 86);
        private static readonly SolidColorBrush AccentTemperature = Brush(255, 142, 88);
        private static readonly SolidColorBrush AccentThermalState = Brush(255, 92, 92);
        private static readonly SolidColorBrush DisabledLegendBrush = Brush(103, 105, 121);
        private static readonly FontFamily ToolbarSymbolFont = new FontFamily("Segoe MDL2 Assets");

        private readonly DeviceLookupService _lookup = new DeviceLookupService();
        private readonly PerfCollector _collector = new PerfCollector();
        private readonly ScreenshotService _screenshots;
        private readonly List<PerfSample> _samples = new List<PerfSample>();
        private readonly List<ScreenshotInfo> _shots = new List<ScreenshotInfo>();
        private readonly string _dataDir;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        private ComboBox _deviceCombo;
        private ComboBox _appCombo;
        private ComboBox _processCombo;
        private TextBox _appSearch;
        private TextBox _processSearch;
        private TextBox _bundleInput;
        private TextBox _appLaunchSearch;
        private CheckBox _screenshotMetricCheck;
        private CheckBox _fpsMetricCheck;
        private CheckBox _frameMetricCheck;
        private CheckBox _memoryMetricCheck;
        private CheckBox _cpuMetricCheck;
        private CheckBox _temperatureMetricCheck;
        private CheckBox _thermalStateMetricCheck;
        private Slider _screenshotInterval;
        private TextBlock _status;
        private TextBlock _processHint;
        private Image _selectedAppSummaryImage;
        private TextBlock _selectedAppSummaryGlyph;
        private TextBlock _selectedAppSummaryName;
        private TextBlock _selectedAppSummaryBundle;
        private TextBlock _targetSummary;
        private TextBlock _timeSummary;
        private TextBlock _appCount;
        private TextBlock _processCount;
        private TextBlock _deviceInfoText;
        private TextBlock _dialogStatus;
        private Button _dialogChooseButton;
        private ListView _dialogProcessList;
        private ListView _dialogAppList;
        private Button _dialogProcessTabButton;
        private Button _dialogLaunchTabButton;
        private Button _dialogLaunchButton;
        private Grid _dialogProcessPanel;
        private Grid _dialogAppPanel;
        private Border _deviceInfoPanel;
        private Border _selectedAppSummaryCard;
        private Border _processEmptyState;
        private TextBlock _processEmptyText;
        private Button _processEmptyLaunchButton;
        private bool _isRenderingAppSelection;
        private bool _lockAndroidProcessToSelectedApp;
        private UIElement _shotsPanel;
        private UIElement _fpsRow;
        private UIElement _frameRow;
        private UIElement _memoryRow;
        private UIElement _cpuRow;
        private UIElement _cpuNormalizedRow;
        private UIElement _cpuCoreRow;
        private UIElement _temperatureRow;
        private UIElement _thermalStateRow;
        private Panel _cpuCoreLegendPanel;
        private Panel _temperatureLegendPanel;
        private int _renderedCpuCoreLegendCount = -1;
        private string _renderedTemperatureLegendSignature;
        private RowDefinition _fpsChartRowDef;
        private RowDefinition _frameChartRowDef;
        private RowDefinition _memoryChartRowDef;
        private RowDefinition _cpuChartRowDef;
        private RowDefinition _cpuNormalizedChartRowDef;
        private RowDefinition _cpuCoreChartRowDef;
        private RowDefinition _temperatureChartRowDef;
        private RowDefinition _thermalStateChartRowDef;
        private ScrollViewer _chartsScrollViewer;
        private Image _preview;
        private ScrollViewer _shotScrollViewer;
        private StackPanel _shotStrip;
        private ChartCanvas _fpsChart;
        private ChartCanvas _frameChart;
        private ChartCanvas _memoryChart;
        private ChartCanvas _cpuChart;
        private ChartCanvas _cpuNormalizedChart;
        private ChartCanvas _cpuCoreChart;
        private ChartCanvas _temperatureChart;
        private ChartCanvas _thermalStateChart;
        private Button _startButton;
        private Button _selectTargetButton;
        private readonly Dictionary<string, TextBlock> _liveMetricValues = new Dictionary<string, TextBlock>();
        private readonly Dictionary<string, TextBlock> _selectedMetricValues = new Dictionary<string, TextBlock>();
        private Button _liveDataTabButton;
        private Button _selectedDataTabButton;
        private UIElement _liveDataPanel;
        private UIElement _selectedDataPanel;
        private bool _showSelectedData;
        private Button _openButton;
        private Button _saveButton;
        private Button _exportButton;
        private ColumnDefinition _paramsColumn;
        private UIElement _paramsExpandedContent;
        private Button _paramsCollapseButton;
        private Button _paramsExpandButton;
        private bool _paramsCollapsed;
        private bool _dialogLaunchMode;
        private bool _sampleRenderQueued;

        private List<DeviceInfo> _devices = new List<DeviceInfo>();
        private List<AppInfo> _apps = new List<AppInfo>();
        private List<ProcessInfo> _processes = new List<ProcessInfo>();
        private DeviceInfo _selectedDevice;
        private AppInfo _selectedApp;
        private ProcessInfo _selectedProcess;
        private string _selectedBundleId = "com.tencent.xin";
        private double? _selectedTime;
        private bool _followLatest = true;
        private bool _loadingDevices;
        private bool _draggingShots;
        private bool _shotDragMoved;
        private bool _suppressShotClick;
        private Point _shotDragStart;
        private double _shotDragStartOffset;
        private bool _deviceInfoVisible;
        private bool _isCapturing;
        private bool _isStartingCapture;
        private bool _fileOperationInProgress;
        private bool _captureFailureHandled;
        private bool _targetIdentityConfirmed;
        private bool _realMetricSampleSeen;
        private bool _captureExpectsMetricData;
        private bool _windowCloseConfirmed;
        private bool _closeConfirmationShowing;
        private DateTime _sessionStartedAt;
        private CancellationTokenSource _deviceMonitorCts;
        private CancellationTokenSource _iconHydrateCts;
        private int _deviceRefreshRevision;
        private int _targetRefreshRevision;
        private int _iconHydrateRevision;
        private DateTime _processesRefreshedAtUtc;
        private string _processSnapshotUdid = "";
        private string _deviceDiscoveryStatus = "未检测到设备，请连接设备后刷新。";

        public MainWindow()
        {
            Title = Program.AppTitle + "-" + Program.AppVersion;
            Width = 1500;
            Height = 940;
            MinWidth = 1180;
            MinHeight = 760;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Icon = LoadPackImage(AppIconAsset);
            Background = AppBackground;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            });

            _dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(_dataDir);
            _screenshots = new ScreenshotService(Path.Combine(_dataDir, "screenshots"));

            BuildUi();
            WireEvents();
            RefreshDevices();
        }

        private void BuildUi()
        {
            DockPanel root = new DockPanel { Background = AppBackground };
            Content = root;

            UIElement topMenu = CreateTopMenu();
            DockPanel.SetDock(topMenu, Dock.Top);
            root.Children.Add(topMenu);

            UIElement toolbar = CreateToolbar();
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);

            Grid content = new Grid();
            _paramsColumn = new ColumnDefinition { Width = new GridLength(398) };
            content.ColumnDefinitions.Add(_paramsColumn);
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(content);

            Border side = CreateLeftPanel();
            Grid.SetColumn(side, 0);
            content.Children.Add(side);

            Grid main = CreateMainPanel();
            Grid.SetColumn(main, 1);
            content.Children.Add(main);
        }

        private UIElement CreateTopMenu()
        {
            Grid bar = new Grid
            {
                Height = 40,
                Background = Brush(20, 20, 29)
            };
            bar.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    return;
                }
                DragMove();
            };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });

            bar.Children.Add(CreateBrandHeader());

            _status = new TextBlock
            {
                Text = "正在检查设备...",
                Foreground = TextSecondary,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 18, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(_status, 2);
            bar.Children.Add(_status);

            StackPanel windowButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            windowButtons.Children.Add(WindowButton("—", delegate { WindowState = WindowState.Minimized; }));
            windowButtons.Children.Add(WindowButton("□", delegate { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }));
            windowButtons.Children.Add(WindowButton("×", delegate { Close(); }));
            Grid.SetColumn(windowButtons, 3);
            bar.Children.Add(windowButtons);
            return bar;
        }

        private static UIElement CreateBrandHeader()
        {
            StackPanel brand = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            brand.Children.Add(new Image
            {
                Source = LoadPackImage(AppLogoAsset),
                Width = 154,
                Height = 38,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            });
            brand.Children.Add(new TextBlock
            {
                Text = Program.AppVersion,
                Foreground = TextSecondary,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 2, 0, 0)
            });
            return brand;
        }

        private UIElement CreateToolbar()
        {
            Grid bar = new Grid
            {
                Height = 64,
                Background = Brush(24, 24, 32)
            };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(438) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

            StackPanel left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(18, 0, 0, 0)
            };
            _openButton = ToolbarButton("打开", delegate { OpenSessionFile(); });
            _saveButton = ToolbarButton("保存", delegate { SaveSessionFile(); });
            _exportButton = ToolbarButton("导出", delegate { ExportCsv(); });
            left.Children.Add(_openButton);
            left.Children.Add(_saveButton);
            left.Children.Add(_exportButton);
            left.Children.Add(ToolbarButton("帮助", delegate { ShowHelpDialog(); }));
            Border leftZone = ToolbarZone(left, HorizontalAlignment.Left);
            Grid.SetColumn(leftZone, 0);
            bar.Children.Add(leftZone);

            StackPanel center = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _startButton = CaptureToolbarButton();
            _startButton.Click += delegate
            {
                if (_isStartingCapture) return;
                if (_isCapturing) StopCapture();
                else StartCapture();
            };
            UpdateCaptureButton();
            center.Children.Add(_startButton);
            Border centerZone = ToolbarZone(center, HorizontalAlignment.Center);
            Grid.SetColumn(centerZone, 1);
            bar.Children.Add(centerZone);

            _selectTargetButton = PrimaryToolbarButton("选择设备及应用", delegate
            {
                if (_isCapturing || _isStartingCapture)
                {
                    SetStatus(_isStartingCapture ? "采集正在启动，请稍候。" : "采集中不能重新选择设备及应用，请先停止采集。");
                    return;
                }
                ShowDeviceAppDialog();
            });
            _selectTargetButton.HorizontalAlignment = HorizontalAlignment.Right;
            _selectTargetButton.VerticalAlignment = VerticalAlignment.Center;
            _selectTargetButton.Margin = new Thickness(0, 0, 18, 0);
            Border rightZone = ToolbarZone(_selectTargetButton, HorizontalAlignment.Right);
            Grid.SetColumn(rightZone, 2);
            bar.Children.Add(rightZone);

            return bar;
        }

        private Border CreateLeftPanel()
        {
            Border panel = new Border
            {
                Background = Brush(24, 25, 35),
                BorderBrush = Brush(37, 38, 51),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };
            Grid shell = new Grid();
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.Child = shell;

            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Style = DarkScrollViewerStyle()
            };
            StackPanel side = new StackPanel { Margin = new Thickness(14, 10, 14, 14) };
            scroll.Content = side;
            _paramsExpandedContent = scroll;
            shell.Children.Add(scroll);

            Grid titleRow = new Grid
            {
                Height = 48,
                Margin = new Thickness(-14, -10, -14, 12),
                Background = Brush(25, 26, 36)
            };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Border titleSeparator = ToolbarSeparator(0, 0, 0, 1);
            Grid.SetColumnSpan(titleSeparator, 2);
            titleRow.Children.Add(titleSeparator);
            titleRow.Children.Add(new Border
            {
                Width = 128,
                Height = 2,
                Background = AccentBlue,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0)
            });
            StackPanel titleContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            titleContent.Children.Add(new TextBlock
            {
                Text = "\uE9D2",
                FontFamily = ToolbarSymbolFont,
                Foreground = TextSecondary,
                FontSize = 16,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            titleContent.Children.Add(new TextBlock
            {
                Text = "参数面板",
                Foreground = TextPrimary,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            titleRow.Children.Add(titleContent);
            _paramsCollapseButton = PanelToggleButton("≪", "收起参数面板");
            _paramsCollapseButton.Click += delegate { ToggleParamsPanel(true); };
            _paramsCollapseButton.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetColumn(_paramsCollapseButton, 1);
            titleRow.Children.Add(_paramsCollapseButton);
            side.Children.Add(titleRow);

            _paramsExpandButton = PanelToggleButton("≫", "展开参数面板");
            _paramsExpandButton.Margin = new Thickness(4, 10, 4, 0);
            _paramsExpandButton.Visibility = Visibility.Collapsed;
            _paramsExpandButton.Click += delegate { ToggleParamsPanel(false); };
            Grid.SetColumn(_paramsExpandButton, 1);
            shell.Children.Add(_paramsExpandButton);

            _deviceCombo = new ComboBox { Visibility = Visibility.Collapsed };
            _appCombo = new ComboBox { Visibility = Visibility.Collapsed };
            _processCombo = new ComboBox { Visibility = Visibility.Collapsed };
            _appSearch = new TextBox { Visibility = Visibility.Collapsed };
            _processSearch = new TextBox { Visibility = Visibility.Collapsed };
            _bundleInput = new TextBox { Text = _selectedBundleId, Visibility = Visibility.Collapsed };
            _processHint = HintText("");
            _appCount = HintText("");
            _processCount = HintText("");

            _screenshotInterval = new Slider { Minimum = 3, Maximum = 3, Value = 3, Visibility = Visibility.Collapsed };

            side.Children.Add(CreateDataTabs());

            _screenshotMetricCheck = new CheckBox { IsChecked = true };
            _fpsMetricCheck = new CheckBox { IsChecked = true };
            _frameMetricCheck = new CheckBox { IsChecked = true };
            _memoryMetricCheck = new CheckBox { IsChecked = true };
            _cpuMetricCheck = new CheckBox { IsChecked = true };
            _temperatureMetricCheck = new CheckBox
            {
                IsChecked = true,
                ToolTip = "\u8bbe\u5907\u6e29\u5ea6\u3002Android \u663e\u793a\u7cfb\u7edf\u5b9e\u9645\u5f00\u653e\u7684 CPU\u3001GPU\u3001\u7535\u6c60\u3001\u673a\u8eab\u7b49\u4f20\u611f\u5668\uff1biOS \u975e\u8d8a\u72f1\u8bbe\u5907\u4ec5\u80fd\u8bfb\u53d6\u7535\u6c60\u6e29\u5ea6\u3002"
            };
            _thermalStateMetricCheck = new CheckBox
            {
                IsChecked = true,
                ToolTip = "系统热状态。iOS 为 0-3，Android 为 0-6；只显示设备实际开放的系统级状态，不可用时不填充。"
            };

            side.Children.Add(MetricCard("Screenshot", "设备截屏", AccentCyan, _screenshotMetricCheck, true));
            side.Children.Add(MetricCard("FPS", "帧率", AccentPink, _fpsMetricCheck, true));
            side.Children.Add(MetricCard("Display FrameTime", "窗口最大帧间隔(ms)", AccentPink, _frameMetricCheck, true));
            side.Children.Add(MetricCard("Process Memory", "所选 pid 内存", AccentGold, _memoryMetricCheck, true));
            side.Children.Add(MetricCard("Process CPU Raw", "所选 pid 原始 CPU", AccentGreen, _cpuMetricCheck, true));

            side.Children.Add(MetricCard("Device Temperature", "\u8bbe\u5907\u7ea7\u4f20\u611f\u5668\u6e29\u5ea6", AccentTemperature, _temperatureMetricCheck, true));
            side.Children.Add(MetricCard("Thermal State / Status", "iOS 0-3 / Android 0-6", AccentThermalState, _thermalStateMetricCheck, true));

            _preview = new Image { Visibility = Visibility.Collapsed };

            return panel;
        }

        private Grid CreateMainPanel()
        {
            Grid main = new Grid { Background = AppBackground };
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(166) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });

            Border headerFrame = new Border
            {
                Background = Brush(25, 26, 36),
                BorderBrush = Brush(37, 39, 52),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            StackPanel tab = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(18, 0, 0, 0)
            };
            tab.Children.Add(new TextBlock
            {
                Text = "\uE9D9",
                FontFamily = ToolbarSymbolFont,
                Foreground = TextSecondary,
                FontSize = 16,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            tab.Children.Add(new TextBlock
            {
                Text = "数据显示",
                Foreground = TextPrimary,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(tab);
            header.Children.Add(new Border
            {
                Width = 128,
                Height = 2,
                Background = AccentBlue,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0)
            });
            headerFrame.Child = header;
            main.Children.Add(headerFrame);

            Border shotsPanel = new Border
            {
                BorderBrush = Brush(37, 38, 51),
                BorderThickness = new Thickness(0, 1, 0, 1),
                Background = Brush(20, 21, 29),
                Padding = new Thickness(12, 8, 12, 8)
            };
            _shotStrip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 2)
            };
            _shotScrollViewer = new ScrollViewer
            {
                Content = _shotStrip,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                PanningMode = PanningMode.HorizontalOnly,
                Height = 150,
                Padding = new Thickness(0)
            };
            _shotScrollViewer.Resources.Add(typeof(ScrollBar), DarkHorizontalScrollBarStyle());
            _shotScrollViewer.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                if (_shotScrollViewer == null) return;
                _followLatest = false;
                _shotScrollViewer.ScrollToHorizontalOffset(_shotScrollViewer.HorizontalOffset - e.Delta);
                e.Handled = true;
            };
            _shotScrollViewer.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ClickCount >= 2)
                {
                    ScreenshotInfo shot = FindShotFromHit(e.OriginalSource as DependencyObject);
                    if (shot != null)
                    {
                        _suppressShotClick = true;
                        _draggingShots = false;
                        _shotDragMoved = false;
                        ShowScreenshotViewer(shot);
                        e.Handled = true;
                        return;
                    }
                }
                _draggingShots = true;
                _shotDragMoved = false;
                _suppressShotClick = false;
                _shotDragStart = e.GetPosition(_shotScrollViewer);
                _shotDragStartOffset = _shotScrollViewer.HorizontalOffset;
                _shotScrollViewer.CaptureMouse();
            };
            _shotScrollViewer.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!_draggingShots || e.LeftButton != MouseButtonState.Pressed || _shotScrollViewer == null) return;
                Point current = e.GetPosition(_shotScrollViewer);
                double delta = current.X - _shotDragStart.X;
                if (Math.Abs(delta) < 3) return;
                _shotDragMoved = true;
                _followLatest = false;
                _shotScrollViewer.ScrollToHorizontalOffset(_shotDragStartOffset + delta);
                e.Handled = true;
            };
            _shotScrollViewer.PreviewMouseLeftButtonUp += delegate
            {
                _draggingShots = false;
                _shotDragMoved = false;
                _shotScrollViewer.ReleaseMouseCapture();
            };
            _shotScrollViewer.MouseLeave += delegate
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed) _draggingShots = false;
            };
            shotsPanel.Child = _shotScrollViewer;
            Grid.SetRow(shotsPanel, 1);
            main.Children.Add(shotsPanel);
            _shotsPanel = shotsPanel;

            Grid ruler = new Grid
            {
                Background = Brush(19, 20, 28)
            };
            ruler.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            ruler.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _timeSummary = new TextBlock
            {
                Text = "时间轴  0s",
                Foreground = TextSecondary,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0)
            };
            ruler.Children.Add(_timeSummary);
            _targetSummary = new TextBlock
            {
                Text = "未开始采集",
                Foreground = TextSecondary,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(_targetSummary, 1);
            ruler.Children.Add(_targetSummary);
            Grid.SetRow(ruler, 2);
            main.Children.Add(ruler);

            Grid charts = new Grid { Margin = new Thickness(0, 0, 10, 0) };
            _fpsChartRowDef = new RowDefinition { Height = GridLength.Auto };
            _frameChartRowDef = new RowDefinition { Height = GridLength.Auto };
            _memoryChartRowDef = new RowDefinition { Height = GridLength.Auto };
            _cpuChartRowDef = new RowDefinition { Height = GridLength.Auto };
            _cpuNormalizedChartRowDef = new RowDefinition { Height = GridLength.Auto };
            _cpuCoreChartRowDef = new RowDefinition { Height = GridLength.Auto };
            _temperatureChartRowDef = new RowDefinition { Height = GridLength.Auto };
            _thermalStateChartRowDef = new RowDefinition { Height = GridLength.Auto };
            charts.RowDefinitions.Add(_fpsChartRowDef);
            charts.RowDefinitions.Add(_frameChartRowDef);
            charts.RowDefinitions.Add(_memoryChartRowDef);
            charts.RowDefinitions.Add(_cpuChartRowDef);
            charts.RowDefinitions.Add(_cpuNormalizedChartRowDef);
            charts.RowDefinitions.Add(_cpuCoreChartRowDef);
            charts.RowDefinitions.Add(_temperatureChartRowDef);
            charts.RowDefinitions.Add(_thermalStateChartRowDef);
            _fpsChart = new ChartCanvas { Mode = "fps" };
            _frameChart = new ChartCanvas { Mode = "frametime" };
            _memoryChart = new ChartCanvas { Mode = "memory" };
            _cpuChart = new ChartCanvas { Mode = "cpu" };
            _cpuNormalizedChart = new ChartCanvas { Mode = "cpunormalized" };
            _cpuCoreChart = new ChartCanvas { Mode = "corecpu" };
            _temperatureChart = new ChartCanvas { Mode = "temperature" };
            _thermalStateChart = new ChartCanvas { Mode = "thermalstate" };
            _fpsChart.TimeSelected += SelectTimeFromChart;
            _frameChart.TimeSelected += SelectTimeFromChart;
            _memoryChart.TimeSelected += SelectTimeFromChart;
            _cpuChart.TimeSelected += SelectTimeFromChart;
            _cpuNormalizedChart.TimeSelected += SelectTimeFromChart;
            _cpuCoreChart.TimeSelected += SelectTimeFromChart;
            _temperatureChart.TimeSelected += SelectTimeFromChart;
            _thermalStateChart.TimeSelected += SelectTimeFromChart;
            _fpsRow = ChartRow("FPS", new[] { "FPS", "Jank", "BigJank" }, _fpsChart, new Brush[] { AccentPink, AccentCyan, AccentGreen });
            charts.Children.Add(_fpsRow);
            Grid.SetRow(_fpsRow, 0);
            _frameRow = ChartRow("Display FrameTime", new[] { "FrameTime" }, _frameChart, new Brush[] { AccentPink });
            charts.Children.Add(_frameRow);
            Grid.SetRow(_frameRow, 1);
            _memoryRow = ChartRow("Process Memory", new[] { "Memory" }, _memoryChart, new Brush[] { AccentGold });
            charts.Children.Add(_memoryRow);
            Grid.SetRow(_memoryRow, 2);
            _cpuRow = ChartRow("Process CPU Raw", new[] { "CPU Raw" }, _cpuChart, new Brush[] { AccentGreen });
            charts.Children.Add(_cpuRow);
            Grid.SetRow(_cpuRow, 3);
            _cpuNormalizedRow = ChartRow("Process CPU Normalized", new[] { "CPU Normalized (0-100%)" }, _cpuNormalizedChart, new Brush[] { AccentCyan });
            charts.Children.Add(_cpuNormalizedRow);
            Grid.SetRow(_cpuNormalizedRow, 4);
            _cpuCoreRow = ChartRow("Device Core CPU", new string[0], _cpuCoreChart, new Brush[0], out _cpuCoreLegendPanel);
            charts.Children.Add(_cpuCoreRow);
            Grid.SetRow(_cpuCoreRow, 5);
            _temperatureRow = ChartRow("Device Temperature", new string[0], _temperatureChart, new Brush[0], out _temperatureLegendPanel);
            charts.Children.Add(_temperatureRow);
            Grid.SetRow(_temperatureRow, 6);
            _thermalStateRow = ChartRow("Thermal State / Status", new[] { "按设备平台显示 0-3 / 0-6" }, _thermalStateChart, new Brush[] { AccentThermalState });
            charts.Children.Add(_thermalStateRow);
            Grid.SetRow(_thermalStateRow, 7);
            _chartsScrollViewer = new ScrollViewer
            {
                Content = charts,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0),
                Style = DarkScrollViewerStyle()
            };
            Grid.SetRow(_chartsScrollViewer, 3);
            main.Children.Add(_chartsScrollViewer);

            AddDeviceInfoOverlay(main);
            return main;
        }

        private void WireEvents()
        {
            _deviceCombo.SelectionChanged += delegate
            {
                if (_loadingDevices) return;
                DeviceInfo device = _deviceCombo == null ? null : _deviceCombo.SelectedItem as DeviceInfo;
                if (device != null)
                {
                    if (_selectedDevice == null || !string.Equals(_selectedDevice.Udid, device.Udid, StringComparison.Ordinal))
                    {
                        _lockAndroidProcessToSelectedApp = false;
                    }
                    _selectedDevice = device;
                }
                RefreshTargetLists();
            };
            _appCombo.SelectionChanged += delegate
            {
                AppInfo app = _appCombo == null ? null : _appCombo.SelectedItem as AppInfo;
                if (app != null)
                {
                    _selectedApp = app;
                    _selectedBundleId = app.BundleId;
                    if (!_isRenderingAppSelection && DeviceLookupService.IsAndroid(_selectedDevice))
                    {
                        _lockAndroidProcessToSelectedApp = true;
                    }
                    if (_bundleInput != null) _bundleInput.Text = app.BundleId;
                    UpdateTargetSummary();
                }
            };
            _processCombo.SelectionChanged += delegate { ApplyProcess(_processCombo == null ? null : _processCombo.SelectedItem as ProcessInfo); };
            _screenshotMetricCheck.Checked += delegate { SyncMetricOptions(); };
            _screenshotMetricCheck.Unchecked += delegate { SyncMetricOptions(); };
            _fpsMetricCheck.Checked += delegate { SyncMetricOptions(); };
            _fpsMetricCheck.Unchecked += delegate { SyncMetricOptions(); };
            _frameMetricCheck.Checked += delegate { SyncMetricOptions(); };
            _frameMetricCheck.Unchecked += delegate { SyncMetricOptions(); };
            _memoryMetricCheck.Checked += delegate { SyncMetricOptions(); };
            _memoryMetricCheck.Unchecked += delegate { SyncMetricOptions(); };
            _cpuMetricCheck.Checked += delegate { SyncMetricOptions(); };
            _cpuMetricCheck.Unchecked += delegate { SyncMetricOptions(); };
            _temperatureMetricCheck.Checked += delegate { SyncMetricOptions(); };
            _temperatureMetricCheck.Unchecked += delegate { SyncMetricOptions(); };
            _thermalStateMetricCheck.Checked += delegate { SyncMetricOptions(); };
            _thermalStateMetricCheck.Unchecked += delegate { SyncMetricOptions(); };
            _screenshotInterval.ValueChanged += delegate { SyncScreenshotOptions(); };
            _collector.SampleReady += delegate(PerfSample sample) { Dispatcher.Invoke(new Action(delegate { AddSample(sample); })); };
            _collector.TargetConfirmationChanged += delegate(bool confirmed)
            {
                Dispatcher.Invoke(new Action(delegate { _targetIdentityConfirmed = confirmed; }));
            };
            _collector.Message += delegate(string message) { Dispatcher.Invoke(new Action(delegate { SetStatus(message); })); };
            _collector.Failed += delegate(string message) { Dispatcher.Invoke(new Action(delegate { OnCaptureFailed(message); })); };
            _screenshots.ScreenshotReady += delegate(ScreenshotInfo shot) { Dispatcher.Invoke(new Action(delegate { AddScreenshot(shot); })); };
            _screenshots.Failed += delegate(string message) { Dispatcher.Invoke(new Action(delegate { SetStatus(message); })); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (_fileOperationInProgress)
                {
                    e.Cancel = true;
                    SetStatus("文件操作进行中，请等待完成后再关闭。");
                    return;
                }
                if (_isCapturing && !_windowCloseConfirmed)
                {
                    e.Cancel = true;
                    if (_closeConfirmationShowing) return;
                    _closeConfirmationShowing = true;
                    MessageBoxResult result = MessageBox.Show(
                        this,
                        "当前正在采集，关闭将停止采集，未保存数据将丢失。确定关闭吗？",
                        "确认关闭",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    _closeConfirmationShowing = false;
                    if (result == MessageBoxResult.Yes)
                    {
                        StopCaptureInternal("采集已停止。");
                        _windowCloseConfirmed = true;
                        Dispatcher.BeginInvoke(new Action(delegate { Close(); }));
                    }
                }
            };
            Closed += delegate
            {
                _screenshots.Stop();
                _lifetime.Cancel();
                StopHydrateAppIcons();
                _collector.Dispose();
            };
            SyncMetricOptions();
        }

        private async void RefreshDevices()
        {
            await RefreshDevicesAsync();
        }

        private async Task RefreshDevicesAsync()
        {
            int revision = Interlocked.Increment(ref _deviceRefreshRevision);
            string previous = SelectedUdid();
            try
            {
                SetStatus("正在刷新设备...");
                DeviceDiscoveryReport discovery = await _lookup.DiscoverDevicesAsync(_lifetime.Token);
                List<DeviceInfo> devices = discovery.Devices;
                Dispatcher.Invoke(new Action(delegate
                {
                    if (revision != _deviceRefreshRevision) return;

                    _loadingDevices = true;
                    try
                    {
                        _devices = devices;
                        DeviceInfo selected = _devices.FirstOrDefault(delegate(DeviceInfo item) { return item.Udid == previous; }) ??
                            _devices.FirstOrDefault(delegate(DeviceInfo item) { return _selectedDevice != null && item.Udid == _selectedDevice.Udid; }) ??
                            _devices.FirstOrDefault();
                        _selectedDevice = selected;
                        if (_deviceCombo != null)
                        {
                            _deviceCombo.ItemsSource = _devices;
                            _deviceCombo.SelectedItem = selected;
                        }
                        if (selected == null)
                        {
                            ClearTargetSelectionState();
                        }
                    }
                    finally
                    {
                        _loadingDevices = false;
                    }

                    _deviceDiscoveryStatus = discovery.StatusMessage;
                    SetStatus(_deviceDiscoveryStatus);
                    UpdateDeviceInfoPanel();
                    UpdateDialogState();
                    if (_selectedDevice != null)
                    {
                        RefreshTargetLists();
                    }
                }));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                string path = CrashLogger.Write("刷新设备失败", ex);
                SetStatus("刷新设备失败：" + ex.Message + "；日志：" + path);
            }
        }

        private async void RefreshTargetLists()
        {
            int revision = Interlocked.Increment(ref _targetRefreshRevision);
            string udid = SelectedUdid();
            try
            {
                DeviceInfo device = _selectedDevice ?? (_deviceCombo == null ? null : _deviceCombo.SelectedItem as DeviceInfo);
                if (device == null || string.IsNullOrWhiteSpace(udid))
                {
                    ClearTargetSelectionState();
                    UpdateDialogState();
                    SetStatus("未检测到设备，请连接设备后刷新。");
                    return;
                }
                SetStatus("正在读取应用和进程...");
                Task<List<AppInfo>> appsTask = _lookup.ListAppsAsync(device, _lifetime.Token);
                Task<List<ProcessInfo>> processesTask = _lookup.ListProcessesAsync(device, _lifetime.Token);
                await Task.WhenAll(appsTask, processesTask);

                Dispatcher.Invoke(new Action(delegate
                {
                    if (revision != _targetRefreshRevision) return;
                    _apps = appsTask.Result;
                    _processes = processesTask.Result;
                    _processesRefreshedAtUtc = DateTime.UtcNow;
                    _processSnapshotUdid = device.Udid ?? "";
                    RenderApps();
                    RenderProcesses();
                    UpdateDialogState();
                    SetStatus(string.Format("应用 {0} 个，进程 {1} 个。", _apps.Count, _processes.Count));
                    StartHydrateAppIcons(device, revision);
                }));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                string path = CrashLogger.Write("刷新应用和进程失败", ex);
                SetStatus("刷新应用和进程失败：" + ex.Message + "；日志：" + path);
            }
        }

        private void ClearTargetSelectionState()
        {
            StopHydrateAppIcons();
            _apps = new List<AppInfo>();
            _processes = new List<ProcessInfo>();
            _selectedApp = null;
            _selectedProcess = null;
            _selectedBundleId = "";
            _lockAndroidProcessToSelectedApp = false;
            _processesRefreshedAtUtc = default(DateTime);
            _processSnapshotUdid = "";
            if (_appCombo != null)
            {
                _appCombo.ItemsSource = _apps;
                _appCombo.SelectedItem = null;
            }
            if (_processCombo != null)
            {
                _processCombo.ItemsSource = _processes;
                _processCombo.SelectedItem = null;
            }
            if (_dialogAppList != null)
            {
                _dialogAppList.ItemsSource = _apps;
                _dialogAppList.SelectedItem = null;
            }
            if (_dialogProcessList != null)
            {
                _dialogProcessList.ItemsSource = _processes;
                _dialogProcessList.SelectedItem = null;
            }
            if (_bundleInput != null) _bundleInput.Text = "";
            if (_appCount != null) _appCount.Text = "显示 0 / 0 个应用";
            if (_processCount != null) _processCount.Text = "显示 0 / 0 个进程";
            UpdateSelectedAppSummary(null);
            UpdateTargetSummary();
        }

        private void RenderApps()
        {
            if (_appCombo == null) return;
            List<AppInfo> items = null;
            AppInfo selected = null;
            bool wasRendering = _isRenderingAppSelection;
            _isRenderingAppSelection = true;
            try
            {
                List<string> terms = Terms(_appSearch == null ? "" : _appSearch.Text);
                items = _apps.Where(delegate(AppInfo app) { return Match(terms, app.Name, app.BundleId, app.Version, app.Reason); }).ToList();
                AppInfo previous = _appCombo.SelectedItem as AppInfo;
                _appCombo.ItemsSource = items;
                if (previous != null)
                {
                    selected = items.FirstOrDefault(delegate(AppInfo app) { return app.BundleId == previous.BundleId; });
                }
                selected = selected ??
                    items.FirstOrDefault(delegate(AppInfo app) { return app.BundleId == DefaultBundleForSelectedDevice(); }) ??
                    items.FirstOrDefault(delegate(AppInfo app) { return app.Recommended; }) ??
                    items.FirstOrDefault();
                _appCombo.SelectedItem = selected;
                if (selected != null)
                {
                    _selectedApp = selected;
                    _selectedBundleId = selected.BundleId;
                    if (_bundleInput != null) _bundleInput.Text = selected.BundleId;
                }
            }
            finally
            {
                _isRenderingAppSelection = wasRendering;
            }
            UpdateSelectedAppSummary(selected);
            if (_appCount != null) _appCount.Text = string.Format("显示 {0} / {1} 个应用", items.Count, _apps.Count);
            UpdateDeviceInfoPanel();
            UpdateDialogState();
        }

        private void RenderLaunchApps()
        {
            if (_dialogAppList == null) return;
            List<AppInfo> items = null;
            bool wasRendering = _isRenderingAppSelection;
            _isRenderingAppSelection = true;
            try
            {
                List<string> terms = Terms(_appLaunchSearch == null ? "" : _appLaunchSearch.Text);
                items = _apps.Where(delegate(AppInfo app) { return Match(terms, app.Name, app.BundleId, app.Version, app.Reason); }).ToList();
                _dialogAppList.ItemsSource = items;
                AppInfo selected = null;
                if (_selectedApp != null)
                {
                    selected = items.FirstOrDefault(delegate(AppInfo app) { return app.BundleId == _selectedApp.BundleId; });
                }
                selected = selected ??
                    items.FirstOrDefault(delegate(AppInfo app) { return app.BundleId == DefaultBundleForSelectedDevice(); }) ??
                    items.FirstOrDefault(delegate(AppInfo app) { return app.Recommended; }) ??
                    items.FirstOrDefault();
                _dialogAppList.SelectedItem = selected;
            }
            finally
            {
                _isRenderingAppSelection = wasRendering;
            }
            if (_appCount != null) _appCount.Text = string.Format("显示 {0} / {1} 个应用", items.Count, _apps.Count);
        }

        private void RenderProcesses()
        {
            if (_processCombo == null) return;
            RefreshIosPickerRecommendations(_processes, _selectedBundleId);
            string recommendationBundle = IosRecommendationBundle(_processes, _selectedBundleId);
            List<string> terms = Terms(_processSearch == null ? "" : _processSearch.Text);
            bool searching = terms.Count > 0;
            List<ProcessInfo> source = searching ? _processes : VisibleProcessesForPicker(_processes, recommendationBundle);
            List<ProcessInfo> items = source.Where(delegate(ProcessInfo process)
            {
                return Match(terms, process.Pid.ToString(), process.Name, process.BundleId, process.DisplayName, process.Reason, process.OwnerBundleId, process.OwnerDisplayName);
            }).ToList();
            ProcessInfo previous = _processCombo.SelectedItem as ProcessInfo;
            _processCombo.ItemsSource = items;
            ProcessInfo selected = null;
            List<ProcessInfo> ownedWechatContents = IosOwnedWebContentCandidates(items, recommendationBundle);
            bool explicitIosWechatSelectionRequired = ownedWechatContents.Count > 1;
            bool iosSelectionMustMatch = !DeviceLookupService.IsAndroid(_selectedDevice)
                && !string.IsNullOrWhiteSpace(recommendationBundle);
            bool lockedAndroidSelectedApp = DeviceLookupService.IsAndroid(_selectedDevice)
                && _lockAndroidProcessToSelectedApp
                && !string.IsNullOrWhiteSpace(recommendationBundle);
            ProcessInfo foregroundRecommended = items.FirstOrDefault(delegate(ProcessInfo p) { return p.Reason == "当前前台微信小游戏进程"; }) ??
                items.FirstOrDefault(delegate(ProcessInfo p) { return p.Reason == "当前前台微信相关进程"; }) ??
                items.FirstOrDefault(delegate(ProcessInfo p) { return p.Reason == "当前前台 Activity 进程"; }) ??
                items.FirstOrDefault(delegate(ProcessInfo p) { return p.Reason == "当前前台微信小游戏 WebContent 进程"; }) ??
                items.FirstOrDefault(delegate(ProcessInfo p) { return p.Reason == "当前前台 iOS APP 主进程"; });
            ProcessInfo selectedBundleProcess = PreferredProcessForSelectedBundle(items, recommendationBundle);
            if (foregroundRecommended != null
                && (!lockedAndroidSelectedApp
                    || ProcessTargetMatcher.IsAndroidProcessCompatibleWithBundle(foregroundRecommended, recommendationBundle)))
            {
                selected = foregroundRecommended;
            }
            else if (previous != null
                && IsProcessCompatibleWithSelectedBundle(previous, recommendationBundle)
                && (!explicitIosWechatSelectionRequired || ownedWechatContents.Any(delegate(ProcessInfo item) { return item.Pid == previous.Pid; })))
            {
                selected = items.FirstOrDefault(delegate(ProcessInfo p) { return p.Pid == previous.Pid && p.Name == previous.Name; });
            }
            if (!explicitIosWechatSelectionRequired)
            {
                selected = selected ?? selectedBundleProcess;
                if (!iosSelectionMustMatch && !lockedAndroidSelectedApp)
                {
                    selected = selected ??
                        items.FirstOrDefault(delegate(ProcessInfo p) { return p.BundleId == DefaultBundleForSelectedDevice(); }) ??
                        items.FirstOrDefault(delegate(ProcessInfo p) { return p.Recommended; }) ??
                        items.FirstOrDefault(delegate(ProcessInfo p) { return p.Name == "WeChat"; }) ??
                        items.FirstOrDefault();
                }
            }
            _processCombo.SelectedItem = selected;
            if (_dialogProcessList != null)
            {
                _dialogProcessList.ItemsSource = items;
                _dialogProcessList.SelectedItem = selected;
            }
            ApplyProcess(selected);
            if (_processCount != null) _processCount.Text = string.Format("显示 {0} / {1} 个进程", items.Count, _processes.Count);
            UpdateProcessEmptyState(items.Count == 0, terms.Count > 0);
            UpdateDeviceInfoPanel();
            UpdateDialogState();
            if (explicitIosWechatSelectionRequired && selected == null && _dialogStatus != null)
            {
                _dialogStatus.Text = "检测到多个归属微信的 WebContent，请手动选择当前小游戏 PID。";
            }
        }

        private Border CreateProcessEmptyState()
        {
            StackPanel content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 260
            };
            _processEmptyText = new TextBlock
            {
                Text = "当前 APP 尚未运行",
                Foreground = TextSecondary,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            _processEmptyLaunchButton = DialogButton("▶  启动所选APP", delegate
            {
                if (_selectedApp == null) SetDialogLaunchMode(true);
                else LaunchSelectedAppFromDialog();
            });
            _processEmptyLaunchButton.Width = 150;
            _processEmptyLaunchButton.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(_processEmptyText);
            content.Children.Add(_processEmptyLaunchButton);
            return new Border
            {
                Background = Brushes.Transparent,
                Child = content,
                Visibility = Visibility.Collapsed
            };
        }

        private void UpdateProcessEmptyState(bool empty, bool searching)
        {
            if (_processEmptyState == null) return;
            _processEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (_processEmptyText != null)
            {
                _processEmptyText.Text = searching ? "没有匹配的进程" : "当前 APP 尚未运行";
            }
            if (_processEmptyLaunchButton != null)
            {
                _processEmptyLaunchButton.Visibility = !searching && _selectedApp != null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private List<ProcessInfo> VisibleProcessesForPicker(List<ProcessInfo> processes)
        {
            return VisibleProcessesForPicker(processes, _selectedBundleId);
        }

        private List<ProcessInfo> VisibleProcessesForPicker(List<ProcessInfo> processes, string selectedBundleId)
        {
            if (processes == null) return new List<ProcessInfo>();
            if (DeviceLookupService.IsAndroid(_selectedDevice)) return processes;
            return processes.Where(delegate(ProcessInfo process) { return IsIosPickerProcess(process, selectedBundleId); }).ToList();
        }

        private void RefreshIosPickerRecommendations(List<ProcessInfo> processes, string selectedBundleId)
        {
            if (processes == null || DeviceLookupService.IsAndroid(_selectedDevice)) return;
            string recommendationBundle = IosRecommendationBundle(processes, selectedBundleId);
            bool hasForegroundApplication = processes.Any(delegate(ProcessInfo process) { return process.ForegroundApplication; });
            List<ProcessInfo> ownedWechatContents = IosOwnedWebContentCandidates(processes, recommendationBundle);
            foreach (ProcessInfo process in processes)
            {
                if (ProcessTargetMatcher.IsIosWebKitProcessRole(process.Name))
                {
                    process.Recommended = recommendationBundle == "com.tencent.xin"
                        && process.Name == "com.apple.WebKit.WebContent"
                        && ownedWechatContents.Count == 1
                        && ProcessTargetMatcher.IsIosOwnedWebProcess(process, recommendationBundle);
                    if (process.Recommended)
                    {
                        process.Reason = hasForegroundApplication
                            ? "当前前台微信小游戏 WebContent 进程"
                            : "当前所选微信小游戏 WebContent 进程";
                    }
                    continue;
                }

                bool appMain = !string.IsNullOrWhiteSpace(process.BundleId);
                if (!appMain) continue;
                bool foregroundMain = process.ForegroundApplication;
                bool selectedMain = !string.IsNullOrWhiteSpace(recommendationBundle)
                    && IsProcessForBundle(process, recommendationBundle);
                bool wechatChildPreferred = recommendationBundle == "com.tencent.xin"
                    && ownedWechatContents.Count > 0;
                process.Recommended = foregroundMain
                    ? !wechatChildPreferred
                    : !hasForegroundApplication && selectedMain && !wechatChildPreferred;
                process.Reason = foregroundMain
                    ? "当前前台 iOS APP 主进程"
                    : selectedMain ? "当前所选 APP 主进程" : "可测试 iOS APP 进程";
            }
        }

        private static string IosRecommendationBundle(List<ProcessInfo> processes, string selectedBundleId)
        {
            if (processes != null)
            {
                ProcessInfo foreground = processes.FirstOrDefault(delegate(ProcessInfo process)
                {
                    return process.ForegroundApplication && !string.IsNullOrWhiteSpace(process.BundleId);
                });
                if (foreground != null) return foreground.BundleId;
            }
            return selectedBundleId ?? "";
        }

        private static bool IsIosPickerProcess(ProcessInfo process, string selectedBundleId)
        {
            return ProcessTargetMatcher.IsIosDefaultPickerProcess(process, selectedBundleId);
        }

        private static ProcessInfo PreferredProcessForSelectedBundle(List<ProcessInfo> items, string bundleId)
        {
            if (items == null || string.IsNullOrWhiteSpace(bundleId)) return null;
            List<ProcessInfo> ownedWechatContents = IosOwnedWebContentCandidates(items, bundleId);
            if (ownedWechatContents.Count == 1) return ownedWechatContents[0];
            if (ownedWechatContents.Count > 1) return null;
            ProcessInfo mainProcess = items.FirstOrDefault(delegate(ProcessInfo process)
            {
                return string.Equals(process.Name, bundleId, StringComparison.Ordinal);
            });
            if (mainProcess != null) return mainProcess;
            ProcessInfo exact = items.FirstOrDefault(delegate(ProcessInfo process) { return IsProcessForBundle(process, bundleId); });
            if (exact != null) return exact;
            if (bundleId == "com.tencent.xin")
            {
                return items.FirstOrDefault(IsIosWechatRelatedProcess);
            }
            return null;
        }

        private static List<ProcessInfo> IosOwnedWebContentCandidates(List<ProcessInfo> items, string bundleId)
        {
            if (items == null || bundleId != "com.tencent.xin") return new List<ProcessInfo>();
            return items.Where(delegate(ProcessInfo process)
            {
                return process.Name == "com.apple.WebKit.WebContent"
                    && ProcessTargetMatcher.IsIosOwnedWebProcess(process, bundleId);
            }).ToList();
        }

        private static bool IsProcessCompatibleWithSelectedBundle(ProcessInfo process, string bundleId)
        {
            if (process == null) return false;
            if (string.IsNullOrWhiteSpace(bundleId)) return true;
            if (IsProcessForBundle(process, bundleId)) return true;
            return bundleId == "com.tencent.xin" && IsIosWechatRelatedProcess(process);
        }

        private static bool IsProcessForBundle(ProcessInfo process, string bundleId)
        {
            if (process == null || string.IsNullOrWhiteSpace(bundleId)) return false;
            if (string.Equals(process.BundleId, bundleId, StringComparison.Ordinal)) return true;
            if (ProcessTargetMatcher.IsIosOwnedWebProcess(process, bundleId)) return true;
            return string.Equals(ProcessBundleFromName(process.Name), bundleId, StringComparison.Ordinal);
        }

        private static bool IsIosWechatRelatedProcess(ProcessInfo process)
        {
            if (process == null) return false;
            if (process.BundleId == "com.tencent.xin" || process.Name == "WeChat") return true;
            return ProcessTargetMatcher.IsIosOwnedWebProcess(process, "com.tencent.xin");
        }

        private void StartHydrateAppIcons(DeviceInfo device, int targetRevision)
        {
            StopHydrateAppIcons();
            if (!DeviceLookupService.IsAndroid(device) || _apps.Count == 0) return;
            int iconRevision = Interlocked.Increment(ref _iconHydrateRevision);
            List<AppInfo> appsSnapshot = _apps;
            List<ProcessInfo> processesSnapshot = _processes;
            _iconHydrateCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            CancellationToken token = _iconHydrateCts.Token;
            Task.Run(async delegate
            {
                try
                {
                    await _lookup.HydrateAppIconsAsync(device, appsSnapshot, processesSnapshot, 18, token);
                    _ = Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (token.IsCancellationRequested || iconRevision != _iconHydrateRevision || targetRevision != _targetRefreshRevision) return;
                        RenderApps();
                        RenderLaunchApps();
                        RenderProcesses();
                    }), DispatcherPriority.Background);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    string path = CrashLogger.Write("读取 Android APP 图标失败", ex);
                    SetStatus("读取 Android APP 图标失败：" + ex.Message + "；日志：" + path);
                }
            }, token);
        }

        private void StopHydrateAppIcons()
        {
            Interlocked.Increment(ref _iconHydrateRevision);
            CancellationTokenSource cts = _iconHydrateCts;
            _iconHydrateCts = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        private void ApplyProcess()
        {
            ApplyProcess(_processCombo == null ? null : _processCombo.SelectedItem as ProcessInfo);
        }

        private void ApplyProcess(ProcessInfo process)
        {
            if (process == null)
            {
                if (_processHint != null) _processHint.Text = "未选择进程。";
                _selectedProcess = null;
                UpdateTargetSummary();
                UpdateDialogState();
                return;
            }
            _selectedProcess = process;
            string processBundle = FirstNonEmpty(process.OwnerBundleId, process.BundleId);
            if (!string.IsNullOrWhiteSpace(processBundle))
            {
                _selectedBundleId = processBundle;
                if (_bundleInput != null) _bundleInput.Text = processBundle;
            }
            SyncAppSummaryFromProcess(process);
            if (_processHint != null) _processHint.Text = string.Format("当前进程：pid {0} / {1} / {2} / {3}", process.Pid, process.Name, process.BundleId, process.Reason);
            UpdateTargetSummary();
            UpdateDialogState();
        }

        private void SyncAppSummaryFromProcess(ProcessInfo process)
        {
            if (process == null) return;
            if (ProcessTargetMatcher.IsIosWebKitProcessRole(process.Name) && string.IsNullOrWhiteSpace(process.OwnerBundleId)) return;
            string bundle = FirstNonEmpty(process.OwnerBundleId, process.BundleId, ProcessBundleFromName(process.Name));
            if (string.IsNullOrWhiteSpace(bundle)) return;
            AppInfo app = _apps.FirstOrDefault(delegate(AppInfo item) { return item.BundleId == bundle; });
            if (app == null)
            {
                app = new AppInfo
                {
                    BundleId = bundle,
                    Name = FirstNonEmpty(process.DisplayName, bundle, process.Name),
                    Platform = process.Platform,
                    IconKey = process.IconKey,
                    IconPath = process.IconPath
                };
            }
            _selectedApp = app;
            _selectedBundleId = app.BundleId;
            if (_bundleInput != null) _bundleInput.Text = app.BundleId;
            UpdateSelectedAppSummary(app);
        }

        private Border CreateSelectedAppSummaryCard()
        {
            Border card = new Border
            {
                Background = Brush(49, 49, 63),
                BorderBrush = Brush(49, 49, 63),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 10, 4),
                Height = 36
            };
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border iconHost = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(5),
                Background = Brush(64, 65, 80),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid iconLayer = new Grid();
            _selectedAppSummaryImage = new Image
            {
                Width = 26,
                Height = 26,
                Stretch = Stretch.UniformToFill
            };
            _selectedAppSummaryGlyph = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconLayer.Children.Add(_selectedAppSummaryImage);
            iconLayer.Children.Add(_selectedAppSummaryGlyph);
            iconHost.Child = iconLayer;
            row.Children.Add(iconHost);

            StackPanel text = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            _selectedAppSummaryName = new TextBlock
            {
                Foreground = TextPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _selectedAppSummaryBundle = new TextBlock
            {
                Foreground = TextSecondary,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, -1, 0, 0)
            };
            text.Children.Add(_selectedAppSummaryName);
            text.Children.Add(_selectedAppSummaryBundle);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            card.Child = row;
            return card;
        }

        private void UpdateSelectedAppSummary(AppInfo app)
        {
            if (_selectedAppSummaryCard == null) return;
            string name = app == null ? "请选择APP" : FirstNonEmpty(app.Name, app.BundleId);
            string bundle = app == null ? "" : app.BundleId;
            if (_selectedAppSummaryName != null) _selectedAppSummaryName.Text = name;
            if (_selectedAppSummaryBundle != null) _selectedAppSummaryBundle.Text = bundle;
            bool hasIcon = app != null && !string.IsNullOrWhiteSpace(app.IconPath) && File.Exists(app.IconPath);
            if (_selectedAppSummaryImage != null)
            {
                _selectedAppSummaryImage.Source = hasIcon ? LoadImageOriginal(app.IconPath) : null;
                _selectedAppSummaryImage.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_selectedAppSummaryGlyph != null)
            {
                _selectedAppSummaryGlyph.Text = AppIconGlyph(app == null ? "" : app.IconKey);
                _selectedAppSummaryGlyph.Visibility = hasIcon ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private async void LaunchSelectedAppFromDialog()
        {
            DeviceInfo device = _deviceCombo == null ? _selectedDevice : _deviceCombo.SelectedItem as DeviceInfo;
            AppInfo app = _dialogAppList == null ? _selectedApp : _dialogAppList.SelectedItem as AppInfo;
            if (app == null) app = _appCombo == null ? null : _appCombo.SelectedItem as AppInfo;
            if (device == null)
            {
                SetStatus("请先选择设备。");
                return;
            }
            if (app == null || string.IsNullOrWhiteSpace(app.BundleId))
            {
                SetStatus("请先选择要启动的 APP。");
                return;
            }
            if (DeviceLookupService.IsAndroid(device)) _lockAndroidProcessToSelectedApp = true;
            try
            {
                SetButtonEnabled(_dialogLaunchButton, false);
                SetStatus("正在启动 APP：" + app.Name + " / " + app.BundleId);
                ProcessResult result = await _lookup.LaunchAppAsync(device, app, _lifetime.Token);
                if (result.ExitCode != 0)
                {
                    if (device.Platform == "ios" && IosLookupService.IsDeveloperModeDisabled(result))
                    {
                        SetStatus("启动 APP 失败：iOS 设备未开启开发者模式。");
                        ShowIosDeveloperModeDisabledDialog();
                    }
                    else
                    {
                        SetStatus("启动 APP 失败：" + FirstNonEmpty(result.Stderr, result.Stdout, "未知错误"));
                    }
                    return;
                }
                _selectedDevice = device;
                _selectedApp = app;
                _selectedBundleId = app.BundleId;
                if (_appCombo != null) _appCombo.SelectedItem = app;
                if (_bundleInput != null) _bundleInput.Text = app.BundleId;
                SetStatus("已启动 APP：" + app.Name + "，正在等待主进程...");
                bool found = await RefreshProcessesAfterLaunchAsync(device, app);
                SetDialogLaunchMode(false);
                SetStatus(found
                    ? "已启动 APP：" + app.Name + "，已自动选择当前主进程。"
                    : "APP 已启动，但暂未读取到主进程，请点击刷新后再选择。");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                string path = CrashLogger.Write("启动 APP 失败", ex);
                SetStatus("启动 APP 失败：" + ex.Message + "；日志：" + path);
            }
            finally
            {
                SetButtonEnabled(_dialogLaunchButton, true);
            }
        }

        private void ShowIosDeveloperModeDisabledDialog()
        {
            Window dialog = new Window
            {
                Title = "无法启动 APP",
                Owner = this,
                Width = 430,
                Height = 285,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                Topmost = true,
                Background = Brush(28, 28, 37),
                Foreground = TextPrimary,
                FontFamily = FontFamily,
                ShowInTaskbar = false
            };
            Border shell = new Border
            {
                Background = Brush(28, 28, 37),
                BorderBrush = Brush(62, 63, 78),
                BorderThickness = new Thickness(1)
            };
            Grid root = new Grid { Margin = new Thickness(28, 20, 28, 24) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            shell.Child = root;
            dialog.Content = shell;

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            header.MouseLeftButtonDown += delegate { try { dialog.DragMove(); } catch { } };
            header.Children.Add(new TextBlock
            {
                Text = "无法启动 APP",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });
            Button close = DialogCloseButton(delegate { dialog.Close(); });
            close.Width = 34;
            close.Height = 34;
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            root.Children.Add(header);

            StackPanel content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            };
            content.Children.Add(new TextBlock
            {
                Text = "×",
                FontSize = 48,
                FontWeight = FontWeights.Light,
                Foreground = AccentPink,
                HorizontalAlignment = HorizontalAlignment.Center,
                LineHeight = 48
            });
            content.Children.Add(new TextBlock
            {
                Text = "iOS 设备未开启开发者模式",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8)
            });
            content.Children.Add(new TextBlock
            {
                Text = "请在设备“设置 > 隐私与安全性”中开启开发者模式，\n按提示重启设备后，再返回 MoTuPerf 重试。",
                FontSize = 13,
                Foreground = TextSecondary,
                TextAlignment = TextAlignment.Center,
                LineHeight = 22,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            Grid.SetRow(content, 1);
            root.Children.Add(content);

            Button confirm = DialogButton("知道了", delegate { dialog.Close(); });
            confirm.Width = 104;
            confirm.Background = AccentBlue;
            confirm.BorderBrush = AccentBlue;
            confirm.Foreground = Brushes.White;
            confirm.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(confirm, 2);
            root.Children.Add(confirm);
            dialog.ShowDialog();
        }

        private void ShowHelpDialog()
        {
            Window dialog = new Window
            {
                Title = "使用帮助",
                Owner = this,
                Width = 620,
                Height = 470,
                MinWidth = 560,
                MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                Background = Brush(28, 28, 37),
                Foreground = TextPrimary,
                FontFamily = FontFamily,
                ShowInTaskbar = false
            };

            Border shell = new Border
            {
                Background = Brush(28, 28, 37),
                BorderBrush = Brush(62, 63, 78),
                BorderThickness = new Thickness(1)
            };
            Grid root = new Grid { Margin = new Thickness(24, 18, 24, 22) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            shell.Child = root;
            dialog.Content = shell;

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            header.MouseLeftButtonDown += delegate { try { dialog.DragMove(); } catch { } };
            StackPanel title = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            title.Children.Add(new TextBlock
            {
                Text = "\uE897",
                FontFamily = ToolbarSymbolFont,
                Foreground = AccentBlue,
                FontSize = 18,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            title.Children.Add(new TextBlock
            {
                Text = "使用帮助",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(title);
            Button close = DialogCloseButton(delegate { dialog.Close(); });
            close.Width = 34;
            close.Height = 34;
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            root.Children.Add(header);

            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Style = DarkScrollViewerStyle()
            };
            StackPanel content = new StackPanel { Margin = new Thickness(0, 8, 6, 10) };
            content.Children.Add(HelpSection(
                "使用说明",
                "1. 连接 iOS 或 Android 真机，并在设备上完成信任、解锁、开发者模式或 USB 调试授权。\n" +
                "2. 点击右上角“选择设备及应用”，选择设备、应用和需要统计的进程。\n" +
                "3. 勾选左侧需要采集的指标后点击“开始采集”，再次点击同一按钮停止采集。\n" +
                "4. 采集过程中不能重新选择设备及应用；停止或设备断开时，可按提示保存现场文件。\n" +
                "5. “保存”用于保存可恢复现场的 .motuperf 文件；“打开”可恢复曲线和截图；“导出”只导出表格数据。"));
            content.Children.Add(HelpSection(
                "指标口径",
                "Android 与 iOS 有序帧源按 PerfDog 口径计算 Jank；BigJank 同时计入 Jank；FPS-only 回退不生成 Display FrameTime/Jank。\n" +
                "FrameTime 是相邻显示帧之间的时间间隔（单位 ms，60Hz 约为 16.7ms）；界面显示当前采样窗口实际观测到的最大帧间隔，Mean/P95/Max 仍保留在导出数据中。"));
            content.Children.Add(HelpSection(
                "数据来源",
                "USB 真实数据；CPU/内存按所选 pid；iOS FPS 为屏幕级，Android FPS 为应用/Surface 级；无模拟模式。"));
            scroll.Content = content;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            Button confirm = DialogButton("知道了", delegate { dialog.Close(); });
            confirm.Width = 104;
            confirm.Background = AccentBlue;
            confirm.BorderBrush = AccentBlue;
            confirm.Foreground = Brushes.White;
            confirm.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(confirm, 2);
            root.Children.Add(confirm);

            dialog.ShowDialog();
        }

        private static UIElement HelpSection(string title, string body)
        {
            Border section = new Border
            {
                Background = Brush(23, 24, 33),
                BorderBrush = Brush(44, 46, 62),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            StackPanel stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = TextPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            stack.Children.Add(new TextBlock
            {
                Text = body,
                Foreground = TextSecondary,
                FontSize = 13,
                LineHeight = 23,
                TextWrapping = TextWrapping.Wrap
            });
            section.Child = stack;
            return section;
        }

        private async Task<bool> RefreshProcessesAfterLaunchAsync(DeviceInfo device, AppInfo app)
        {
            int revision = Interlocked.Increment(ref _targetRefreshRevision);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(attempt == 0 ? 300 : 350, _lifetime.Token);
                List<ProcessInfo> processes = await _lookup.ListProcessesAsync(device, _lifetime.Token);
                if (revision != _targetRefreshRevision) return false;
                _processes = processes ?? new List<ProcessInfo>();
                _processesRefreshedAtUtc = DateTime.UtcNow;
                _processSnapshotUdid = device == null ? "" : device.Udid ?? "";
                _selectedApp = app;
                _selectedBundleId = app == null ? "" : app.BundleId;
                RenderProcesses();
                UpdateDialogState();
                if (_selectedProcess != null && IsProcessCompatibleWithSelectedBundle(_selectedProcess, _selectedBundleId))
                {
                    return true;
                }
            }
            return false;
        }

        private async void StartCapture()
        {
            if (_isCapturing || _isStartingCapture) return;
            _isStartingCapture = true;
            UpdateCaptureButton();
            try
            {
                StopHydrateAppIcons();
                ProcessInfo process = _selectedProcess;
                string bundleId = string.IsNullOrWhiteSpace(_selectedBundleId) ? DefaultBundleForSelectedDevice() : _selectedBundleId.Trim();
                DeviceInfo device = _selectedDevice;
                string udid = SelectedUdid();
                if (string.IsNullOrWhiteSpace(udid))
                {
                    SetStatus("未选择在线设备，请点击“选择设备及应用”重新选择。");
                    return;
                }
                if (process == null || process.Pid <= 0)
                {
                    SetStatus("未选择有效进程，请点击“选择设备及应用”选择具体 pid 后再开始采集。");
                    return;
                }
                if (_devices.Count == 0 || !_devices.Any(delegate(DeviceInfo item) { return item.Udid == udid; }))
                {
                    SetStatus("当前选择的设备不在在线列表中，请重新刷新并选择设备。");
                    RefreshDevices();
                    return;
                }
                process = await ResolveProcessForCaptureStartAsync(device, process, bundleId);
                if (process == null || process.Pid <= 0)
                {
                    SetStatus("当前选择的 pid 已不存在，请点击“选择设备及应用”刷新并重新选择正在运行的进程。");
                    return;
                }
                if (DeviceLookupService.IsAndroid(device)
                    && await _lookup.IsAndroidHomeProcessAsync(device, process, _lifetime.Token))
                {
                    ApplyProcess(null);
                    RefreshProcessPickerAfterStartValidation(null);
                    SetStatus("所选 pid 是系统桌面进程，无法统计游戏 FPS。请刷新并选择正在运行的游戏进程后再开始采集。");
                    return;
                }
                bundleId = string.IsNullOrWhiteSpace(_selectedBundleId) ? bundleId : _selectedBundleId.Trim();
                ClearSamples();
                CaptureConfig config = new CaptureConfig
                {
                    Platform = DeviceLookupService.IsAndroid(device) ? "android" : "ios",
                    Udid = udid,
                    BundleId = bundleId,
                    TargetPid = process == null ? (int?)null : process.Pid,
                    TargetName = process == null ? "" : process.Name,
                    TargetStartAbsTime = process == null ? 0 : process.StartAbsTime,
                    TargetAndroidStartTimeTicks = process == null ? 0 : process.AndroidStartTimeTicks,
                    TargetCoalitionId = process == null ? 0 : process.CoalitionId,
                    TargetOwnerPid = process == null ? 0 : process.OwnerPid,
                    TargetOwnerName = process == null ? "" : process.OwnerName,
                    ProductVersion = device.ProductVersion ?? "",
                    CollectFps = _fpsMetricCheck.IsChecked == true || _frameMetricCheck.IsChecked == true,
                    CollectMemory = _memoryMetricCheck.IsChecked == true,
                    CollectCpu = _cpuMetricCheck.IsChecked == true,
                    CollectTemperature = _temperatureMetricCheck.IsChecked == true,
                    CollectThermalState = !DeviceLookupService.IsAndroid(device) && _thermalStateMetricCheck.IsChecked == true,
                    CaptureScreenshots = _screenshotMetricCheck.IsChecked == true,
                    ScreenshotIntervalSec = 3
                };
                _screenshots.Udid = config.Udid;
                _screenshots.Platform = config.Platform;
                _screenshots.ProductVersion = config.ProductVersion;
                _screenshots.Enabled = config.CaptureScreenshots;
                _screenshots.IntervalSec = config.ScreenshotIntervalSec;
                _screenshots.Reset(_lifetime.Token);
                _targetIdentityConfirmed = false;
                _realMetricSampleSeen = false;
                _captureExpectsMetricData = config.CollectFps || config.CollectMemory || config.CollectCpu || config.CollectTemperature || config.CollectThermalState;
                _sessionStartedAt = DateTime.Now;
                _collector.Start(config);
                _isCapturing = true;
                _captureFailureHandled = false;
                StartDeviceMonitor(config.Udid, config.Platform);
                UpdateCaptureButton();
                _targetSummary.Text = "采集中：pid " + config.TargetPid + (string.IsNullOrWhiteSpace(config.TargetName) ? "" : " / " + config.TargetName);
            }
            catch (Exception ex)
            {
                _screenshots.Stop();
                string path = CrashLogger.Write("开始采集失败", ex);
                SetStatus("开始采集失败：" + ex.Message + "；日志：" + path);
            }
            finally
            {
                _isStartingCapture = false;
                UpdateCaptureButton();
            }
        }

        private async Task<ProcessInfo> ResolveProcessForCaptureStartAsync(DeviceInfo device, ProcessInfo selected, string bundleId)
        {
            if (selected == null || selected.Pid <= 0)
            {
                return selected;
            }

            if (!DeviceLookupService.IsAndroid(device))
            {
                return await ResolveIosProcessForCaptureStartAsync(device, selected, bundleId);
            }

            if (device != null && ProcessTargetMatcher.CanReuseAndroidSelectionForCapture(selected, device.Udid))
            {
                ApplyProcess(selected);
                return selected;
            }

            SetStatus("正在确认当前 Android 进程仍在运行...");
            List<ProcessInfo> liveProcesses = await _lookup.ListProcessesAsync(device, _lifetime.Token);
            if (liveProcesses == null || liveProcesses.Count == 0)
            {
                return null;
            }
            _processes = liveProcesses;

            ProcessInfo samePidCandidate = liveProcesses.FirstOrDefault(delegate(ProcessInfo process)
            {
                return process.Pid == selected.Pid;
            });
            ProcessInfo samePid = liveProcesses.FirstOrDefault(delegate(ProcessInfo process)
            {
                return ProcessTargetMatcher.SameAndroidProcessInstance(process, selected);
            });
            if (samePid != null)
            {
                ApplyProcess(samePid);
                RefreshProcessPickerAfterStartValidation(samePid);
                return samePid;
            }
            if (samePidCandidate != null && samePid == null)
            {
                RefreshProcessPickerAfterStartValidation(null);
                SetStatus(string.Format("所选 pid {0} 的进程实例已变化或无法复核，请重新选择后再开始采集。", selected.Pid));
                return null;
            }

            List<ProcessInfo> candidates = liveProcesses.Where(delegate(ProcessInfo process)
            {
                return ProcessTargetMatcher.SameAndroidTarget(process, selected, bundleId);
            }).ToList();
            if (candidates.Count == 1)
            {
                ProcessInfo replacement = candidates[0];
                ApplyProcess(replacement);
                RefreshProcessPickerAfterStartValidation(replacement);
                SetStatus(string.Format("检测到原 pid {0} 已变化，已自动切换到当前 pid {1}。", selected.Pid, replacement.Pid));
                return replacement;
            }

            RefreshProcessPickerAfterStartValidation(null);
            return null;
        }

        private async Task<ProcessInfo> ResolveIosProcessForCaptureStartAsync(DeviceInfo device, ProcessInfo selected, string bundleId)
        {
            ProcessInfo cached = CachedIosProcessForCaptureStart(device, selected, bundleId);
            if (cached != null)
            {
                ApplyProcess(cached);
                return cached;
            }
            SetStatus("正在确认当前 iOS 进程与所选应用匹配...");
            List<ProcessInfo> liveProcesses = await _lookup.ListProcessesAsync(device, _lifetime.Token);
            if (liveProcesses == null || liveProcesses.Count == 0)
            {
                return null;
            }
            _processes = liveProcesses;
            _processesRefreshedAtUtc = DateTime.UtcNow;
            _processSnapshotUdid = device == null ? "" : device.Udid ?? "";

            ProcessInfo samePid = liveProcesses.FirstOrDefault(delegate(ProcessInfo process)
            {
                return process.Pid == selected.Pid && process.Name == selected.Name;
            });
            if (samePid != null && IsProcessCompatibleWithSelectedBundle(samePid, bundleId))
            {
                ApplyProcess(samePid);
                RefreshProcessPickerAfterStartValidation(samePid);
                return samePid;
            }

            ProcessInfo replacement = PreferredReplacementForSelectedProcess(liveProcesses, selected, bundleId);
            if (replacement != null)
            {
                ApplyProcess(replacement);
                RefreshProcessPickerAfterStartValidation(replacement);
                if (samePid != null)
                {
                    SetStatus(string.Format("所选 pid {0} 不属于当前应用，已自动切换到应用进程 pid {1}。", selected.Pid, replacement.Pid));
                }
                else
                {
                    SetStatus(string.Format("检测到原 pid {0} 已变化，已自动切换到当前 iOS 应用进程 pid {1}。", selected.Pid, replacement.Pid));
                }
                return replacement;
            }

            if (samePid != null)
            {
                ApplyProcess(samePid);
                RefreshProcessPickerAfterStartValidation(samePid);
                return samePid;
            }

            RefreshProcessPickerAfterStartValidation(null);
            return null;
        }

        private ProcessInfo CachedIosProcessForCaptureStart(DeviceInfo device, ProcessInfo selected, string bundleId)
        {
            if (device == null || selected == null) return null;

            bool modernBoundSelection = IosLookupService.UsesRsd(device.ProductVersion)
                && ProcessTargetMatcher.BelongsToDevice(selected, device.Udid)
                && (IsProcessCompatibleWithSelectedBundle(selected, bundleId) || IsIosWebKitProcessRole(selected.Name));
            if (modernBoundSelection)
            {
                ProcessInfo sameSnapshotProcess = _processes.FirstOrDefault(delegate(ProcessInfo process)
                {
                    return process.Pid == selected.Pid
                        && process.Name == selected.Name
                        && ProcessTargetMatcher.BelongsToDevice(process, device.Udid)
                        && IsProcessCompatibleWithSelectedBundle(process, bundleId);
                });
                return sameSnapshotProcess ?? selected;
            }

            if (_processesRefreshedAtUtc == default(DateTime)) return null;
            if (!string.Equals(_processSnapshotUdid, device.Udid ?? "", StringComparison.Ordinal)) return null;
            ProcessInfo cached = _processes.FirstOrDefault(delegate(ProcessInfo process)
            {
                return process.Pid == selected.Pid && process.Name == selected.Name;
            });
            if (cached == null || !IsProcessCompatibleWithSelectedBundle(cached, bundleId)) return null;

            double ageSeconds = (DateTime.UtcNow - _processesRefreshedAtUtc).TotalSeconds;
            return ageSeconds >= 0 && ageSeconds <= FreshIosProcessSnapshotSeconds ? cached : null;
        }

        private static ProcessInfo PreferredReplacementForSelectedProcess(List<ProcessInfo> liveProcesses, ProcessInfo selected, string bundleId)
        {
            if (liveProcesses == null || selected == null) return null;
            if (IsIosWebKitProcessRole(selected.Name)) return null;
            ProcessInfo sameRole = liveProcesses
                .Where(delegate(ProcessInfo process) { return string.Equals(process.Name, selected.Name, StringComparison.Ordinal); })
                .OrderByDescending(delegate(ProcessInfo process) { return process.Pid; })
                .FirstOrDefault();
            if (sameRole != null && IsProcessCompatibleWithSelectedBundle(sameRole, bundleId)) return sameRole;
            return PreferredProcessForSelectedBundle(liveProcesses, bundleId);
        }

        private static bool IsIosWebKitProcessRole(string processName)
        {
            return ProcessTargetMatcher.IsIosWebKitProcessRole(processName);
        }

        private void RefreshProcessPickerAfterStartValidation(ProcessInfo selected)
        {
            if (_processCombo != null)
            {
                _processCombo.ItemsSource = _processes;
                if (selected != null) _processCombo.SelectedItem = selected;
            }
            if (_dialogProcessList != null)
            {
                _dialogProcessList.ItemsSource = _processes;
                if (selected != null) _dialogProcessList.SelectedItem = selected;
            }
            if (_processCount != null) _processCount.Text = string.Format("显示 {0} / {1} 个进程", _processes.Count, _processes.Count);
        }

        private void StopCapture()
        {
            StopCaptureInternal("采集已停止。");
            PromptSaveSession("采集已停止，是否保存本次数据？");
        }

        private void OnCaptureFailed(string message)
        {
            if (_captureFailureHandled) return;
            _captureFailureHandled = true;
            StopCaptureInternal(message);
            PromptSaveSession(message + "\n\n是否保存当前已采集的数据？");
        }

        private void StopCaptureInternal(string status)
        {
            StopDeviceMonitor();
            _screenshots.Stop();
            _collector.Stop();
            _isCapturing = false;
            UpdateCaptureButton();
            SetStatus(status);
        }

        private void PromptSaveSession(string message)
        {
            if (_samples.Count == 0 && _shots.Count == 0) return;
            MessageBoxResult result = MessageBox.Show(this, message, "保存采集数据", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                SaveSessionFile();
            }
        }

        private void StartDeviceMonitor(string udid, string platform)
        {
            StopDeviceMonitor();
            if (string.IsNullOrWhiteSpace(udid)) return;
            _deviceMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            CancellationToken token = _deviceMonitorCts.Token;
            Task.Run(async delegate
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        bool? online = await _lookup.IsDeviceOnlineAsync(udid, platform, token);
                        if (online == false && !token.IsCancellationRequested)
                        {
                            Dispatcher.Invoke(new Action(delegate
                            {
                                if (_isCapturing) OnCaptureFailed("检测到设备已断开，采集已停止。");
                            }));
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                    }
                }
            }, token);
        }

        private void StopDeviceMonitor()
        {
            if (_deviceMonitorCts == null) return;
            _deviceMonitorCts.Cancel();
            _deviceMonitorCts.Dispose();
            _deviceMonitorCts = null;
        }

        private async void ShowDeviceAppDialog()
        {
            if (_isCapturing)
            {
                SetStatus("采集中不能重新选择设备及应用，请先停止采集。");
                return;
            }
            await RefreshDevicesAsync();
            if (_isCapturing)
            {
                SetStatus("采集中不能重新选择设备及应用，请先停止采集。");
                return;
            }
            Window dialog = new Window
            {
                Title = "选择设备及应用",
                Owner = this,
                Width = 680,
                Height = 640,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                Background = Brush(28, 28, 37),
                Foreground = TextPrimary,
                FontFamily = FontFamily,
                ShowInTaskbar = false
            };

            Border shell = new Border
            {
                Background = Brush(28, 28, 37),
                BorderBrush = Brush(62, 63, 78),
                BorderThickness = new Thickness(1)
            };
            DockPanel root = new DockPanel { Background = Brush(28, 28, 37) };
            shell.Child = root;
            dialog.Content = shell;

            Grid header = new Grid
            {
                Height = 48,
                Background = Brush(24, 24, 32)
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            header.MouseLeftButtonDown += delegate
            {
                try { dialog.DragMove(); } catch { }
            };
            StackPanel title = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(26, 0, 0, 0)
            };
            title.Children.Add(new TextBlock
            {
                Text = "\uE8EA",
                FontFamily = ToolbarSymbolFont,
                Foreground = TextPrimary,
                FontSize = 17,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            title.Children.Add(new TextBlock
            {
                Text = "选择设备及应用",
                Foreground = TextPrimary,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(title);
            Button closeTop = DialogCloseButton(delegate { dialog.Close(); });
            Grid.SetColumn(closeTop, 1);
            header.Children.Add(closeTop);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            Grid footer = new Grid
            {
                Height = 72,
                Background = Brush(28, 28, 37),
                Margin = new Thickness(0, 0, 0, 0)
            };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            CheckBox autoStart = new CheckBox
            {
                Content = "自动开始采集",
                Foreground = TextPrimary,
                Margin = new Thickness(122, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            footer.Children.Add(autoStart);
            Button closeButton = DialogButton("关闭", delegate { dialog.Close(); });
            Grid.SetColumn(closeButton, 1);
            footer.Children.Add(closeButton);
            Button chooseButton = DialogButton("选择", delegate
            {
                CommitSelectionFromDialog();
                dialog.Close();
                if (autoStart.IsChecked == true) StartCapture();
            });
            _dialogChooseButton = chooseButton;
            Grid.SetColumn(chooseButton, 2);
            footer.Children.Add(chooseButton);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            Grid body = new Grid { Margin = new Thickness(32, 32, 38, 0) };
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(body);

            body.Children.Add(DialogLabel("选择设备", 0));
            Grid deviceRow = new Grid();
            deviceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            deviceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            _deviceCombo = DarkCombo();
            _deviceCombo.ItemTemplate = DarkTextItemTemplate("PickerLabel");
            _deviceCombo.MinHeight = 36;
            _deviceCombo.ItemsSource = _devices;
            DeviceInfo selectedDevice = _devices.FirstOrDefault(delegate(DeviceInfo item) { return _selectedDevice != null && item.Udid == _selectedDevice.Udid; }) ?? _devices.FirstOrDefault();
            _selectedDevice = selectedDevice;
            _deviceCombo.SelectedItem = selectedDevice;
            _deviceCombo.SelectionChanged += delegate
            {
                if (_loadingDevices) return;
                DeviceInfo device = _deviceCombo == null ? null : _deviceCombo.SelectedItem as DeviceInfo;
                if (device != null)
                {
                    if (_selectedDevice == null || !string.Equals(_selectedDevice.Udid, device.Udid, StringComparison.Ordinal))
                    {
                        _lockAndroidProcessToSelectedApp = false;
                    }
                    _selectedDevice = device;
                }
                RefreshTargetLists();
            };
            deviceRow.Children.Add(_deviceCombo);
            Button deviceRefreshButton = IconOnlyDialogButton("↻", delegate { RefreshPickerDevicesAndTargets(); });
            deviceRefreshButton.ToolTip = "刷新设备";
            Grid.SetColumn(deviceRefreshButton, 1);
            deviceRow.Children.Add(deviceRefreshButton);
            Grid.SetRow(deviceRow, 0);
            Grid.SetColumn(deviceRow, 1);
            body.Children.Add(deviceRow);

            _dialogStatus = HintText("");
            _dialogStatus.Margin = new Thickness(0, -1, 0, 0);
            _dialogStatus.TextWrapping = TextWrapping.NoWrap;
            _dialogStatus.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(_dialogStatus, 1);
            Grid.SetColumn(_dialogStatus, 1);
            body.Children.Add(_dialogStatus);

            body.Children.Add(DialogLabel("选择应用", 2));
            Grid appRow = new Grid();
            appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            _appCombo = new ComboBox { Visibility = Visibility.Collapsed };
            _appCombo.ItemsSource = _apps;
            _appCombo.SelectedItem = _selectedApp ?? _apps.FirstOrDefault(delegate(AppInfo app) { return app.BundleId == DefaultBundleForSelectedDevice(); }) ?? _apps.FirstOrDefault();
            _appCombo.SelectionChanged += delegate
            {
                AppInfo app = _appCombo == null ? null : _appCombo.SelectedItem as AppInfo;
                if (app != null)
                {
                    _selectedApp = app;
                    _selectedBundleId = app.BundleId;
                    if (!_isRenderingAppSelection && DeviceLookupService.IsAndroid(_selectedDevice))
                    {
                        _lockAndroidProcessToSelectedApp = true;
                    }
                    if (_bundleInput != null) _bundleInput.Text = app.BundleId;
                    UpdateSelectedAppSummary(app);
                    if (_dialogAppList != null) _dialogAppList.SelectedItem = app;
                    RenderProcesses();
                    UpdateTargetSummary();
                }
            };
            appRow.Children.Add(_appCombo);
            _selectedAppSummaryCard = CreateSelectedAppSummaryCard();
            appRow.Children.Add(_selectedAppSummaryCard);
            Button appRefreshButton = IconOnlyDialogButton("↻", delegate { RefreshTargetLists(); });
            appRefreshButton.ToolTip = "刷新应用和进程";
            Grid.SetColumn(appRefreshButton, 1);
            appRow.Children.Add(appRefreshButton);
            Grid.SetRow(appRow, 2);
            Grid.SetColumn(appRow, 1);
            body.Children.Add(appRow);

            StackPanel tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _dialogProcessTabButton = DialogTabButton("选择进程", true, delegate { SetDialogLaunchMode(false); });
            _dialogLaunchTabButton = DialogTabButton("启动APP", false, delegate { SetDialogLaunchMode(true); });
            tabs.Children.Add(_dialogProcessTabButton);
            tabs.Children.Add(_dialogLaunchTabButton);
            Grid.SetRow(tabs, 3);
            Grid.SetColumn(tabs, 1);
            body.Children.Add(tabs);

            Grid processBox = new Grid();
            _dialogProcessPanel = processBox;
            processBox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            processBox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _processSearch = DarkTextBox("请输入进程名称进行搜索");
            _processSearch.Text = "";
            _processSearch.Margin = new Thickness(0);
            _processSearch.Height = 32;
            _processSearch.Padding = new Thickness(38, 6, 8, 6);
            _processSearch.TextChanged += delegate { RenderProcesses(); };
            Grid searchFrame = SearchBoxFrame(_processSearch, "请输入进程名称进行搜索");
            processBox.Children.Add(searchFrame);

            _processCombo = new ComboBox { Visibility = Visibility.Collapsed };
            _processCombo.SelectionChanged += delegate { ApplyProcess(_processCombo == null ? null : _processCombo.SelectedItem as ProcessInfo); };
            processBox.Children.Add(_processCombo);

            ListView processList = CreateProcessList();
            _dialogProcessList = processList;
            processList.SelectionChanged += delegate
            {
                ProcessInfo process = processList.SelectedItem as ProcessInfo;
                if (process != null)
                {
                    _selectedProcess = process;
                    if (_processCombo != null) _processCombo.SelectedItem = process;
                    ApplyProcess(process);
                }
            };
            Grid.SetRow(processList, 1);
            processBox.Children.Add(processList);
            _processEmptyState = CreateProcessEmptyState();
            Grid.SetRow(_processEmptyState, 1);
            processBox.Children.Add(_processEmptyState);
            processBox.Tag = processList;
            Grid.SetRow(processBox, 4);
            Grid.SetColumn(processBox, 1);
            body.Children.Add(processBox);

            Grid appBox = new Grid { Visibility = Visibility.Collapsed };
            _dialogAppPanel = appBox;
            appBox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            appBox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid launchSearchRow = new Grid();
            launchSearchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            launchSearchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            _appLaunchSearch = DarkTextBox("请输入应用名称进行搜索");
            _appLaunchSearch.Margin = new Thickness(0, 0, 8, 0);
            _appLaunchSearch.Height = 32;
            _appLaunchSearch.Padding = new Thickness(38, 6, 8, 6);
            _appLaunchSearch.TextChanged += delegate { RenderLaunchApps(); };
            launchSearchRow.Children.Add(SearchBoxFrame(_appLaunchSearch, "请输入应用名称进行搜索"));
            _dialogLaunchButton = DialogButton("启动", delegate { LaunchSelectedAppFromDialog(); });
            _dialogLaunchButton.Width = 78;
            _dialogLaunchButton.Height = 32;
            _dialogLaunchButton.MinWidth = 78;
            _dialogLaunchButton.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(_dialogLaunchButton, 1);
            launchSearchRow.Children.Add(_dialogLaunchButton);
            appBox.Children.Add(launchSearchRow);
            _dialogAppList = CreateAppList();
            _dialogAppList.SelectionChanged += delegate
            {
                AppInfo app = _dialogAppList.SelectedItem as AppInfo;
                if (app == null) return;
                _selectedApp = app;
                _selectedBundleId = app.BundleId;
                if (!_isRenderingAppSelection && DeviceLookupService.IsAndroid(_selectedDevice))
                {
                    _lockAndroidProcessToSelectedApp = true;
                }
                if (_appCombo != null) _appCombo.SelectedItem = app;
                UpdateSelectedAppSummary(app);
                if (_bundleInput != null) _bundleInput.Text = app.BundleId;
                RenderProcesses();
                UpdateTargetSummary();
            };
            Grid.SetRow(_dialogAppList, 1);
            appBox.Children.Add(_dialogAppList);
            Grid.SetRow(appBox, 4);
            Grid.SetColumn(appBox, 1);
            body.Children.Add(appBox);

            _bundleInput = new TextBox { Text = _selectedBundleId, Visibility = Visibility.Collapsed };
            body.Children.Add(_bundleInput);
            _processHint = new TextBlock { Visibility = Visibility.Collapsed };

            RenderApps();
            RenderProcesses();
            RenderLaunchApps();
            SetDialogLaunchMode(false);
            UpdateDialogState();
            processList.ItemsSource = _processCombo == null ? new List<ProcessInfo>() : _processCombo.ItemsSource;
            processList.SelectedItem = _selectedProcess;
            _processCombo.SelectionChanged += delegate
            {
                processList.ItemsSource = _processCombo == null ? new List<ProcessInfo>() : _processCombo.ItemsSource;
                processList.SelectedItem = _selectedProcess;
            };

            dialog.ShowDialog();
            _deviceCombo = null;
            _appCombo = null;
            _processCombo = null;
            _processSearch = new TextBox { Visibility = Visibility.Collapsed };
            _appLaunchSearch = new TextBox { Visibility = Visibility.Collapsed };
            _dialogProcessList = null;
            _dialogAppList = null;
            _dialogProcessTabButton = null;
            _dialogLaunchTabButton = null;
            _dialogLaunchButton = null;
            _dialogProcessPanel = null;
            _dialogAppPanel = null;
            _bundleInput = new TextBox { Text = _selectedBundleId, Visibility = Visibility.Collapsed };
            _processHint = HintText("");
            _appCount = HintText("");
            _processCount = HintText("");
        }

        private void CommitSelectionFromDialog()
        {
            DeviceInfo device = _deviceCombo == null ? null : _deviceCombo.SelectedItem as DeviceInfo;
            AppInfo app = _selectedApp ?? (_appCombo == null ? null : _appCombo.SelectedItem as AppInfo);
            ProcessInfo process = _processCombo == null ? null : _processCombo.SelectedItem as ProcessInfo;
            if (device != null) _selectedDevice = device;
            if (app != null)
            {
                _selectedApp = app;
                _selectedBundleId = app.BundleId;
            }
            if (process != null) ApplyProcess(process);
            UpdateTargetSummary();
            UpdateDeviceInfoPanel();
        }

        private async void RefreshPickerDevicesAndTargets()
        {
            await RefreshDevicesAsync();
        }

        private void UpdateDialogState()
        {
            if (_dialogStatus == null) return;
            bool hasDevice = _devices.Count > 0;
            if (!hasDevice)
            {
                _dialogStatus.Text = _deviceDiscoveryStatus;
                if (_deviceCombo != null)
                {
                    _deviceCombo.ItemsSource = _devices;
                    _deviceCombo.SelectedItem = null;
                }
                if (_appCombo != null) _appCombo.ItemsSource = new List<AppInfo>();
                if (_processCombo != null) _processCombo.ItemsSource = new List<ProcessInfo>();
                if (_dialogProcessList != null) _dialogProcessList.ItemsSource = new List<ProcessInfo>();
                if (_dialogAppList != null) _dialogAppList.ItemsSource = new List<AppInfo>();
            }
            else
            {
                _dialogStatus.Text = string.Format("已检测到 {0} 台设备。", _devices.Count);
            }
            if (_dialogChooseButton != null)
            {
                SetButtonEnabled(_dialogChooseButton, hasDevice && _selectedProcess != null);
            }
        }

        private void AddDeviceInfoOverlay(Grid main)
        {
            Grid overlay = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 18, 46)
            };
            Grid.SetRow(overlay, 0);
            Grid.SetRowSpan(overlay, 5);

            StackPanel stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _deviceInfoPanel = new Border
            {
                Width = 312,
                Background = Brush(9, 10, 15),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 0, 0, 14),
                Visibility = Visibility.Collapsed
            };
            _deviceInfoText = new TextBlock
            {
                Foreground = TextPrimary,
                FontSize = 13,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap
            };
            _deviceInfoPanel.Child = _deviceInfoText;
            stack.Children.Add(_deviceInfoPanel);

            Button phoneButton = new Button
            {
                Content = "▯",
                Foreground = Brush(0, 214, 117),
                Background = Brush(9, 10, 15),
                BorderBrush = Brush(9, 10, 15),
                BorderThickness = new Thickness(1),
                Width = 46,
                Height = 46,
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = DarkButtonStyle()
            };
            phoneButton.Click += delegate
            {
                _deviceInfoVisible = !_deviceInfoVisible;
                UpdateDeviceInfoPanel();
            };
            stack.Children.Add(phoneButton);
            overlay.Children.Add(stack);
            main.Children.Add(overlay);
        }

        private void SyncMetricOptions()
        {
            UpdateMetricLayout();
            SyncScreenshotOptions();
            Render();
        }

        private void UpdateMetricLayout()
        {
            bool screenshots = _screenshotMetricCheck.IsChecked == true;
            bool fps = _fpsMetricCheck.IsChecked == true;
            bool frame = _frameMetricCheck.IsChecked == true;
            bool memory = _memoryMetricCheck.IsChecked == true;
            bool cpu = _cpuMetricCheck.IsChecked == true;
            bool temperature = _temperatureMetricCheck.IsChecked == true;
            bool thermalState = _thermalStateMetricCheck.IsChecked == true && !DeviceLookupService.IsAndroid(_selectedDevice);
            bool showCpuNormalized = ShouldShowCpuNormalizedChart(cpu);
            bool showCpuCore = ShouldShowCpuCoreChart(cpu);
            if (_shotsPanel != null) _shotsPanel.Visibility = screenshots ? Visibility.Visible : Visibility.Collapsed;
            SetChartRowVisibility(_fpsRow, _fpsChartRowDef, fps);
            SetChartRowVisibility(_frameRow, _frameChartRowDef, frame);
            SetChartRowVisibility(_memoryRow, _memoryChartRowDef, memory);
            SetChartRowVisibility(_cpuRow, _cpuChartRowDef, cpu);
            SetChartRowVisibility(_cpuNormalizedRow, _cpuNormalizedChartRowDef, showCpuNormalized);
            SetChartRowVisibility(_cpuCoreRow, _cpuCoreChartRowDef, showCpuCore);
            SetChartRowVisibility(_temperatureRow, _temperatureChartRowDef, temperature);
            SetChartRowVisibility(_thermalStateRow, _thermalStateChartRowDef, thermalState);
        }

        private bool ShouldShowCpuNormalizedChart(bool cpuSelected)
        {
            if (!cpuSelected) return false;
            if (DeviceLookupService.IsAndroid(_selectedDevice)) return true;
            return _samples.Any(delegate(PerfSample sample) { return sample.HasCpuNormalized; });
        }

        private bool ShouldShowCpuCoreChart(bool cpuSelected)
        {
            if (!cpuSelected) return false;
            if (DeviceLookupService.IsAndroid(_selectedDevice)) return true;
            return _samples.Any(delegate(PerfSample sample) { return sample.HasCpuCoreUsage; });
        }

        private static void SetChartRowVisibility(UIElement row, RowDefinition rowDefinition, bool visible)
        {
            if (row != null) row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (rowDefinition != null) rowDefinition.Height = visible ? GridLength.Auto : new GridLength(0);
        }

        private void UpdateDeviceInfoPanel()
        {
            if (_deviceInfoPanel == null || _deviceInfoText == null) return;
            _deviceInfoPanel.Visibility = _deviceInfoVisible ? Visibility.Visible : Visibility.Collapsed;
            if (!_deviceInfoVisible) return;

            string deviceName = _selectedDevice == null ? "未选择设备" : _selectedDevice.ToString();
            string app = _selectedApp == null ? "未选择应用" : (_selectedApp.BundleId + (string.IsNullOrWhiteSpace(_selectedApp.Name) ? "" : "\n" + _selectedApp.Name));
            string process = _selectedProcess == null ? "未选择进程" : string.Format("{0}\npid {1}", _selectedProcess.Name, _selectedProcess.Pid);
            string details = DeviceDetailsText(_selectedDevice);
            _deviceInfoText.Text =
                "设备名称：\n" + deviceName + "\n\n" +
                "当前应用：\n" + app + "\n\n" +
                "当前进程：\n" + process + "\n\n" +
                "设备详情：\n" + details;
        }

        private ListView CreateProcessList()
        {
            ListView list = new ListView
            {
                Background = Brush(37, 37, 49),
                Foreground = TextPrimary,
                BorderBrush = BorderBrushDark,
                BorderThickness = new Thickness(1, 0, 1, 1),
                AlternationCount = 2,
                ItemContainerStyle = DarkListViewItemStyle()
            };
            list.Resources.Add(typeof(ScrollBar), DarkScrollBarStyle());
            list.Resources.Add(typeof(GridViewColumnHeader), DarkGridViewColumnHeaderStyle());
            list.Resources.Add(SystemColors.HighlightBrushKey, Brush(24, 118, 220));
            list.Resources.Add(SystemColors.HighlightTextBrushKey, Brushes.White);
            list.Resources.Add(SystemColors.InactiveSelectionHighlightBrushKey, Brush(46, 90, 145));
            list.Resources.Add(SystemColors.InactiveSelectionHighlightTextBrushKey, Brushes.White);
            GridView grid = new GridView();
            grid.Columns.Add(new GridViewColumn { Header = "", CellTemplate = ProcessIconTemplate(), Width = 34 });
            grid.Columns.Add(new GridViewColumn { Header = "PID", DisplayMemberBinding = new Binding("Pid"), Width = 64 });
            grid.Columns.Add(new GridViewColumn { Header = "Process", CellTemplate = ProcessTextTemplate(), Width = 360 });
            list.View = grid;
            return list;
        }

        private ListView CreateAppList()
        {
            ListView list = new ListView
            {
                Background = Brush(37, 37, 49),
                Foreground = TextPrimary,
                BorderBrush = BorderBrushDark,
                BorderThickness = new Thickness(1, 0, 1, 1),
                AlternationCount = 2,
                ItemContainerStyle = AppListViewItemStyle()
            };
            list.Resources.Add(typeof(ScrollBar), DarkScrollBarStyle());
            list.Resources.Add(typeof(GridViewColumnHeader), DarkGridViewColumnHeaderStyle());
            list.Resources.Add(SystemColors.HighlightBrushKey, Brush(24, 118, 220));
            list.Resources.Add(SystemColors.HighlightTextBrushKey, Brushes.White);
            GridView grid = new GridView();
            grid.Columns.Add(new GridViewColumn { Header = "", CellTemplate = AppIconTemplate(), Width = 42 });
            grid.Columns.Add(new GridViewColumn { Header = "APP", CellTemplate = AppTextTemplate(), Width = 414 });
            list.View = grid;
            return list;
        }

        private static DataTemplate ProcessIconTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.WidthProperty, 22.0);
            border.SetValue(Border.HeightProperty, 22.0);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BackgroundProperty, Brush(64, 65, 76));
            border.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            FrameworkElementFactory layer = new FrameworkElementFactory(typeof(Grid));
            FrameworkElementFactory image = new FrameworkElementFactory(typeof(Image));
            image.SetValue(Image.WidthProperty, 22.0);
            image.SetValue(Image.HeightProperty, 22.0);
            image.SetValue(Image.StretchProperty, Stretch.UniformToFill);
            image.SetBinding(UIElement.VisibilityProperty, new Binding("IconPath") { Converter = new IconPathVisibilityConverter() });
            image.SetBinding(Image.SourceProperty, new Binding("IconPath") { Converter = new IconPathToImageConverter() });
            layer.AppendChild(image);
            FrameworkElementFactory text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.ForegroundProperty, TextPrimary);
            text.SetValue(TextBlock.FontSizeProperty, 12.0);
            text.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetBinding(UIElement.VisibilityProperty, new Binding("IconPath") { Converter = new IconPathVisibilityConverter(), ConverterParameter = "fallback" });
            text.SetBinding(TextBlock.TextProperty, new Binding("IconKey") { Converter = new IconKeyToGlyphConverter() });
            layer.AppendChild(text);
            border.AppendChild(layer);
            return new DataTemplate { VisualTree = border };
        }

        private static DataTemplate AppIconTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.WidthProperty, 26.0);
            border.SetValue(Border.HeightProperty, 26.0);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetValue(Border.BackgroundProperty, Brush(54, 57, 76));
            border.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            FrameworkElementFactory layer = new FrameworkElementFactory(typeof(Grid));
            FrameworkElementFactory image = new FrameworkElementFactory(typeof(Image));
            image.SetValue(Image.WidthProperty, 26.0);
            image.SetValue(Image.HeightProperty, 26.0);
            image.SetValue(Image.StretchProperty, Stretch.UniformToFill);
            image.SetBinding(UIElement.VisibilityProperty, new Binding("IconPath") { Converter = new IconPathVisibilityConverter() });
            image.SetBinding(Image.SourceProperty, new Binding("IconPath") { Converter = new IconPathToImageConverter() });
            layer.AppendChild(image);
            FrameworkElementFactory text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            text.SetValue(TextBlock.FontSizeProperty, 13.0);
            text.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetBinding(UIElement.VisibilityProperty, new Binding("IconPath") { Converter = new IconPathVisibilityConverter(), ConverterParameter = "fallback" });
            text.SetBinding(TextBlock.TextProperty, new Binding("IconKey") { Converter = new IconKeyToGlyphConverter() });
            layer.AppendChild(text);
            border.AppendChild(layer);
            return new DataTemplate { VisualTree = border };
        }

        private static DataTemplate AppTextTemplate()
        {
            FrameworkElementFactory stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            stack.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 5));
            FrameworkElementFactory name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetValue(TextBlock.ForegroundProperty, TextPrimary);
            name.SetValue(TextBlock.FontSizeProperty, 15.0);
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            FrameworkElementFactory bundle = new FrameworkElementFactory(typeof(TextBlock));
            bundle.SetValue(TextBlock.ForegroundProperty, TextSecondary);
            bundle.SetValue(TextBlock.FontSizeProperty, 13.0);
            bundle.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
            bundle.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            bundle.SetBinding(TextBlock.TextProperty, new Binding("BundleId"));
            stack.AppendChild(name);
            stack.AppendChild(bundle);
            return new DataTemplate { VisualTree = stack };
        }

        private static DataTemplate ProcessTextTemplate()
        {
            FrameworkElementFactory root = new FrameworkElementFactory(typeof(DockPanel));
            root.SetValue(DockPanel.LastChildFillProperty, true);

            FrameworkElementFactory badge = new FrameworkElementFactory(typeof(Border));
            badge.SetValue(DockPanel.DockProperty, Dock.Right);
            badge.SetValue(Border.BackgroundProperty, Brush(18, 92, 188));
            badge.SetValue(Border.BorderBrushProperty, Brush(93, 170, 255));
            badge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            badge.SetValue(Border.PaddingProperty, new Thickness(5, 1, 5, 1));
            badge.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 1, 4, 1));
            badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            badge.SetBinding(UIElement.VisibilityProperty, new Binding("Recommended") { Converter = new RecommendedProcessVisibilityConverter() });
            FrameworkElementFactory badgeText = new FrameworkElementFactory(typeof(TextBlock));
            badgeText.SetValue(TextBlock.TextProperty, "推荐");
            badgeText.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            badgeText.SetValue(TextBlock.FontSizeProperty, 11.0);
            badgeText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            badge.AppendChild(badgeText);
            root.AppendChild(badge);

            FrameworkElementFactory text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.FontSizeProperty, 16.0);
            text.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 0, 0));
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetBinding(TextBlock.TextProperty, new Binding("PickerName"));
            MultiBinding foreground = new MultiBinding { Converter = new RecommendedProcessTextBrushConverter() };
            foreground.Bindings.Add(new Binding("Recommended"));
            foreground.Bindings.Add(new Binding("IsSelected") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListViewItem), 1) });
            text.SetBinding(TextBlock.ForegroundProperty, foreground);
            root.AppendChild(text);
            return new DataTemplate { VisualTree = root };
        }

        private Button DialogTabButton(string text, bool active, RoutedEventHandler handler)
        {
            Button button = new Button
            {
                Content = text,
                Foreground = active ? AccentBlue : TextPrimary,
                Background = active ? Brush(39, 39, 52) : Brushes.Transparent,
                BorderBrush = active ? AccentBlue : BorderBrushDark,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 5, 24, 5),
                Margin = new Thickness(0, 0, 0, 0),
                MinWidth = 130,
                Height = 38,
                FontSize = 15,
                Style = DarkButtonStyle()
            };
            button.Click += handler;
            return button;
        }

        private void SetDialogLaunchMode(bool launchMode)
        {
            _dialogLaunchMode = launchMode;
            if (_dialogProcessPanel != null) _dialogProcessPanel.Visibility = launchMode ? Visibility.Collapsed : Visibility.Visible;
            if (_dialogAppPanel != null) _dialogAppPanel.Visibility = launchMode ? Visibility.Visible : Visibility.Collapsed;
            SetDialogTabState(_dialogProcessTabButton, !launchMode);
            SetDialogTabState(_dialogLaunchTabButton, launchMode);
            RenderLaunchApps();
        }

        private static void SetDialogTabState(Button button, bool active)
        {
            if (button == null) return;
            button.Foreground = active ? AccentBlue : TextPrimary;
            button.Background = active ? Brush(39, 39, 52) : Brushes.Transparent;
            button.BorderBrush = active ? AccentBlue : BorderBrushDark;
        }

        private static Style DarkListViewItemStyle()
        {
            Style style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(39, 39, 52)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 1, 6, 1)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 26.0));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(Control.TemplateProperty, DarkListViewItemTemplate()));

            Trigger alternate = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
            alternate.Setters.Add(new Setter(Control.BackgroundProperty, Brush(54, 54, 67)));
            style.Triggers.Add(alternate);

            Trigger selected = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush(74, 137, 213)));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            selected.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(74, 137, 213)));
            style.Triggers.Add(selected);

            Trigger hover = new Trigger { Property = ListViewItem.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(68, 69, 84)));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Triggers.Add(hover);
            return style;
        }

        private static ControlTemplate DarkListViewItemTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ItemBorder";
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(GridViewRowPresenter));
            presenter.SetBinding(GridViewRowPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetBinding(GridViewRowPresenter.ColumnsProperty, new Binding("View.Columns") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListView), 1) });
            presenter.SetBinding(GridViewRowPresenter.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(presenter);
            return new ControlTemplate(typeof(ListViewItem)) { VisualTree = border };
        }

        private static Style AppListViewItemStyle()
        {
            Style style = DarkListViewItemStyle();
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 38.0));
            return style;
        }

        private static Style DarkGridViewColumnHeaderStyle()
        {
            Style style = new Style(typeof(GridViewColumnHeader));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(31, 31, 42)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(64, 65, 80)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
            style.Setters.Add(new Setter(Control.HeightProperty, 26.0));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.TemplateProperty, DarkGridViewColumnHeaderTemplate()));
            return style;
        }

        private static ControlTemplate DarkGridViewColumnHeaderTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(ContentPresenter.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetBinding(ContentPresenter.VerticalAlignmentProperty, new Binding("VerticalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(presenter);
            ControlTemplate template = new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = border };
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(39, 39, 52)));
            template.Triggers.Add(hover);
            return template;
        }

        private void AddSample(PerfSample sample)
        {
            InsertSampleChronologically(sample);
            double latestElapsed = _samples.Count > 0 ? _samples[_samples.Count - 1].ElapsedSec : sample.ElapsedSec;
            if (_followLatest) _selectedTime = latestElapsed;
            if (sample.FpsUpdated || sample.MemoryUpdated || sample.CpuUpdated || sample.CpuNormalizedUpdated || sample.CpuCoreUpdated || sample.TemperatureUpdated || sample.ThermalStateUpdated)
            {
                _realMetricSampleSeen = true;
            }
            if (_targetIdentityConfirmed && (_realMetricSampleSeen || !_captureExpectsMetricData))
            {
                _screenshots.CaptureDue(latestElapsed, _samples.Count - 1, _lifetime.Token);
            }
            QueueSampleRender();
        }

        private void QueueSampleRender()
        {
            if (_sampleRenderQueued) return;
            _sampleRenderQueued = true;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                _sampleRenderQueued = false;
                Render();
            }), DispatcherPriority.Render);
        }

        private void InsertSampleChronologically(PerfSample sample)
        {
            int low = 0;
            int high = _samples.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_samples[middle].ElapsedSec <= sample.ElapsedSec)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            _samples.Insert(low, sample);
        }

        private void AddScreenshot(ScreenshotInfo shot)
        {
            _shots.Add(shot);
            if (_shotStrip != null) _shotStrip.Children.Add(CreateScreenshotItem(shot));
            if (_followLatest)
            {
                UpdatePreview(shot);
                ScrollToLatestShot();
            }
            Render();
        }

        private void SelectTimeFromChart(double elapsed)
        {
            SelectTime(elapsed, false);
        }

        private void SelectTime(double elapsed, bool followLatest)
        {
            if (_samples.Count == 0 && _shots.Count == 0) return;
            _followLatest = followLatest;
            if (_samples.Count > 0)
            {
                _selectedTime = _samples.OrderBy(delegate(PerfSample sample) { return Math.Abs(sample.ElapsedSec - elapsed); }).First().ElapsedSec;
            }
            else
            {
                _selectedTime = elapsed;
            }
            ScreenshotInfo shot = NearestShot(_selectedTime.Value);
            UpdatePreview(shot);
            SetSelectedShot(shot);
            Render();
        }

        private void Render()
        {
            UpdateMetricLayout();
            UpdateCpuCoreLegend();
            UpdateTemperatureLegend();
            PerfSample latestSample = _samples.Count > 0 ? _samples[_samples.Count - 1] : null;
            PerfSample selected = null;
            if (_selectedTime.HasValue)
            {
                selected = _samples.OrderBy(delegate(PerfSample sample) { return Math.Abs(sample.ElapsedSec - _selectedTime.Value); }).FirstOrDefault();
            }
            if (selected == null) selected = latestSample;
            UpdateDataPanels(latestSample, selected);

            double latest = _samples.Count > 0 ? _samples[_samples.Count - 1].ElapsedSec : 0;
            string selectedText = _selectedTime.HasValue ? FormatElapsed(_selectedTime.Value) : "0s";
            string shotText = _shots.Count > 0 ? "    最新截图：" + FormatElapsed(_shots[_shots.Count - 1].ElapsedSec) : "";
            _timeSummary.Text = "时间轴  0s  ->  " + FormatElapsed(latest) + "    当前：" + selectedText + shotText;

            ChartCanvas[] charts = new[] { _fpsChart, _frameChart, _memoryChart, _cpuChart, _cpuNormalizedChart, _cpuCoreChart, _temperatureChart, _thermalStateChart };
            foreach (ChartCanvas chart in charts)
            {
                chart.Samples = _samples;
                chart.Screenshots = _shots;
                chart.SelectedTime = _selectedTime;
                chart.InvalidateVisual();
            }
        }

        private void UpdateCpuCoreLegend()
        {
            if (_cpuCoreLegendPanel == null) return;
            int coreCount = _samples
                .Where(delegate(PerfSample sample)
                {
                    return sample.HasCpuCoreUsage
                        && sample.CpuCorePercents != null
                        && sample.CpuCoreCount > 0;
                })
                .Select(delegate(PerfSample sample)
                {
                    return Math.Min(sample.CpuCoreCount, sample.CpuCorePercents.Count);
                })
                .DefaultIfEmpty(0)
                .Max();
            if (coreCount == _renderedCpuCoreLegendCount) return;

            _renderedCpuCoreLegendCount = coreCount;
            _cpuCoreLegendPanel.Children.Clear();
            if (coreCount <= 0)
            {
                _cpuCoreLegendPanel.Children.Add(new TextBlock
                {
                    Text = "等待逐核数据",
                    Foreground = TextSecondary,
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            else
            {
                for (int coreIndex = 0; coreIndex < coreCount; coreIndex++)
                {
                    ChartCanvas interactiveChart = coreCount > 1 ? _cpuCoreChart : null;
                    _cpuCoreLegendPanel.Children.Add(LegendItem(ChartCanvas.CoreSeriesName(coreIndex), ChartCanvas.CoreCpuBrush(coreIndex), true, interactiveChart));
                }
            }

            FrameworkElement coreRow = _cpuCoreRow as FrameworkElement;
            if (coreRow != null)
            {
                double legendRows = Math.Ceiling(Math.Max(1, coreCount) / 3.0);
                double height = Math.Max(ChartRowHeight, 42.0 + legendRows * 19.0);
                coreRow.Height = height;
                coreRow.MinHeight = height;
            }
        }

        private void UpdateTemperatureLegend()
        {
            if (_temperatureLegendPanel == null) return;
            List<string> sensors = ChartCanvas.TemperatureSensorNames(_samples);
            string signature = string.Join("\u001f", sensors);
            if (_renderedTemperatureLegendSignature != null
                && string.Equals(signature, _renderedTemperatureLegendSignature, StringComparison.Ordinal)) return;

            _renderedTemperatureLegendSignature = signature;
            _temperatureLegendPanel.Children.Clear();
            if (sensors.Count == 0)
            {
                _temperatureLegendPanel.Children.Add(new TextBlock
                {
                    Text = "Waiting for device temperature",
                    Foreground = TextSecondary,
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            else
            {
                for (int sensorIndex = 0; sensorIndex < sensors.Count; sensorIndex++)
                {
                    ChartCanvas interactiveChart = sensors.Count > 1 ? _temperatureChart : null;
                    _temperatureLegendPanel.Children.Add(LegendItem(
                        sensors[sensorIndex],
                        ChartCanvas.TemperatureBrush(sensorIndex),
                        true,
                        interactiveChart,
                        ChartCanvas.TemperatureSeriesName(sensors[sensorIndex])));
                }
            }

            FrameworkElement temperatureRow = _temperatureRow as FrameworkElement;
            if (temperatureRow != null)
            {
                double legendRows = Math.Ceiling(Math.Max(1, sensors.Count) / 3.0);
                double height = Math.Max(ChartRowHeight, 42.0 + legendRows * 19.0);
                temperatureRow.Height = height;
                temperatureRow.MinHeight = height;
            }
        }

        private void ClearSamples()
        {
            _samples.Clear();
            _shots.Clear();
            _renderedTemperatureLegendSignature = null;
            if (_shotStrip != null) _shotStrip.Children.Clear();
            _preview.Source = null;
            _selectedTime = null;
            _followLatest = true;
            _targetIdentityConfirmed = false;
            _realMetricSampleSeen = false;
            _captureExpectsMetricData = false;
            Render();
        }

        private async void ExportCsv()
        {
            bool fileOperationStarted = false;
            try
            {
                fileOperationStarted = BeginFileOperation("正在准备导出 CSV...");
                if (!fileOperationStarted) return;
                if (_samples.Count == 0)
                {
                    SetStatus("没有可导出的采集数据。");
                    return;
                }
                Directory.CreateDirectory(Path.Combine(_dataDir, "exports"));
                SaveFileDialog dialog = new SaveFileDialog
                {
                    Title = "导出 CSV 数据",
                    Filter = "CSV 文件 (*.csv)|*.csv",
                    InitialDirectory = Path.Combine(_dataDir, "exports"),
                    FileName = "motuperf-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv",
                    AddExtension = true,
                    DefaultExt = ".csv"
                };
                if (dialog.ShowDialog(this) != true) return;
                string path = dialog.FileName;
                SetStatus("正在导出 CSV，请稍候...");
                await Task.Run(delegate
                {
                    string csv = BuildGearStyleCsv().ToString();
                    WriteAllTextAtomic(path, csv, new UTF8Encoding(true));
                });
                SetStatus("已导出：" + path);
            }
            catch (OperationCanceledException)
            {
                SetStatus("导出已取消。");
            }
            catch (Exception ex)
            {
                string path = CrashLogger.Write("导出 CSV 失败", ex);
                SetStatus("导出失败：" + ex.Message + "；日志：" + path);
            }
            finally
            {
                if (fileOperationStarted) EndFileOperation();
            }
        }

        private async void SaveSessionFile()
        {
            bool fileOperationStarted = false;
            try
            {
                fileOperationStarted = BeginFileOperation("正在准备保存现场...");
                if (!fileOperationStarted) return;
                if (_samples.Count == 0 && _shots.Count == 0)
                {
                    SetStatus("没有可保存的现场数据。");
                    return;
                }
                Directory.CreateDirectory(Path.Combine(_dataDir, "sessions"));
                SaveFileDialog dialog = new SaveFileDialog
                {
                    Title = "保存现场文件",
                    Filter = "MoTuPerf 现场文件 (*.motuperf)|*.motuperf",
                    InitialDirectory = Path.Combine(_dataDir, "sessions"),
                    FileName = "motuperf-session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".motuperf",
                    AddExtension = true,
                    DefaultExt = ".motuperf"
                };
                if (dialog.ShowDialog(this) != true) return;
                string path = dialog.FileName;
                SetStatus("正在保存现场文件，请不要关闭软件...");
                SessionDocument document = BuildSessionDocument();
                await Task.Run(delegate { SaveSessionArchive(path, document); });
                SetStatus("已保存现场：" + dialog.FileName);
            }
            catch (OperationCanceledException)
            {
                SetStatus("保存已取消。");
            }
            catch (Exception ex)
            {
                string path = CrashLogger.Write("保存现场失败", ex);
                SetStatus("保存失败：" + ex.Message + "；日志：" + path);
            }
            finally
            {
                if (fileOperationStarted) EndFileOperation();
            }
        }

        private async void OpenSessionFile()
        {
            bool fileOperationStarted = false;
            try
            {
                fileOperationStarted = BeginFileOperation("正在准备打开现场...");
                if (!fileOperationStarted) return;
                OpenFileDialog dialog = new OpenFileDialog
                {
                    Title = "打开现场文件",
                    Filter = "MoTuPerf 现场文件 (*.motuperf)|*.motuperf",
                    InitialDirectory = Path.Combine(_dataDir, "sessions"),
                    Multiselect = false
                };
                if (dialog.ShowDialog(this) != true) return;
                SetStatus("正在打开现场文件...");
                SessionDocument document = await Task.Run(delegate { return LoadSessionArchiveDocument(dialog.FileName); });
                ApplySessionDocument(document);
                SetStatus("已打开现场：" + dialog.FileName);
            }
            catch (InvalidDataException ex)
            {
                SetStatus("打开失败：现场文件不完整或已损坏，可能是上次保存未完成。请重新保存一次完整现场文件。详情：" + ex.Message);
            }
            catch (OperationCanceledException)
            {
                SetStatus("打开已取消。");
            }
            catch (Exception ex)
            {
                string path = CrashLogger.Write("打开现场失败", ex);
                SetStatus("打开失败：" + ex.Message + "；日志：" + path);
            }
            finally
            {
                if (fileOperationStarted) EndFileOperation();
            }
        }

        private StringBuilder BuildGearStyleCsv()
        {
            ExportStats stats = ComputeExportStats();
            StringBuilder sb = new StringBuilder();
            string bundle = string.IsNullOrWhiteSpace(_selectedBundleId) ? "-" : _selectedBundleId;
            string preferredMemoryMetric = MetricStatistics.PreferredMemoryMetric(_samples);
            string memoryMetric = MemoryMetricForExport();
            DateTime exportStart = _sessionStartedAt == default(DateTime) && _samples.Count > 0 ? _samples[0].Timestamp : _sessionStartedAt;
            if (exportStart == default(DateTime)) exportStart = DateTime.Now;
            sb.AppendLine(CsvLine(exportStart.ToString("yyyy/MM/dd HH:mm:ss"), bundle));
            sb.AppendLine("Device,OS,CPU,GPU,Resolution");
            sb.AppendLine(CsvLine(DeviceNameForExport(), DeviceOsForExport(), DeviceCpuForExport(), DeviceGpuForExport(), DeviceResolutionForExport()));
            sb.AppendLine("Stat");
            sb.AppendLine("FPS(avg),FPS>=18(%),FPS>=25(%),FPS(var),FPS(std),FPS(drop),FPS(min),FPS(median),FPS(medRange+-20%)[%],Jank(/10min),BigJank(/10min),Stutter(%)," + memoryMetric + "(avg)[MB]," + memoryMetric + "(peak)[MB],AppCPU(raw avg)[%],AppCPU(raw<=60%)[%],AppCPU(raw<=80%)[%],TargetPid,App,Process,OwnerPid,OwnerBundle,OwnershipSource,CoalitionId,ProcessStartIdentity,ScreenshotCount,FrameMetricQuality,FPSStatsSourceTier,FPSStatsSampleCount,FPSStatsExcludedSampleCount,FPSStatsObservationSec,JankStatsSampleCount,JankStatsExcludedSampleCount,JankStatsObservationSec");
            sb.AppendLine(CsvLine(
                stats.HasFpsStats ? Num(stats.FpsAvg) : "-",
                stats.HasFpsStats ? Num(stats.FpsGe18Percent) : "-",
                stats.HasFpsStats ? Num(stats.FpsGe25Percent) : "-",
                stats.HasFpsStats ? Num(stats.FpsVariance) : "-",
                stats.HasFpsStats ? Num(stats.FpsStdDev) : "-",
                stats.HasFpsStats ? Num(stats.FpsDropPerHour) : "-",
                stats.HasFpsStats ? Num(stats.FpsMinimum) : "-",
                stats.HasFpsStats ? Num(stats.FpsMedian) : "-",
                stats.HasFpsStats ? Num(stats.FpsMedianRangePercent) : "-",
                stats.HasJankStats ? Num(stats.JankPer10Min) : "-",
                stats.HasJankStats ? Num(stats.BigJankPer10Min) : "-",
                stats.HasStutterStats ? Num(stats.StutterPercent) : "-",
                stats.HasMemoryStats ? Num(stats.MemoryAvgMb) : "-",
                stats.HasMemoryStats ? Num(stats.MemoryPeakMb) : "-",
                stats.HasCpuStats ? Num(stats.CpuAvg) : "-",
                stats.HasCpuStats ? Num(stats.CpuLe60Percent) : "-",
                stats.HasCpuStats ? Num(stats.CpuLe80Percent) : "-",
                TargetPidForExport(),
                AppNameForExport(),
                ProcessNameForExport(),
                _selectedProcess == null || _selectedProcess.OwnerPid <= 0 ? "-" : _selectedProcess.OwnerPid.ToString(CultureInfo.InvariantCulture),
                _selectedProcess == null || string.IsNullOrWhiteSpace(_selectedProcess.OwnerBundleId) ? "-" : _selectedProcess.OwnerBundleId,
                _selectedProcess == null || string.IsNullOrWhiteSpace(_selectedProcess.OwnershipSource) ? "-" : _selectedProcess.OwnershipSource,
                _selectedProcess == null || _selectedProcess.CoalitionId <= 0 ? "-" : _selectedProcess.CoalitionId.ToString(CultureInfo.InvariantCulture),
                ProcessStartIdentityForExport(),
                _shots.Count.ToString(CultureInfo.InvariantCulture),
                FrameMetricQualityForExport(),
                stats.HasFpsStats ? stats.FpsSourceTier : "-",
                stats.HasFpsStats ? stats.FpsSampleCount.ToString(CultureInfo.InvariantCulture) : "-",
                stats.HasFpsStats ? stats.FpsExcludedSampleCount.ToString(CultureInfo.InvariantCulture) : "-",
                stats.HasFpsStats ? Num(stats.FpsObservationSec) : "-",
                stats.HasJankStats ? stats.JankSampleCount.ToString(CultureInfo.InvariantCulture) : "-",
                stats.HasJankStats ? stats.JankExcludedSampleCount.ToString(CultureInfo.InvariantCulture) : "-",
                stats.HasJankStats ? Num(stats.JankObservationSec) : "-"));
            sb.AppendLine();
            sb.AppendLine("Data");
            sb.AppendLine("Index,Time,FPS-FPS[frame/s],FPS-Jank[frame/s],FPS-BigJank[frame/s],DisplayFrameTime-P95[ms],MemoryDetail-" + memoryMetric + " Total[KB],MemoryRaw[KB],MemoryRSSRaw[KB],CPUUsage-Process Raw[%],CPUUsage-Process Normalized[%],CPUUsage-Device Core[%],CPU-Core-Count,CPU-Core-Scope,CPU-Normalized-Source,CPU-Core-Source,Target-PID,Screenshot-Path,Source,Note,Stutter[%],DisplayFrameTime-Mean[ms],DisplayFrameTime-Max[ms],JankTime[ms],FrameObservation[ms],DisplayFrameCount,FrameObservationAvailable,RefreshRate[Hz],MemoryMetric,MemorySource,CPU Source,FPS Scope,Frame Source,OrderedFrames,ApproximateFrameMetrics,SourceDegraded,FPS Updated,Memory Updated,CPU Updated,CPU Normalized Updated,CPU Core Updated,Temperature-CPU[C],Temperature-GPU[C],Temperature-Battery[C],Temperature-Skin[C],Temperature-NPU[C],Temperature-All[C],TemperatureSource,TemperatureScope,Temperature Updated,ThermalState-Level,ThermalState-Name,ThermalStateSource,ThermalStateScope,ThermalStateUpdated,FrameTargetVerificationAvailable,FrameTargetVerified,SurfaceOwnerPid,SurfaceOwnerUid,DuplicateFrameTimestamps,OutOfOrderFrameTimestamps,InvalidFrameIntervals,FrameRingBufferOverrun,FrameSourceSequenceAvailable,FrameSourceSequence,FrameSourceSequenceDiscontinuity,MissingFrameSourceWindows,NoPresentFrames,ResumeGap[ms]");
            for (int i = 0; i < _samples.Count; i++)
            {
                PerfSample sample = _samples[i];
                ScreenshotInfo shot = NearestShot(sample.ElapsedSec);
                string shotPath = shot != null && Math.Abs(shot.ElapsedSec - sample.ElapsedSec) <= 1.6 ? shot.Path : "";
                sb.AppendLine(CsvLine(
                    i.ToString(CultureInfo.InvariantCulture),
                    sample.ElapsedSec.ToString("0.000000", CultureInfo.InvariantCulture),
                    sample.HasFps && sample.FpsUpdated ? sample.Fps.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasJank && sample.FpsUpdated ? sample.Jank.ToString(CultureInfo.InvariantCulture) : "-",
                    sample.HasJank && sample.FpsUpdated ? sample.BigJank.ToString(CultureInfo.InvariantCulture) : "-",
                    sample.HasFrameTimeP95 && sample.FpsUpdated ? sample.FrameTimeP95Ms.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    MetricStatistics.IsPreferredMemorySample(sample, preferredMemoryMetric) ? (sample.MemoryMb * 1024.0).ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasMemory && sample.MemoryUpdated ? (sample.MemoryMb * 1024.0).ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasMemoryRss && sample.MemoryUpdated ? (sample.MemoryRssMb * 1024.0).ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasCpu && sample.CpuUpdated ? sample.CpuPercent.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasCpuNormalized && sample.CpuNormalizedUpdated ? sample.CpuNormalizedPercent.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasCpuCoreUsage && sample.CpuCoreUpdated ? string.Join(";", sample.CpuCorePercents.Select(delegate(double value) { return value.ToString("0.000000", CultureInfo.InvariantCulture); })) : "-",
                    sample.HasCpuCoreUsage && sample.CpuCoreUpdated ? sample.CpuCoreCount.ToString(CultureInfo.InvariantCulture) : "-",
                    sample.HasCpuCoreUsage && sample.CpuCoreUpdated ? sample.CpuCoreScope : "-",
                    sample.HasCpuNormalized && sample.CpuNormalizedUpdated ? sample.CpuNormalizedSource : "-",
                    sample.HasCpuCoreUsage && sample.CpuCoreUpdated ? sample.CpuCoreSource : "-",
                    sample.TargetPid.ToString(CultureInfo.InvariantCulture),
                    shotPath,
                    sample.Source,
                    sample.Note,
                    sample.HasStutter && sample.FpsUpdated ? sample.StutterPercent.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasFrameTimeMean && sample.FpsUpdated ? sample.FrameTimeMeanMs.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasFrameTimeMax && sample.FpsUpdated ? sample.FrameTimeMaxMs.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasStutter && sample.FpsUpdated ? sample.JankTimeMs.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasFrameObservation && sample.FpsUpdated ? sample.FrameObservationMs.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    sample.HasFrameObservation && sample.FpsUpdated ? sample.FrameCount.ToString(CultureInfo.InvariantCulture) : "-",
                    sample.HasFrameObservation && sample.FpsUpdated ? "true" : "false",
                    sample.HasRefreshRate && sample.FpsUpdated ? sample.RefreshRateHz.ToString("0.000000", CultureInfo.InvariantCulture) : "-",
                    MemoryMetricDisplayName(sample.MemoryMetric),
                    sample.MemorySource,
                    sample.CpuSource,
                    sample.FpsScope,
                    sample.FrameSource,
                    sample.OrderedFrames ? "true" : "false",
                    sample.ApproximateFrameMetrics ? "true" : "false",
                    sample.SourceDegraded ? "true" : "false",
                    sample.FpsUpdated ? "true" : "false",
                    sample.MemoryUpdated ? "true" : "false",
                    sample.CpuUpdated ? "true" : "false",
                    sample.CpuNormalizedUpdated ? "true" : "false",
                    sample.CpuCoreUpdated ? "true" : "false",
                    TemperatureCsvValue(sample, "CPU"),
                    TemperatureCsvValue(sample, "GPU"),
                    TemperatureCsvValue(sample, "Battery"),
                    TemperatureCsvValue(sample, "Skin"),
                    TemperatureCsvValue(sample, "NPU"),
                    TemperatureCsvAll(sample),
                    sample.HasTemperature && sample.TemperatureUpdated ? sample.TemperatureSource : "-",
                    sample.HasTemperature && sample.TemperatureUpdated ? sample.TemperatureScope : "-",
                    sample.TemperatureUpdated ? "true" : "false",
                    sample.HasThermalState ? sample.ThermalStateLevel.ToString(CultureInfo.InvariantCulture) : "-",
                    sample.HasThermalState ? ThermalStateLabel(sample) : "-",
                    sample.HasThermalState && sample.ThermalStateUpdated ? sample.ThermalStateSource : "-",
                    sample.HasThermalState && sample.ThermalStateUpdated ? sample.ThermalStateScope : "-",
                    sample.ThermalStateUpdated ? "true" : "false",
                    sample.HasFrameTargetVerification ? "true" : "false",
                    sample.HasFrameTargetVerification ? (sample.FrameTargetVerified ? "true" : "false") : "-",
                    sample.SurfaceOwnerPid > 0 ? sample.SurfaceOwnerPid.ToString(CultureInfo.InvariantCulture) : "-",
                    sample.SurfaceOwnerUid > 0 ? sample.SurfaceOwnerUid.ToString(CultureInfo.InvariantCulture) : "-",
                    sample.DuplicateFrameTimestamps.ToString(CultureInfo.InvariantCulture),
                    sample.OutOfOrderFrameTimestamps.ToString(CultureInfo.InvariantCulture),
                    sample.InvalidFrameIntervals.ToString(CultureInfo.InvariantCulture),
                    sample.FrameRingBufferOverrun ? "true" : "false",
                    sample.HasFrameSourceSequence ? "true" : "false",
                    sample.HasFrameSourceSequence ? sample.FrameSourceSequence.ToString(CultureInfo.InvariantCulture) : "-",
                    sample.FrameSourceSequenceDiscontinuity ? "true" : "false",
                    sample.MissingFrameSourceWindows.ToString(CultureInfo.InvariantCulture),
                    sample.NoPresentFrames ? "true" : "false",
                    sample.ResumeGapMs > 0 ? sample.ResumeGapMs.ToString("0.000000", CultureInfo.InvariantCulture) : "-"));
            }
            return sb;
        }

        private static string TemperatureCsvValue(PerfSample sample, string sensor)
        {
            if (sample == null || !sample.HasTemperature || !sample.TemperatureUpdated || sample.TemperatureCelsius == null) return "-";
            double value;
            return sample.TemperatureCelsius.TryGetValue(sensor, out value)
                ? value.ToString("0.000000", CultureInfo.InvariantCulture)
                : "-";
        }

        private static string TemperatureCsvAll(PerfSample sample)
        {
            if (sample == null || !sample.HasTemperature || !sample.TemperatureUpdated || sample.TemperatureCelsius == null) return "-";
            return string.Join(";", sample.TemperatureCelsius
                .OrderBy(delegate(KeyValuePair<string, double> pair) { return pair.Key; }, StringComparer.OrdinalIgnoreCase)
                .Select(delegate(KeyValuePair<string, double> pair)
                {
                    return pair.Key + "=" + pair.Value.ToString("0.000000", CultureInfo.InvariantCulture);
                }));
        }

        private bool BeginFileOperation(string message)
        {
            if (_fileOperationInProgress)
            {
                SetStatus("文件操作进行中，请稍候完成后再继续。");
                return false;
            }
            _fileOperationInProgress = true;
            UpdateFileButtons();
            SetStatus(message);
            return true;
        }

        private void EndFileOperation()
        {
            _fileOperationInProgress = false;
            UpdateFileButtons();
        }

        private void UpdateFileButtons()
        {
            bool enabled = !_fileOperationInProgress;
            SetButtonEnabled(_openButton, enabled);
            SetButtonEnabled(_saveButton, enabled);
            SetButtonEnabled(_exportButton, enabled);
        }

        private static string TempSiblingPath(string path)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            string fileName = Path.GetFileName(path);
            string tempName = "." + fileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            return string.IsNullOrWhiteSpace(directory) ? tempName : Path.Combine(directory, tempName);
        }

        private static void ReplaceFileAtomic(string tempPath, string finalPath)
        {
            File.Move(tempPath, finalPath, true);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        private static void WriteAllTextAtomic(string path, string text, Encoding encoding)
        {
            string fullPath = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            string tempPath = TempSiblingPath(fullPath);
            try
            {
                File.WriteAllText(tempPath, text, encoding);
                ReplaceFileAtomic(tempPath, fullPath);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }

        private void SaveSessionArchive(string path, SessionDocument document)
        {
            string fullPath = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            string tempPath = TempSiblingPath(fullPath);

            JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
            try
            {
                using (ZipArchive archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    ZipArchiveEntry manifest = archive.CreateEntry("session.json", CompressionLevel.Optimal);
                    using (Stream stream = manifest.Open())
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(JsonSerializer.Serialize(document, options));
                    }

                    foreach (SessionScreenshot shot in document.Screenshots)
                    {
                        if (!File.Exists(shot.OriginalPath)) continue;
                        ZipArchiveEntry entry = archive.CreateEntry(shot.ArchivePath, CompressionLevel.Optimal);
                        using (Stream input = File.OpenRead(shot.OriginalPath))
                        using (Stream output = entry.Open())
                        {
                            input.CopyTo(output);
                        }
                    }
                }
                ReplaceFileAtomic(tempPath, fullPath);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }

        private void LoadSessionArchive(string path)
        {
            if (_isCapturing) StopCaptureInternal("采集已停止。");
            ApplySessionDocument(LoadSessionArchiveDocument(path));
        }

        private SessionDocument LoadSessionArchiveDocument(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string sessionDir = Path.Combine(_dataDir, "opened", Path.GetFileNameWithoutExtension(fullPath) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(sessionDir);
            string shotDir = Path.Combine(sessionDir, "screenshots");
            Directory.CreateDirectory(shotDir);

            SessionDocument document;
            using (ZipArchive archive = ZipFile.OpenRead(fullPath))
            {
                ZipArchiveEntry manifest = archive.GetEntry("session.json");
                if (manifest == null) throw new InvalidDataException("现场文件缺少 session.json。");
                using (Stream stream = manifest.Open())
                {
                    document = JsonSerializer.Deserialize<SessionDocument>(stream);
                }
                if (document == null) throw new InvalidDataException("现场文件内容为空。");

                foreach (SessionScreenshot shot in document.Screenshots ?? new List<SessionScreenshot>())
                {
                    if (string.IsNullOrWhiteSpace(shot.ArchivePath)) continue;
                    ZipArchiveEntry entry = archive.GetEntry(shot.ArchivePath);
                    if (entry == null) continue;
                    string target = Path.Combine(shotDir, Path.GetFileName(shot.ArchivePath));
                    using (Stream input = entry.Open())
                    using (Stream output = File.Create(target))
                    {
                        input.CopyTo(output);
                    }
                    shot.OriginalPath = target;
                }
            }

            return document;
        }

        private SessionDocument BuildSessionDocument()
        {
            SessionDocument document = new SessionDocument
            {
                Format = "motuperf-session",
                Version = 5,
                AppVersion = Program.AppVersion,
                SavedAt = DateTime.Now,
                StartedAt = _sessionStartedAt == default(DateTime) && _samples.Count > 0 ? _samples[0].Timestamp : _sessionStartedAt,
                Device = _selectedDevice,
                App = _selectedApp,
                Process = _selectedProcess,
                SelectedBundleId = _selectedBundleId,
                SelectedTime = _selectedTime,
                FollowLatest = _followLatest,
                CaptureScreenshots = _screenshotMetricCheck != null && _screenshotMetricCheck.IsChecked == true,
                CaptureTemperature = _temperatureMetricCheck != null && _temperatureMetricCheck.IsChecked == true,
                CaptureThermalState = _thermalStateMetricCheck != null && _thermalStateMetricCheck.IsChecked == true,
                Samples = _samples.ToList(),
                Screenshots = new List<SessionScreenshot>()
            };
            for (int i = 0; i < _shots.Count; i++)
            {
                ScreenshotInfo shot = _shots[i];
                document.Screenshots.Add(new SessionScreenshot
                {
                    Timestamp = shot.Timestamp,
                    ElapsedSec = shot.ElapsedSec,
                    OriginalPath = shot.Path,
                    ArchivePath = "screenshots/" + i.ToString("00000", CultureInfo.InvariantCulture) + "-" + Path.GetFileName(shot.Path)
                });
            }
            return document;
        }

        private void ApplySessionDocument(SessionDocument document)
        {
            ClearSamples();
            _selectedDevice = document.Device;
            _selectedApp = document.App;
            _selectedProcess = document.Process;
            _selectedBundleId = string.IsNullOrWhiteSpace(document.SelectedBundleId) ? DefaultBundleForSelectedDevice() : document.SelectedBundleId;
            _sessionStartedAt = document.StartedAt;
            if (_temperatureMetricCheck != null)
            {
                _temperatureMetricCheck.IsChecked = document.Version >= 4 ? document.CaptureTemperature : true;
            }
            if (_thermalStateMetricCheck != null)
            {
                _thermalStateMetricCheck.IsChecked = document.Version >= 5 ? document.CaptureThermalState : true;
            }
            foreach (PerfSample sample in document.Samples ?? new List<PerfSample>())
            {
                _samples.Add(NormalizeSessionSample(sample));
            }
            _samples.Sort(delegate(PerfSample left, PerfSample right)
            {
                return left.ElapsedSec.CompareTo(right.ElapsedSec);
            });
            foreach (SessionScreenshot stored in document.Screenshots ?? new List<SessionScreenshot>())
            {
                ScreenshotInfo shot = new ScreenshotInfo
                {
                    Timestamp = stored.Timestamp,
                    ElapsedSec = stored.ElapsedSec,
                    Path = stored.OriginalPath
                };
                _shots.Add(shot);
                if (_shotStrip != null && File.Exists(shot.Path)) _shotStrip.Children.Add(CreateScreenshotItem(shot));
            }
            _selectedTime = document.SelectedTime;
            _followLatest = document.FollowLatest;
            if (_selectedTime.HasValue)
            {
                ScreenshotInfo shot = NearestShot(_selectedTime.Value);
                UpdatePreview(shot);
                SetSelectedShot(shot);
            }
            else if (_shots.Count > 0)
            {
                UpdatePreview(_shots[_shots.Count - 1]);
            }
            UpdateTargetSummary();
            Render();
        }

        private void SyncScreenshotOptions()
        {
            _screenshots.Enabled = _screenshotMetricCheck != null && _screenshotMetricCheck.IsChecked == true;
            _screenshots.IntervalSec = 3;
            SetStatus("截图设置：" + (_screenshots.Enabled ? "已开启" : "已关闭") + " / " + _screenshots.IntervalSec + " 秒");
        }

        private void UpdatePreview(ScreenshotInfo shot)
        {
            if (_preview == null || _preview.Visibility != Visibility.Visible) return;
            if (shot == null || !File.Exists(shot.Path)) return;
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(shot.Path);
            bitmap.EndInit();
            _preview.Source = bitmap;
        }

        private ScreenshotInfo NearestShot(double elapsed)
        {
            return _shots.OrderBy(delegate(ScreenshotInfo item) { return Math.Abs(item.ElapsedSec - elapsed); }).FirstOrDefault();
        }

        private void SetSelectedShot(ScreenshotInfo shot)
        {
            if (_shotStrip == null) return;
            foreach (UIElement child in _shotStrip.Children)
            {
                Border border = child as Border;
                if (border == null) continue;
                ScreenshotInfo item = border.Tag as ScreenshotInfo;
                bool selected = shot != null && item == shot;
                border.BorderBrush = selected ? AccentCyan : Brushes.Transparent;
                border.Background = selected ? Brush(28, 42, 55) : Brushes.Transparent;
            }
            ScrollToShot(shot);
        }

        private UIElement CreateDataTabs()
        {
            Border panel = new Border
            {
                Background = Brush(20, 21, 30),
                BorderBrush = Brush(41, 43, 58),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 8, 0, 12)
            };
            StackPanel root = new StackPanel();

            Grid tabs = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _liveDataTabButton = DataTabButton("实时数据", true, delegate
            {
                _showSelectedData = false;
                UpdateDataTabState();
            });
            _selectedDataTabButton = DataTabButton("选中数据", false, delegate
            {
                _showSelectedData = true;
                UpdateDataTabState();
            });
            tabs.Children.Add(_liveDataTabButton);
            Grid.SetColumn(_selectedDataTabButton, 1);
            tabs.Children.Add(_selectedDataTabButton);
            root.Children.Add(tabs);

            Grid content = new Grid();
            _liveDataPanel = CreateDataGrid(_liveMetricValues);
            _selectedDataPanel = CreateDataGrid(_selectedMetricValues);
            content.Children.Add(_liveDataPanel);
            content.Children.Add(_selectedDataPanel);
            root.Children.Add(content);

            panel.Child = root;
            UpdateDataTabState();
            return panel;
        }

        private static Button DataTabButton(string text, bool active, RoutedEventHandler handler)
        {
            Button button = new Button
            {
                Content = text,
                Foreground = active ? TextPrimary : TextSecondary,
                Background = active ? Brush(42, 44, 60) : Brushes.Transparent,
                BorderBrush = active ? AccentBlue : Brush(42, 44, 60),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Style = DarkButtonStyle()
            };
            button.Click += handler;
            return button;
        }

        private static Button PanelToggleButton(string text, string tooltip)
        {
            Button button = new Button
            {
                Content = text,
                ToolTip = tooltip,
                Foreground = TextSecondary,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                MinWidth = 34,
                MinHeight = 30,
                VerticalAlignment = VerticalAlignment.Top,
                Style = DarkButtonStyle()
            };
            return button;
        }

        private void ToggleParamsPanel(bool collapsed)
        {
            _paramsCollapsed = collapsed;
            if (_paramsColumn != null) _paramsColumn.Width = collapsed ? new GridLength(42) : new GridLength(398);
            if (_paramsExpandedContent != null) _paramsExpandedContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            if (_paramsExpandButton != null) _paramsExpandButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            if (_paramsCollapseButton != null) _paramsCollapseButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        private static Grid CreateDataGrid(Dictionary<string, TextBlock> values)
        {
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 6; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            AddMetricTile(grid, values, "pid", "PID", 0, 0);
            AddMetricTile(grid, values, "elapsed", "时长", 0, 1);
            AddMetricTile(grid, values, "cpu", "CPU Raw", 1, 0);
            AddMetricTile(grid, values, "memory", "内存", 1, 1);
            AddMetricTile(grid, values, "fps", "FPS", 2, 0);
            AddMetricTile(grid, values, "frame", "FrameTime", 2, 1);
            AddMetricTile(grid, values, "jank", "Jank", 3, 0);
            AddMetricTile(grid, values, "bigJank", "BigJank", 3, 1);
            AddMetricTile(grid, values, "temperature", "Temperature", 4, 0);
            AddMetricTile(grid, values, "temperatureSensors", "Temp Sensors", 4, 1);
            AddMetricTile(grid, values, "thermalState", "Thermal State", 5, 0);
            AddMetricTile(grid, values, "thermalStateSource", "Thermal Source", 5, 1);
            return grid;
        }

        private static void AddMetricTile(Grid grid, Dictionary<string, TextBlock> values, string key, string label, int row, int column)
        {
            Border tile = new Border
            {
                Background = Brush(27, 28, 39),
                BorderBrush = Brush(45, 47, 63),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(column == 0 ? 0 : 4, row == 0 ? 0 : 6, column == 0 ? 4 : 0, 0)
            };
            StackPanel stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = TextSecondary,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            TextBlock value = new TextBlock
            {
                Text = "--",
                Foreground = TextPrimary,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stack.Children.Add(value);
            tile.Child = stack;
            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, column);
            grid.Children.Add(tile);
            values[key] = value;
        }

        private void UpdateDataPanels(PerfSample latest, PerfSample selected)
        {
            string preferredMemoryMetric = MetricStatistics.PreferredMemoryMetric(_samples);
            PerfSample latestMemory = null;
            PerfSample selectedMemory = null;
            double selectedDistance = double.MaxValue;
            foreach (PerfSample memorySample in _samples)
            {
                if (!MetricStatistics.IsPreferredMemorySample(memorySample, preferredMemoryMetric)) continue;
                latestMemory = memorySample;
                if (selected == null) continue;
                double distance = Math.Abs(memorySample.ElapsedSec - selected.ElapsedSec);
                if (distance >= selectedDistance) continue;
                selectedDistance = distance;
                selectedMemory = memorySample;
            }
            if (selectedMemory == null) selectedMemory = latestMemory;
            PerfSample latestTemperature = LatestTemperatureSample();
            PerfSample selectedTemperature = NearestTemperatureSample(selected);
            PerfSample latestThermalState = LatestThermalStateSample();
            PerfSample selectedThermalState = NearestThermalStateSample(selected);
            UpdateDataPanel(_liveMetricValues, latest, latestMemory, latestTemperature, latestThermalState);
            UpdateDataPanel(_selectedMetricValues, selected, selectedMemory, selectedTemperature, selectedThermalState);
        }

        private static void UpdateDataPanel(Dictionary<string, TextBlock> values, PerfSample sample, PerfSample memorySample, PerfSample temperatureSample, PerfSample thermalStateSample)
        {
            if (values.Count == 0) return;
            SetMetricValue(values, "pid", sample == null ? "--" : sample.TargetPid.ToString(CultureInfo.InvariantCulture));
            SetMetricValue(values, "elapsed", sample == null ? "--" : FormatElapsed(sample.ElapsedSec));
            SetMetricValue(values, "cpu", sample == null || !sample.HasCpu ? "--" : string.Format(CultureInfo.InvariantCulture, "{0:0.0}%", sample.CpuPercent));
            SetMetricValue(values, "memory", memorySample == null || !memorySample.HasMemory
                ? "--"
                : string.Format(CultureInfo.InvariantCulture, "{0} {1:0} MB", MemoryMetricDisplayName(memorySample.MemoryMetric), memorySample.MemoryMb));
            SetMetricValue(values, "fps", sample == null || !sample.HasFps ? "--" : string.Format(CultureInfo.InvariantCulture, "{0:0.0}", sample.Fps));
            SetMetricValue(values, "frame", sample == null || !sample.HasFrameTimeMax ? "--" : string.Format(CultureInfo.InvariantCulture, "{0:0.0} ms", DisplayFrameTimeMs(sample)));
            SetMetricValue(values, "jank", sample == null || !sample.HasJank ? "--" : sample.Jank.ToString(CultureInfo.InvariantCulture));
            SetMetricValue(values, "bigJank", sample == null || !sample.HasJank ? "--" : sample.BigJank.ToString(CultureInfo.InvariantCulture));
            SetMetricValue(values, "temperature", TemperatureSummary(temperatureSample));
            SetMetricValue(values, "temperatureSensors", TemperatureSensorSummary(temperatureSample));
            SetMetricValue(values, "thermalState", ThermalStateSummary(thermalStateSample));
            SetMetricValue(values, "thermalStateSource", ThermalStateSourceSummary(thermalStateSample));
        }

        private PerfSample LatestTemperatureSample()
        {
            return _samples.LastOrDefault(HasTemperatureValues);
        }

        private PerfSample NearestTemperatureSample(PerfSample reference)
        {
            if (reference == null) return LatestTemperatureSample();
            return _samples
                .Where(HasTemperatureValues)
                .OrderBy(delegate(PerfSample sample) { return Math.Abs(sample.ElapsedSec - reference.ElapsedSec); })
                .FirstOrDefault() ?? LatestTemperatureSample();
        }

        private static bool HasTemperatureValues(PerfSample sample)
        {
            return sample != null && sample.HasTemperature && sample.TemperatureCelsius != null && sample.TemperatureCelsius.Count > 0;
        }

        private static string TemperatureSummary(PerfSample sample)
        {
            if (!HasTemperatureValues(sample)) return "--";
            KeyValuePair<string, double> hottest = sample.TemperatureCelsius
                .OrderByDescending(delegate(KeyValuePair<string, double> pair) { return pair.Value; })
                .First();
            return string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0}\u2103", hottest.Key, hottest.Value);
        }

        private static string TemperatureSensorSummary(PerfSample sample)
        {
            if (!HasTemperatureValues(sample)) return "--";
            return string.Join(" / ", ChartCanvas.TemperatureSensorNames(new[] { sample }));
        }

        private PerfSample LatestThermalStateSample()
        {
            return _samples.LastOrDefault(HasThermalStateValue);
        }

        private PerfSample NearestThermalStateSample(PerfSample reference)
        {
            if (reference == null) return LatestThermalStateSample();
            return _samples
                .Where(delegate(PerfSample sample) { return HasThermalStateValue(sample) && sample.ElapsedSec <= reference.ElapsedSec + 0.000001; })
                .OrderByDescending(delegate(PerfSample sample) { return sample.ElapsedSec; })
                .FirstOrDefault();
        }

        private static bool HasThermalStateValue(PerfSample sample)
        {
            return sample != null && sample.HasThermalState && sample.ThermalStateLevel >= 0 && sample.ThermalStateLevel <= 6;
        }

        private static string ThermalStateSummary(PerfSample sample)
        {
            if (!HasThermalStateValue(sample)) return "--";
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", sample.ThermalStateLevel, ThermalStateLabel(sample));
        }

        private static string ThermalStateSourceSummary(PerfSample sample)
        {
            if (!HasThermalStateValue(sample) || string.IsNullOrWhiteSpace(sample.ThermalStateSource)) return "--";
            return sample.ThermalStateSource;
        }

        private static string ThermalStateLabel(PerfSample sample)
        {
            string name = (sample.ThermalStateName ?? "").Trim().ToLowerInvariant();
            if (name == "nominal" || name == "none") return "正常";
            if (name == "fair") return "升温";
            if (name == "light") return "轻微";
            if (name == "moderate") return "中度";
            if (name == "serious" || name == "severe") return "严重";
            if (name == "critical") return "临界";
            if (name == "emergency") return "紧急";
            if (name == "shutdown") return "关机";
            return "未知";
        }

        private static void SetMetricValue(Dictionary<string, TextBlock> values, string key, string value)
        {
            TextBlock text;
            if (values.TryGetValue(key, out text)) text.Text = value;
        }

        private void UpdateDataTabState()
        {
            SetDataTabState(_liveDataTabButton, !_showSelectedData);
            SetDataTabState(_selectedDataTabButton, _showSelectedData);
            if (_liveDataPanel != null) _liveDataPanel.Visibility = _showSelectedData ? Visibility.Collapsed : Visibility.Visible;
            if (_selectedDataPanel != null) _selectedDataPanel.Visibility = _showSelectedData ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void SetDataTabState(Button button, bool active)
        {
            if (button == null) return;
            button.Foreground = active ? TextPrimary : TextSecondary;
            button.Background = active ? Brush(42, 44, 60) : Brushes.Transparent;
            button.BorderBrush = active ? AccentBlue : Brush(42, 44, 60);
        }

        private void ScrollToShot(ScreenshotInfo shot)
        {
            if (shot == null || _shotScrollViewer == null || _shotStrip == null) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_shotScrollViewer == null || _shotStrip == null) return;
                FrameworkElement targetElement = null;
                foreach (UIElement child in _shotStrip.Children)
                {
                    FrameworkElement element = child as FrameworkElement;
                    if (element != null && ReferenceEquals(element.Tag, shot))
                    {
                        targetElement = element;
                        break;
                    }
                }
                if (targetElement == null) return;
                _shotStrip.UpdateLayout();
                _shotScrollViewer.UpdateLayout();
                Point location = targetElement.TransformToAncestor(_shotStrip).Transform(new Point(0, 0));
                double cardWidth = Math.Max(1, targetElement.ActualWidth);
                double viewportWidth = Math.Max(1, _shotScrollViewer.ViewportWidth);
                double target = location.X + cardWidth / 2.0 - viewportWidth / 2.0;
                target = Math.Max(0, Math.Min(target, _shotScrollViewer.ScrollableWidth));
                _shotScrollViewer.ScrollToHorizontalOffset(target);
            }), DispatcherPriority.Background);
        }

        private void ScrollToLatestShot()
        {
            if (_shotScrollViewer == null || _shotStrip == null || _shotStrip.Children.Count == 0) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_shotScrollViewer != null) _shotScrollViewer.ScrollToEnd();
            }), DispatcherPriority.Background);
        }

        private string SelectedUdid()
        {
            DeviceInfo device = _selectedDevice ?? (_deviceCombo == null ? null : _deviceCombo.SelectedItem as DeviceInfo);
            return device == null ? "" : device.Udid;
        }

        private string DefaultBundleForSelectedDevice()
        {
            DeviceInfo device = _selectedDevice ?? (_deviceCombo == null ? null : _deviceCombo.SelectedItem as DeviceInfo);
            return DeviceLookupService.IsAndroid(device) ? "com.tencent.mm" : "com.tencent.xin";
        }

        private void SetStatus(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { SetStatus(message); }));
                return;
            }
            if (_status != null) _status.Text = message;
        }

        private void UpdateTargetSummary()
        {
            AppInfo app = _selectedApp ?? (_appCombo == null ? null : _appCombo.SelectedItem as AppInfo);
            ProcessInfo process = _selectedProcess ?? (_processCombo == null ? null : _processCombo.SelectedItem as ProcessInfo);
            string appName = app == null ? "未选应用" : (string.IsNullOrWhiteSpace(app.Name) ? app.BundleId : app.Name);
            string processName = process == null ? "未选进程" : process.Name + " / pid " + process.Pid;
            if (_targetSummary != null) _targetSummary.Text = appName + " / " + processName;
            UpdateDeviceInfoPanel();
            UpdateMetricLayout();
        }

        private static List<string> Terms(string text)
        {
            return (text ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(delegate(string item) { return item.ToLowerInvariant(); }).ToList();
        }

        private static bool Match(List<string> terms, params string[] values)
        {
            if (terms.Count == 0) return true;
            string haystack = string.Join(" ", values ?? new string[0]).ToLowerInvariant();
            return terms.All(delegate(string term) { return haystack.Contains(term); });
        }

        private static string FormatElapsed(double seconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (ts.TotalHours >= 1) return string.Format("{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            return ts.TotalMinutes >= 1 ? string.Format("{0}:{1:00}", (int)ts.TotalMinutes, ts.Seconds) : string.Format("{0:0.0}s", seconds);
        }

        private static double DisplayFrameTimeMs(PerfSample sample)
        {
            if (sample == null) return 0;
            if (sample.HasFrameTimeMax) return sample.FrameTimeMaxMs;
            return 0;
        }

        private static PerfSample NormalizeSessionSample(PerfSample sample)
        {
            if (sample == null) return new PerfSample();
            if (!sample.HasFreshnessMetadata)
            {
                sample.FpsUpdated = sample.HasFps;
                sample.MemoryUpdated = sample.HasMemory;
                sample.CpuUpdated = sample.HasCpu;
                sample.CpuNormalizedUpdated = sample.HasCpuNormalized;
                sample.CpuCoreUpdated = sample.HasCpuCoreUsage;
                sample.TemperatureUpdated = sample.HasTemperature;
                sample.ThermalStateUpdated = sample.HasThermalState;
                sample.HasFreshnessMetadata = true;
            }
            if (sample.HasFps && !IsValidFpsValue(sample.Fps))
            {
                sample.HasFps = false;
                sample.FpsUpdated = false;
                sample.Fps = 0;
                sample.HasAverageFps = false;
                sample.AverageFps = 0;
                sample.HasFrameTime = false;
                sample.FrameTimeMs = 0;
                sample.HasFrameTimeMean = false;
                sample.FrameTimeMeanMs = 0;
                sample.HasFrameTimeP95 = false;
                sample.FrameTimeP95Ms = 0;
                sample.HasFrameTimeMax = false;
                sample.FrameTimeMaxMs = 0;
                sample.HasJank = false;
                sample.Jank = 0;
                sample.BigJank = 0;
                sample.HasStutter = false;
                sample.StutterPercent = 0;
                sample.JankTimeMs = 0;
                sample.HasFrameObservation = false;
                sample.FrameCount = 0;
                sample.FrameObservationMs = 0;
            }
            if (sample.HasAverageFps && !IsValidFpsValue(sample.AverageFps))
            {
                sample.HasAverageFps = false;
                sample.AverageFps = sample.HasFps ? sample.Fps : 0;
            }
            if (sample.HasFrameTime && !IsValidFrameTimeValue(sample.FrameTimeMs))
            {
                sample.HasFrameTime = false;
                sample.FrameTimeMs = 0;
            }
            if (sample.HasFrameTime)
            {
                sample.HasFrameTimeMean = sample.HasFrameTimeMean || IsValidFrameTimeValue(sample.FrameTimeMeanMs);
                sample.HasFrameTimeP95 = sample.HasFrameTimeP95 || IsValidFrameTimeValue(sample.FrameTimeP95Ms);
                sample.HasFrameTimeMax = sample.HasFrameTimeMax || IsValidFrameTimeValue(sample.FrameTimeMaxMs);
                if (!sample.HasFrameTimeP95)
                {
                    sample.HasFrameTimeP95 = true;
                    sample.FrameTimeP95Ms = sample.FrameTimeMs;
                }
            }
            if (!sample.HasFrameTimeMean || !IsValidFrameTimeValue(sample.FrameTimeMeanMs))
            {
                sample.HasFrameTimeMean = false;
                sample.FrameTimeMeanMs = 0;
            }
            if (!sample.HasFrameTimeP95 || !IsValidFrameTimeValue(sample.FrameTimeP95Ms))
            {
                sample.HasFrameTimeP95 = false;
                sample.FrameTimeP95Ms = 0;
            }
            if (!sample.HasFrameTimeMax || !IsValidFrameTimeValue(sample.FrameTimeMaxMs))
            {
                sample.HasFrameTimeMax = false;
                sample.FrameTimeMaxMs = 0;
            }
            if (!sample.HasFrameObservation && sample.FrameObservationMs > 0 && sample.FrameCount > 0)
            {
                sample.HasFrameObservation = true;
            }
            if (sample.HasFrameObservation && (
                !IsFiniteSessionMetric(sample.FrameObservationMs)
                || sample.FrameObservationMs <= 0
                || sample.FrameCount < 0
                || (sample.FrameCount == 0 && sample.HasFps && sample.Fps > 0)))
            {
                sample.HasFrameObservation = false;
                sample.FrameCount = 0;
                sample.FrameObservationMs = 0;
            }
            if (!IsFiniteSessionMetric(sample.ResumeGapMs) || sample.ResumeGapMs <= 0)
            {
                sample.ResumeGapMs = 0;
            }
            if (sample.NoPresentFrames && (!sample.HasFrameObservation || sample.FrameCount != 0 || !sample.HasFps || sample.Fps != 0))
            {
                sample.NoPresentFrames = false;
            }
            if (sample.HasRefreshRate && (!IsFiniteSessionMetric(sample.RefreshRateHz) || sample.RefreshRateHz <= 0 || sample.RefreshRateHz > 1000.0))
            {
                sample.HasRefreshRate = false;
                sample.RefreshRateHz = 0;
            }
            sample.DuplicateFrameTimestamps = Math.Max(0, sample.DuplicateFrameTimestamps);
            sample.OutOfOrderFrameTimestamps = Math.Max(0, sample.OutOfOrderFrameTimestamps);
            sample.InvalidFrameIntervals = Math.Max(0, sample.InvalidFrameIntervals);
            if (!sample.HasFrameSourceSequence || sample.FrameSourceSequence <= 0)
            {
                sample.HasFrameSourceSequence = false;
                sample.FrameSourceSequence = 0;
                sample.FrameSourceSequenceDiscontinuity = false;
                sample.MissingFrameSourceWindows = 0;
            }
            else
            {
                sample.MissingFrameSourceWindows = Math.Max(0, sample.MissingFrameSourceWindows);
            }
            bool derivedFrameMetricsUnavailable = sample.OutOfOrderFrameTimestamps > 0
                || sample.InvalidFrameIntervals > 0
                || sample.FrameRingBufferOverrun;
            if (sample.DuplicateFrameTimestamps > 0
                || sample.OutOfOrderFrameTimestamps > 0
                || sample.InvalidFrameIntervals > 0
                || sample.FrameRingBufferOverrun)
            {
                sample.SourceDegraded = true;
                sample.ApproximateFrameMetrics = true;
            }
            if (derivedFrameMetricsUnavailable)
            {
                sample.HasFrameTime = false;
                sample.FrameTimeMs = 0;
                sample.HasFrameTimeMean = false;
                sample.FrameTimeMeanMs = 0;
                sample.HasFrameTimeP95 = false;
                sample.FrameTimeP95Ms = 0;
                sample.HasFrameTimeMax = false;
                sample.FrameTimeMaxMs = 0;
                sample.HasJank = false;
                sample.Jank = 0;
                sample.BigJank = 0;
                sample.HasStutter = false;
                sample.StutterPercent = 0;
                sample.JankTimeMs = 0;
            }
            MetricStatistics.NormalizeFrameTargetVerification(sample);
            if (sample.HasJank)
            {
                if (!IsFiniteSessionMetric(sample.Jank) || !IsFiniteSessionMetric(sample.BigJank))
                {
                    sample.HasJank = false;
                    sample.Jank = 0;
                    sample.BigJank = 0;
                    sample.HasStutter = false;
                    sample.StutterPercent = 0;
                    sample.JankTimeMs = 0;
                }
                else
                {
                    sample.Jank = Math.Max(sample.Jank, sample.BigJank);
                    if (!IsFiniteSessionMetric(sample.JankTimeMs))
                    {
                        sample.JankTimeMs = 0;
                        sample.HasStutter = false;
                        sample.StutterPercent = 0;
                    }
                }
            }
            if (sample.HasJank && !MetricStatistics.IsReliableJankSample(sample))
            {
                sample.HasJank = false;
                sample.Jank = 0;
                sample.BigJank = 0;
                sample.HasStutter = false;
                sample.StutterPercent = 0;
                sample.JankTimeMs = 0;
            }
            if (sample.HasStutter && (
                !sample.HasJank
                || !sample.HasFrameObservation
                || !IsFiniteSessionMetric(sample.StutterPercent)
                || sample.StutterPercent > 100.0
                || !IsFiniteSessionMetric(sample.JankTimeMs)
                || sample.JankTimeMs > sample.FrameObservationMs
                || (sample.Jank > 0 && sample.JankTimeMs <= 0)))
            {
                sample.HasStutter = false;
                sample.StutterPercent = 0;
                sample.JankTimeMs = 0;
            }
            if (sample.HasMemory && (!IsFiniteSessionMetric(sample.MemoryMb) || sample.MemoryMb <= 0))
            {
                sample.HasMemory = false;
                sample.MemoryMb = 0;
                sample.MemoryUpdated = false;
            }
            if (!sample.HasMemory || (sample.HasMemoryRss && (!IsFiniteSessionMetric(sample.MemoryRssMb) || sample.MemoryRssMb <= 0)))
            {
                sample.HasMemoryRss = false;
                sample.MemoryRssMb = 0;
            }
            if (sample.HasCpu)
            {
                double? cpu = NormalizeCpuRawValue(sample.CpuPercent);
                if (cpu.HasValue)
                {
                    sample.CpuPercent = cpu.Value;
                }
                else
                {
                    sample.HasCpu = false;
                    sample.CpuPercent = 0;
                    sample.CpuUpdated = false;
                }
            }
            if (sample.HasCpuNormalized)
            {
                double? normalizedCpu = NormalizeCpuRawValue(sample.CpuNormalizedPercent);
                if (normalizedCpu.HasValue)
                {
                    sample.CpuNormalizedPercent = normalizedCpu.Value;
                }
                else
                {
                    sample.HasCpuNormalized = false;
                    sample.CpuNormalizedPercent = 0;
                    sample.CpuNormalizedUpdated = false;
                }
            }
            if (sample.CpuCorePercents == null) sample.CpuCorePercents = new List<double>();
            if (sample.HasCpuCoreUsage
                && (sample.CpuCoreCount <= 0
                    || sample.CpuCorePercents.Count != sample.CpuCoreCount
                    || !IsValidCpuCoreValues(sample.CpuCorePercents)))
            {
                sample.HasCpuCoreUsage = false;
                sample.CpuCorePercents.Clear();
                sample.CpuCoreCount = 0;
                sample.CpuCoreUpdated = false;
            }
            Dictionary<string, double> validTemperatures = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (sample.TemperatureCelsius != null)
            {
                foreach (KeyValuePair<string, double> pair in sample.TemperatureCelsius)
                {
                    string sensor = (pair.Key ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(sensor) && IsValidTemperatureCelsius(pair.Value))
                    {
                        validTemperatures[sensor] = pair.Value;
                    }
                }
            }
            sample.TemperatureCelsius = validTemperatures;
            sample.HasTemperature = validTemperatures.Count > 0;
            if (!sample.HasTemperature)
            {
                sample.TemperatureUpdated = false;
                sample.TemperatureSource = "";
                sample.TemperatureScope = "";
            }
            if (!sample.HasThermalState
                || sample.ThermalStateLevel < 0
                || sample.ThermalStateLevel > 6
                || !IsValidThermalStateName(sample.ThermalStateLevel, sample.ThermalStateName))
            {
                sample.HasThermalState = false;
                sample.ThermalStateLevel = 0;
                sample.ThermalStateName = "";
                sample.ThermalStateUpdated = false;
                sample.ThermalStateSource = "";
                sample.ThermalStateScope = "";
            }
            return sample;
        }

        private static bool IsFiniteSessionMetric(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
        }

        private static bool IsValidFpsValue(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 240.0;
        }

        private static bool IsValidFrameTimeValue(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 && value <= 60000.0;
        }

        private static double? NormalizeCpuRawValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return null;
            return value;
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

        private static bool IsValidTemperatureCelsius(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= -20.0 && value <= 120.0;
        }

        private static bool IsValidThermalStateName(int level, string name)
        {
            string normalized = (name ?? "").Trim().ToLowerInvariant();
            if (level == 0) return normalized == "nominal" || normalized == "none";
            if (level == 1) return normalized == "fair" || normalized == "light";
            if (level == 2) return normalized == "serious" || normalized == "moderate";
            if (level == 3) return normalized == "critical" || normalized == "severe";
            if (level == 4) return normalized == "critical";
            if (level == 5) return normalized == "emergency";
            if (level == 6) return normalized == "shutdown";
            return false;
        }

        private ExportStats ComputeExportStats()
        {
            ExportStats stats = new ExportStats();
            FpsStatistics fpsStats = MetricStatistics.ComputeFps(_samples);
            if (fpsStats.HasData)
            {
                stats.HasFpsStats = true;
                stats.FpsAvg = fpsStats.Average;
                stats.FpsVariance = fpsStats.Variance;
                stats.FpsStdDev = fpsStats.StandardDeviation;
                stats.FpsMinimum = fpsStats.Minimum;
                stats.FpsMedian = fpsStats.Median;
                stats.FpsMedianRangePercent = fpsStats.MedianRangePercent;
                stats.FpsGe18Percent = fpsStats.FpsGe18Percent;
                stats.FpsGe25Percent = fpsStats.FpsGe25Percent;
                stats.FpsDropPerHour = fpsStats.DropPerHour;
                stats.FpsSourceTier = fpsStats.SourceTier;
                stats.FpsSampleCount = fpsStats.SampleCount;
                stats.FpsExcludedSampleCount = fpsStats.ExcludedSampleCount;
                stats.FpsObservationSec = fpsStats.TotalDurationSec;
            }
            JankStatistics jankStats = MetricStatistics.ComputeJank(_samples);
            if (jankStats.HasRateData)
            {
                stats.HasJankStats = true;
                stats.JankPer10Min = jankStats.JankPer10Min;
                stats.BigJankPer10Min = jankStats.BigJankPer10Min;
                stats.JankSampleCount = jankStats.SampleCount;
                stats.JankExcludedSampleCount = jankStats.ExcludedSampleCount;
                stats.JankObservationSec = jankStats.ObservationSec;
            }
            if (jankStats.HasStutterData)
            {
                stats.HasStutterStats = true;
                stats.StutterPercent = jankStats.StutterPercent;
            }
            MemoryStatistics memoryStats = MetricStatistics.ComputeMemory(_samples);
            if (memoryStats.HasData)
            {
                stats.HasMemoryStats = true;
                stats.MemoryAvgMb = memoryStats.AverageMb;
                stats.MemoryPeakMb = memoryStats.PeakMb;
            }
            List<PerfSample> cpuSamples = _samples.Where(delegate(PerfSample sample) { return sample.HasCpu && sample.CpuUpdated; }).ToList();
            if (cpuSamples.Count > 0)
            {
                stats.HasCpuStats = true;
                stats.CpuAvg = cpuSamples.Average(delegate(PerfSample sample) { return sample.CpuPercent; });
                stats.CpuLe60Percent = cpuSamples.Count(delegate(PerfSample sample) { return sample.CpuPercent <= 60; }) * 100.0 / cpuSamples.Count;
                stats.CpuLe80Percent = cpuSamples.Count(delegate(PerfSample sample) { return sample.CpuPercent <= 80; }) * 100.0 / cpuSamples.Count;
            }
            return stats;
        }

        private string MemoryMetricForExport()
        {
            string metric = MetricStatistics.PreferredMemoryMetric(_samples);
            if (!string.IsNullOrWhiteSpace(metric)) return MemoryMetricDisplayName(metric);
            return DeviceLookupService.IsAndroid(_selectedDevice) ? "PSS" : "Footprint";
        }

        private string FrameMetricQualityForExport()
        {
            List<PerfSample> frames = _samples.Where(delegate(PerfSample sample) { return sample.HasFps && sample.FpsUpdated; }).ToList();
            if (frames.Count == 0) return "Unavailable";
            List<PerfSample> jankFrames = frames.Where(MetricStatistics.IsReliableJankSample).ToList();
            bool anyApproximate = frames.Any(delegate(PerfSample sample) { return sample.ApproximateFrameMetrics || sample.SourceDegraded || !sample.OrderedFrames; });
            string sequenceSuffix = frames.Any(delegate(PerfSample sample) { return sample.FrameSourceSequenceDiscontinuity; })
                ? "; source sequence discontinuity"
                : "";
            if (jankFrames.Count == 0)
            {
                if (frames.Any(delegate(PerfSample sample) { return sample.HasFrameTime; }))
                {
                    return "Approximate FPS/FrameTime; Jank unavailable" + sequenceSuffix;
                }
                return "FPS-only" + sequenceSuffix;
            }
            bool anyExact = jankFrames.Any(delegate(PerfSample sample) { return sample.OrderedFrames && !sample.ApproximateFrameMetrics && !sample.SourceDegraded; });
            if (anyApproximate && anyExact) return "Mixed exact/approximate" + sequenceSuffix;
            return (anyApproximate ? "Approximate" : "Exact ordered") + sequenceSuffix;
        }

        private static string MemoryMetricDisplayName(string metric)
        {
            string value = (metric ?? "").Trim().ToLowerInvariant();
            if (value == "physical_footprint" || value == "footprint") return "Footprint";
            if (value == "pss") return "PSS";
            if (value == "rss") return "RSS";
            return string.IsNullOrWhiteSpace(value) ? "-" : metric;
        }

        private string DeviceNameForExport()
        {
            DeviceInfo device = _selectedDevice;
            if (device == null) return "-";
            if (!string.IsNullOrWhiteSpace(device.MarketName)) return device.MarketName;
            if (!string.IsNullOrWhiteSpace(device.Name)) return device.Name;
            return DeviceLookupService.IsAndroid(device) ? "Android Device" : "iOS Device";
        }

        private string DeviceOsForExport()
        {
            DeviceInfo device = _selectedDevice;
            string platform = DevicePlatformName(device);
            return device == null || string.IsNullOrWhiteSpace(device.ProductVersion) ? platform : platform + " " + device.ProductVersion;
        }

        private static string DevicePlatformName(DeviceInfo device)
        {
            return DeviceLookupService.IsAndroid(device) ? "Android" : "iOS";
        }

        private static string DeviceDetailsText(DeviceInfo device)
        {
            StringBuilder details = new StringBuilder();
            AppendDeviceDetail(details, "CPU信息", device == null ? "" : device.CpuInfo);
            AppendDeviceDetail(details, "GPU信息", device == null ? "" : device.GpuInfo);
            AppendDeviceDetail(details, "系统信息", DeviceOsText(device));
            AppendDeviceDetail(details, "分辨率", device == null ? "" : device.Resolution);
            AppendDeviceDetail(details, "平台", DevicePlatformName(device));
            AppendDeviceDetail(details, "连接方式", device == null ? "-" : device.ConnType);
            return details.ToString().TrimEnd();
        }

        private static string DeviceOsText(DeviceInfo device)
        {
            string platform = DevicePlatformName(device);
            return device == null || string.IsNullOrWhiteSpace(device.ProductVersion) ? platform : platform + " " + device.ProductVersion;
        }

        private static void AppendDeviceDetail(StringBuilder details, string label, string value)
        {
            if (details == null) return;
            string text = string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
            details.Append(label).Append("：").Append(text).Append('\n');
        }

        private string DeviceCpuForExport()
        {
            return _selectedDevice == null || string.IsNullOrWhiteSpace(_selectedDevice.CpuInfo) ? "-" : _selectedDevice.CpuInfo;
        }

        private string DeviceGpuForExport()
        {
            return _selectedDevice == null || string.IsNullOrWhiteSpace(_selectedDevice.GpuInfo) ? "-" : _selectedDevice.GpuInfo;
        }

        private string DeviceResolutionForExport()
        {
            return _selectedDevice == null || string.IsNullOrWhiteSpace(_selectedDevice.Resolution) ? "-" : _selectedDevice.Resolution;
        }

        private string AppNameForExport()
        {
            if (_selectedApp != null)
            {
                return string.IsNullOrWhiteSpace(_selectedApp.Name) ? _selectedApp.BundleId : _selectedApp.Name + " / " + _selectedApp.BundleId;
            }
            return string.IsNullOrWhiteSpace(_selectedBundleId) ? "-" : _selectedBundleId;
        }

        private string ProcessNameForExport()
        {
            if (_selectedProcess == null) return "-";
            return _selectedProcess.Name + " / pid " + _selectedProcess.Pid;
        }

        private string ProcessStartIdentityForExport()
        {
            if (_selectedProcess == null) return "-";
            long identity = _selectedProcess.AndroidStartTimeTicks > 0
                ? _selectedProcess.AndroidStartTimeTicks
                : _selectedProcess.StartAbsTime;
            return identity > 0 ? identity.ToString(CultureInfo.InvariantCulture) : "-";
        }

        private string TargetPidForExport()
        {
            if (_selectedProcess != null && _selectedProcess.Pid > 0) return _selectedProcess.Pid.ToString(CultureInfo.InvariantCulture);
            PerfSample sample = _samples.FirstOrDefault();
            return sample == null ? "-" : sample.TargetPid.ToString(CultureInfo.InvariantCulture);
        }

        private static string Num(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "-";
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string CsvLine(params string[] values)
        {
            return string.Join(",", values.Select(CsvCell));
        }

        private static string CsvCell(string value)
        {
            value = value ?? "";
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = TextSecondary,
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 4)
            };
        }

        private static TextBlock HintText(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Brush(132, 135, 154),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
        }

        private static TextBox DarkTextBox(string tooltip)
        {
            return new TextBox
            {
                ToolTip = tooltip,
                Background = Brush(49, 49, 63),
                Foreground = TextPrimary,
                BorderBrush = Brush(63, 64, 80),
                SelectionBrush = AccentBlue,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 3)
            };
        }

        private static Grid SearchBoxFrame(TextBox input, string placeholder)
        {
            Grid frame = new Grid();
            frame.Children.Add(input);
            TextBlock icon = new TextBlock
            {
                Text = "⌕",
                Foreground = Brush(138, 141, 158),
                FontSize = 20,
                FontWeight = FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, -1, 0, 0),
                IsHitTestVisible = false
            };
            TextBlock hint = new TextBlock
            {
                Text = placeholder,
                Foreground = Brush(138, 141, 158),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(38, 0, 0, 0),
                IsHitTestVisible = false
            };
            hint.SetBinding(UIElement.VisibilityProperty, new Binding("Text")
            {
                Source = input,
                Converter = new EmptyTextVisibilityConverter()
            });
            frame.Children.Add(icon);
            frame.Children.Add(hint);
            return frame;
        }

        private static Style DarkScrollViewerStyle()
        {
            Style style = new Style(typeof(ScrollViewer));
            style.Resources.Add(typeof(ScrollBar), DarkScrollBarStyle());
            return style;
        }

        private static Style DarkScrollBarStyle()
        {
            Style style = new Style(typeof(ScrollBar));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(13, 14, 21)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(91, 92, 96)));
            style.Setters.Add(new Setter(ScrollBar.WidthProperty, 14.0));
            style.Setters.Add(new Setter(ScrollBar.MinWidthProperty, 14.0));
            style.Setters.Add(new Setter(Control.TemplateProperty, DarkScrollBarTemplate()));
            return style;
        }

        private static Style DarkHorizontalScrollBarStyle()
        {
            Style style = new Style(typeof(ScrollBar));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(13, 14, 21)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(91, 92, 96)));
            style.Setters.Add(new Setter(ScrollBar.HeightProperty, 12.0));
            style.Setters.Add(new Setter(ScrollBar.MinHeightProperty, 12.0));
            style.Setters.Add(new Setter(Control.TemplateProperty, DarkHorizontalScrollBarTemplate()));
            return style;
        }

        private static ControlTemplate DarkScrollBarTemplate()
        {
            const string xaml =
@"<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                  xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                  TargetType=""ScrollBar"">
    <Grid Background=""#0D0E15"">
        <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
            <Track.DecreaseRepeatButton>
                <RepeatButton Command=""ScrollBar.PageUpCommand"" Opacity=""0"" />
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
                <Thumb>
                    <Thumb.Template>
                        <ControlTemplate TargetType=""Thumb"">
                            <Border Background=""#5B5C60"" CornerRadius=""5"" Margin=""3,2"" />
                        </ControlTemplate>
                    </Thumb.Template>
                </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
                <RepeatButton Command=""ScrollBar.PageDownCommand"" Opacity=""0"" />
            </Track.IncreaseRepeatButton>
        </Track>
    </Grid>
</ControlTemplate>";
            return (ControlTemplate)XamlReader.Parse(xaml);
        }

        private static ControlTemplate DarkHorizontalScrollBarTemplate()
        {
            const string xaml =
@"<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                  xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                  TargetType=""ScrollBar"">
    <Grid Background=""#0D0E15"" Height=""12"">
        <Track x:Name=""PART_Track"">
            <Track.DecreaseRepeatButton>
                <RepeatButton Command=""ScrollBar.PageLeftCommand"" Opacity=""0"" />
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
                <Thumb>
                    <Thumb.Template>
                        <ControlTemplate TargetType=""Thumb"">
                            <Border Background=""#5B5C60"" CornerRadius=""4"" Margin=""2,3"" />
                        </ControlTemplate>
                    </Thumb.Template>
                </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
                <RepeatButton Command=""ScrollBar.PageRightCommand"" Opacity=""0"" />
            </Track.IncreaseRepeatButton>
        </Track>
    </Grid>
</ControlTemplate>";
            return (ControlTemplate)XamlReader.Parse(xaml);
        }

        private static ComboBox DarkCombo()
        {
            ComboBox combo = new ComboBox
            {
                Background = Brush(49, 49, 63),
                Foreground = TextPrimary,
                BorderBrush = Brush(49, 49, 63),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 3),
                MinHeight = 38,
                ItemContainerStyle = DarkComboItemStyle()
            };
            combo.Template = DarkComboTemplate();
            combo.Resources.Add(SystemColors.WindowBrushKey, Brush(23, 24, 33));
            combo.Resources.Add(SystemColors.WindowTextBrushKey, TextPrimary);
            combo.Resources.Add(SystemColors.HighlightBrushKey, Brush(24, 118, 220));
            combo.Resources.Add(SystemColors.HighlightTextBrushKey, Brushes.White);
            return combo;
        }

        private static ControlTemplate DarkComboTemplate()
        {
            const string xaml =
@"<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                  xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                  TargetType=""ComboBox"">
    <Grid>
        <ToggleButton x:Name=""ToggleButton""
                      Focusable=""False""
                      IsChecked=""{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}""
                      ClickMode=""Press""
                      Background=""#31313F""
                      BorderBrush=""#31313F""
                      BorderThickness=""1"">
            <ToggleButton.Template>
                <ControlTemplate TargetType=""ToggleButton"">
                    <Border x:Name=""ToggleRoot""
                            Background=""{TemplateBinding Background}""
                            BorderBrush=""{TemplateBinding BorderBrush}""
                            BorderThickness=""{TemplateBinding BorderThickness}"">
                        <ContentPresenter Content=""{TemplateBinding Content}""
                                          HorizontalAlignment=""Stretch""
                                          VerticalAlignment=""Stretch"" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""ToggleRoot"" Property=""Background"" Value=""#393A4A"" />
                            <Setter TargetName=""ToggleRoot"" Property=""BorderBrush"" Value=""#4A4B5E"" />
                        </Trigger>
                        <Trigger Property=""IsPressed"" Value=""True"">
                            <Setter TargetName=""ToggleRoot"" Property=""Background"" Value=""#303141"" />
                            <Setter TargetName=""ToggleRoot"" Property=""BorderBrush"" Value=""#50536A"" />
                        </Trigger>
                        <Trigger Property=""IsChecked"" Value=""True"">
                            <Setter TargetName=""ToggleRoot"" Property=""Background"" Value=""#31313F"" />
                            <Setter TargetName=""ToggleRoot"" Property=""BorderBrush"" Value=""#4A4B5E"" />
                        </Trigger>
                        <Trigger Property=""IsEnabled"" Value=""False"">
                            <Setter TargetName=""ToggleRoot"" Property=""Opacity"" Value=""0.48"" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </ToggleButton.Template>
            <Grid>
                <ContentPresenter x:Name=""ContentSite""
                                  IsHitTestVisible=""False""
                                  Content=""{TemplateBinding SelectionBoxItem}""
                                  ContentTemplate=""{TemplateBinding SelectionBoxItemTemplate}""
                                  ContentStringFormat=""{TemplateBinding SelectionBoxItemStringFormat}""
                                  Margin=""8,0,28,0""
                                  VerticalAlignment=""Center""
                                  HorizontalAlignment=""Left"" />
                <Path HorizontalAlignment=""Right""
                      Margin=""0,0,10,0""
                      VerticalAlignment=""Center""
                      Data=""M 0 0 L 4 4 L 8 0 Z""
                      Fill=""#8B8EA0"" />
            </Grid>
        </ToggleButton>
        <Popup x:Name=""PART_Popup""
               AllowsTransparency=""True""
               Focusable=""False""
               IsOpen=""{TemplateBinding IsDropDownOpen}""
               Placement=""Bottom""
               PopupAnimation=""Slide"">
            <Border Background=""#31313F""
                    BorderBrush=""#4A4B5E""
                    BorderThickness=""1""
                    MinWidth=""{TemplateBinding ActualWidth}""
                    MaxHeight=""260"">
                <ScrollViewer CanContentScroll=""True"">
                    <ItemsPresenter />
                </ScrollViewer>
            </Border>
        </Popup>
    </Grid>
</ControlTemplate>";
            return (ControlTemplate)XamlReader.Parse(xaml);
        }

        private static Style DarkComboItemStyle()
        {
            Style style = new Style(typeof(ComboBoxItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(23, 24, 33)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));

            Trigger selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush(24, 118, 220)));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Triggers.Add(selected);

            Trigger hover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(54, 58, 76)));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Triggers.Add(hover);
            return style;
        }

        private static DataTemplate DarkTextItemTemplate(string path)
        {
            FrameworkElementFactory text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.ForegroundProperty, TextPrimary);
            text.SetValue(TextBlock.FontSizeProperty, 13.0);
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetBinding(TextBlock.TextProperty, new Binding(path));
            return new DataTemplate { VisualTree = text };
        }

        private static Border SectionPanel(string title)
        {
            StackPanel content = new StackPanel { Margin = new Thickness(12) };
            content.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = TextPrimary,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 6)
            });
            return new Border
            {
                Child = content,
                Background = PanelBackground,
                BorderBrush = BorderBrushDark,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 12)
            };
        }

        private static Border MetricCard(string name, string subtitle, Brush accent, CheckBox checkBox, bool enabled)
        {
            Grid grid = new Grid { Margin = new Thickness(14, 12, 14, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            checkBox.Foreground = TextPrimary;
            checkBox.VerticalAlignment = VerticalAlignment.Center;
            checkBox.HorizontalAlignment = HorizontalAlignment.Center;
            checkBox.IsEnabled = enabled;
            grid.Children.Add(checkBox);

            Border icon = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(4),
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "▣",
                    Foreground = accent,
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);

            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, Foreground = enabled ? TextPrimary : Brush(112, 114, 126), FontSize = 14, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = subtitle, Foreground = enabled ? TextSecondary : Brush(92, 94, 106), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);

            TextBlock visible = new TextBlock
            {
                Text = enabled ? "◉" : "◌",
                Foreground = enabled ? TextSecondary : Brush(92, 94, 106),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(visible, 3);
            grid.Children.Add(visible);

            if (name == "Display FrameTime")
            {
                grid.ToolTip = "FrameTime 是相邻显示帧之间的时间间隔。界面显示当前采样窗口最大值，用于保留最严重的一次长帧；数值越大越容易出现卡顿。";
            }

            return new Border
            {
                Background = enabled ? CardBackground : Brush(35, 36, 48),
                CornerRadius = new CornerRadius(7),
                Child = grid,
                Margin = new Thickness(0, 0, 0, 10),
                Opacity = enabled ? 1.0 : 0.68
            };
        }

        private static Button ToolbarButton(string text, RoutedEventHandler handler)
        {
            Button button = new Button
            {
                Content = ToolbarButtonContent(text),
                Foreground = TextPrimary,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 8, 13, 8),
                Margin = new Thickness(5, 0, 5, 0),
                MinWidth = text == "×" ? 42 : 84,
                Height = 40,
                Cursor = Cursors.Hand,
                Style = DarkButtonStyle()
            };
            button.Click += handler;
            return button;
        }

        private static Border ToolbarZone(UIElement child, HorizontalAlignment contentAlignment)
        {
            if (child is FrameworkElement element)
            {
                element.HorizontalAlignment = contentAlignment;
            }
            return new Border
            {
                Child = child,
                Background = Brush(24, 24, 32),
                BorderThickness = new Thickness(0)
            };
        }

        private static Border ToolbarSeparator(double left, double top, double right, double bottom)
        {
            return new Border
            {
                BorderBrush = Brush(37, 39, 52),
                BorderThickness = new Thickness(left, top, right, bottom),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
        }

        private static object ToolbarButtonContent(string text)
        {
            string icon = null;
            if (text == "打开") icon = "\uE838";
            else if (text == "保存") icon = "\uE74E";
            else if (text == "导出") icon = "\uEDE4";
            else if (text == "帮助") icon = "\uE897";
            if (icon == null) return text;
            return IconToolbarContent(icon, text, TextSecondary, TextPrimary, true);
        }

        private static Button WindowButton(string text, RoutedEventHandler handler)
        {
            Button button = new Button
            {
                Content = text,
                Foreground = TextPrimary,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 46,
                FontSize = 16,
                Style = DarkButtonStyle()
            };
            button.Click += handler;
            return button;
        }

        private static Button PrimaryToolbarButton(string text, RoutedEventHandler handler)
        {
            Button button = new Button
            {
                Content = text,
                Foreground = Brushes.White,
                Background = AccentBlue,
                BorderBrush = AccentBlue,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(6, 0, 6, 0),
                MinWidth = 100,
                Style = DarkButtonStyle()
            };
            button.Click += handler;
            return button;
        }

        private static Button CaptureToolbarButton()
        {
            Button button = IconToolbarButton("▶", "开始采集", true);
            button.Width = 150;
            return button;
        }

        private static Button IconToolbarButton(string icon, string text, bool active)
        {
            Button button = new Button
            {
                Content = IconToolbarContent(icon, text, active ? AccentBlue : Brush(101, 104, 120), active ? TextPrimary : Brush(126, 128, 145)),
                Foreground = active ? TextPrimary : Brush(126, 128, 145),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(6, 0, 6, 0),
                MinWidth = 132,
                Height = 44,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Style = DarkButtonStyle()
            };
            return button;
        }

        private static Style DarkButtonStyle()
        {
            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.TemplateProperty, DarkButtonTemplate()));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            return style;
        }

        private static ControlTemplate DarkButtonTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "RootBorder";
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(ContentPresenter.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetBinding(ContentPresenter.VerticalAlignmentProperty, new Binding("VerticalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            border.AppendChild(presenter);
            ControlTemplate template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush(36, 37, 50), "RootBorder"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, Brush(49, 51, 66), "RootBorder"));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush(48, 50, 64), "RootBorder"));
            pressed.Setters.Add(new Setter(Border.BorderBrushProperty, Brush(68, 72, 92), "RootBorder"));
            template.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "RootBorder"));
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, Brush(32, 33, 44), "RootBorder"));
            disabled.Setters.Add(new Setter(Border.BorderBrushProperty, Brush(42, 44, 56), "RootBorder"));
            template.Triggers.Add(disabled);
            return template;
        }

        private static StackPanel IconToolbarContent(string icon, string text, Brush iconBrush, Brush textBrush, bool useSymbolFont = false)
        {
            StackPanel row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock iconBlock = new TextBlock
            {
                Text = icon,
                Foreground = iconBrush,
                FontSize = useSymbolFont ? 19 : 22,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, -1, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (useSymbolFont) iconBlock.FontFamily = ToolbarSymbolFont;
            row.Children.Add(iconBlock);
            row.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = textBrush,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            return row;
        }

        private void UpdateCaptureButton()
        {
            if (_startButton == null) return;
            string icon = _isCapturing ? "■" : "▶";
            string text = _isStartingCapture ? "正在启动" : (_isCapturing ? "停止采集" : "开始采集");
            Brush iconBrush = _isCapturing ? AccentPink : AccentBlue;
            _startButton.Content = IconToolbarContent(icon, text, iconBrush, TextPrimary);
            _startButton.Background = _isCapturing ? Brush(35, 36, 50) : Brushes.Transparent;
            _startButton.BorderBrush = _isCapturing ? Brush(43, 45, 60) : Brushes.Transparent;
            _startButton.Foreground = TextPrimary;
            _startButton.IsEnabled = !_isStartingCapture;
            _startButton.Opacity = _isStartingCapture ? 0.7 : 1.0;
            if (_selectTargetButton != null)
            {
                SetButtonEnabled(_selectTargetButton, !_isCapturing && !_isStartingCapture);
            }
            bool metricSelectionEnabled = !_isCapturing && !_isStartingCapture;
            if (_fpsMetricCheck != null) _fpsMetricCheck.IsEnabled = metricSelectionEnabled;
            if (_frameMetricCheck != null) _frameMetricCheck.IsEnabled = metricSelectionEnabled;
            if (_memoryMetricCheck != null) _memoryMetricCheck.IsEnabled = metricSelectionEnabled;
            if (_cpuMetricCheck != null) _cpuMetricCheck.IsEnabled = metricSelectionEnabled;
            if (_temperatureMetricCheck != null) _temperatureMetricCheck.IsEnabled = metricSelectionEnabled;
            if (_thermalStateMetricCheck != null) _thermalStateMetricCheck.IsEnabled = metricSelectionEnabled && !DeviceLookupService.IsAndroid(_selectedDevice);
        }

        private static void SetButtonEnabled(Button button, bool enabled)
        {
            if (button == null) return;
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1.0 : 0.45;
            if (!enabled)
            {
                button.Background = Brush(39, 41, 54);
                button.Foreground = Brush(154, 158, 178);
                button.BorderBrush = BorderBrushDark;
            }
            else if (Convert.ToString(button.Content) == "开始采集")
            {
                button.Background = AccentBlue;
                button.Foreground = Brushes.White;
                button.BorderBrush = AccentBlue;
            }
            else if (Convert.ToString(button.Content) == "选择设备及应用")
            {
                button.Background = AccentBlue;
                button.Foreground = Brushes.White;
                button.BorderBrush = AccentBlue;
            }
            else
            {
                button.Background = Brushes.Transparent;
                button.Foreground = TextPrimary;
                button.BorderBrush = Brushes.Transparent;
            }
        }

        private static Button SmallToolButton(string text)
        {
            return new Button
            {
                Content = text,
                Foreground = TextSecondary,
                Background = Brushes.Transparent,
                BorderBrush = BorderBrushDark,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Style = DarkButtonStyle()
            };
        }

        private static Button DialogButton(string text, RoutedEventHandler handler)
        {
            Button button = new Button
            {
                Content = text,
                Foreground = TextPrimary,
                Background = Brushes.Transparent,
                BorderBrush = Brush(68, 69, 84),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(6, 0, 6, 0),
                Width = 100,
                Height = 40,
                MinWidth = 92,
                VerticalAlignment = VerticalAlignment.Center,
                Style = DarkButtonStyle()
            };
            button.Click += handler;
            return button;
        }

        private static Button IconOnlyDialogButton(string text, RoutedEventHandler handler)
        {
            Button button = new Button
            {
                Content = text,
                Foreground = Brush(176, 178, 190),
                Background = Brush(49, 49, 63),
                BorderBrush = Brush(49, 49, 63),
                BorderThickness = new Thickness(1),
                Width = 38,
                Height = 38,
                Padding = new Thickness(0),
                Margin = new Thickness(10, 0, 0, 0),
                FontSize = 23,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = DarkButtonStyle()
            };
            button.Click += handler;
            return button;
        }

        private static Button DialogCloseButton(RoutedEventHandler handler)
        {
            Button button = new Button
            {
                Content = "×",
                Foreground = TextPrimary,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 42,
                Height = 42,
                Padding = new Thickness(0),
                FontSize = 22,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = DarkButtonStyle()
            };
            button.Click += handler;
            return button;
        }

        private static TextBlock DialogLabel(string text, int row)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                Foreground = TextPrimary,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            return label;
        }

        private static Grid ChartRow(string title, string[] legends, ChartCanvas chart, Brush[] colors)
        {
            Panel legendPanel;
            return ChartRow(title, legends, chart, colors, out legendPanel, false);
        }

        private static Grid ChartRow(string title, string[] legends, ChartCanvas chart, Brush[] colors, out Panel legendPanel)
        {
            return ChartRow(title, legends, chart, colors, out legendPanel, true);
        }

        private static Grid ChartRow(string title, string[] legends, ChartCanvas chart, Brush[] colors, out Panel legendPanel, bool compactLegend)
        {
            Grid row = new Grid
            {
                Height = ChartRowHeight,
                MinHeight = ChartRowHeight,
                Margin = new Thickness(0, 0, 0, 8)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border left = new Border
            {
                Background = CardActiveBackground,
                BorderBrush = BorderBrushDark,
                BorderThickness = new Thickness(0, 1, 1, 1)
            };
            StackPanel label = new StackPanel { Margin = new Thickness(12, 8, 8, 8) };
            label.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = TextPrimary,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            legendPanel = compactLegend
                ? (Panel)new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    ItemWidth = 64,
                    ItemHeight = 19
                }
                : new StackPanel();
            for (int i = 0; i < legends.Length; i++)
            {
                ChartCanvas interactiveChart = legends.Length > 1 ? chart : null;
                legendPanel.Children.Add(LegendItem(legends[i], colors[Math.Min(i, colors.Length - 1)], compactLegend, interactiveChart));
            }
            label.Children.Add(legendPanel);
            left.Child = label;
            row.Children.Add(left);

            Border chartHost = new Border
            {
                Background = Brush(34, 35, 48),
                BorderBrush = BorderBrushDark,
                BorderThickness = new Thickness(0, 1, 0, 1),
                Child = chart
            };
            Grid.SetColumn(chartHost, 1);
            row.Children.Add(chartHost);
            return row;
        }

        private static UIElement LegendItem(string text, Brush color, bool compact = false, ChartCanvas interactiveChart = null, string seriesName = null)
        {
            Border swatch = new Border
            {
                Width = compact ? 9 : 11,
                Height = compact ? 9 : 11,
                Margin = compact ? new Thickness(0, 3, 5, 0) : new Thickness(0, 2, 8, 0)
            };
            TextBlock legendText = new TextBlock
            {
                Text = text,
                FontSize = compact ? 12 : 13
            };
            StackPanel row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = compact ? new Thickness(0, 1, 0, 1) : new Thickness(0, 3, 0, 3),
                Background = Brushes.Transparent,
                Cursor = interactiveChart == null ? Cursors.Arrow : Cursors.Hand
            };
            row.Children.Add(swatch);
            row.Children.Add(legendText);

            Action updateState = delegate
            {
                string key = string.IsNullOrWhiteSpace(seriesName) ? text : seriesName;
                bool visible = interactiveChart == null || interactiveChart.IsSeriesVisible(key);
                swatch.Background = visible ? color : DisabledLegendBrush;
                legendText.Foreground = visible ? TextSecondary : DisabledLegendBrush;
                row.Opacity = visible ? 1.0 : 0.72;
            };
            updateState();

            if (interactiveChart != null)
            {
                row.ToolTip = "点击隐藏或显示 " + text + " 曲线";
                row.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
                {
                    interactiveChart.ToggleSeries(string.IsNullOrWhiteSpace(seriesName) ? text : seriesName);
                    updateState();
                    e.Handled = true;
                };
            }
            else if (text == "FrameTime")
            {
                row.ToolTip = "当前采样窗口实际观测到的最大帧间隔。";
            }
            else if (text == "P95")
            {
                row.ToolTip = "P95：当前统计窗口内 95% 的帧间隔不超过该值。";
            }
            else if (text == "Max")
            {
                row.ToolTip = "Max：当前统计窗口内实际观测到的最大帧间隔。";
            }
            return row;
        }

        private UIElement CreateScreenshotItem(ScreenshotInfo shot)
        {
            Border card = new Border
            {
                Width = 76,
                Height = 134,
                Padding = new Thickness(4),
                Margin = new Thickness(3, 0, 7, 0),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                Tag = shot,
                Cursor = Cursors.Hand
            };
            StackPanel root = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            Image image = new Image
            {
                Width = 64,
                Height = 100,
                Stretch = Stretch.Uniform,
                Source = LoadImage(shot.Path)
            };
            TextBlock time = new TextBlock
            {
                Text = FormatElapsed(shot.ElapsedSec),
                Foreground = TextSecondary,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.None,
                Margin = new Thickness(0, 2, 0, 0)
            };
            root.Children.Add(image);
            root.Children.Add(time);
            card.Child = root;
            card.MouseEnter += delegate
            {
                if (card.BorderBrush != AccentCyan) card.Background = Brush(34, 38, 50);
            };
            card.MouseLeave += delegate
            {
                if (card.BorderBrush != AccentCyan) card.Background = Brushes.Transparent;
            };
            card.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ClickCount < 2) return;
                _suppressShotClick = true;
                _draggingShots = false;
                _shotDragMoved = false;
                ShowScreenshotViewer(shot);
                e.Handled = true;
            };
            card.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if (_suppressShotClick)
                {
                    _suppressShotClick = false;
                    e.Handled = true;
                    return;
                }
                if (_shotDragMoved) return;
                SelectTime(shot.ElapsedSec, false);
                e.Handled = true;
            };
            return card;
        }

        private static ScreenshotInfo FindShotFromHit(DependencyObject hit)
        {
            DependencyObject current = hit;
            while (current != null)
            {
                FrameworkElement element = current as FrameworkElement;
                if (element != null)
                {
                    ScreenshotInfo shot = element.Tag as ScreenshotInfo;
                    if (shot != null) return shot;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void ShowScreenshotViewer(ScreenshotInfo shot)
        {
            if (shot == null || !File.Exists(shot.Path)) return;
            List<ScreenshotInfo> viewerShots = _shots
                .Where(delegate(ScreenshotInfo item) { return item != null && File.Exists(item.Path); })
                .OrderBy(delegate(ScreenshotInfo item) { return item.ElapsedSec; })
                .ThenBy(delegate(ScreenshotInfo item) { return item.Timestamp; })
                .ToList();
            int currentIndex = viewerShots.FindIndex(delegate(ScreenshotInfo item) { return ReferenceEquals(item, shot); });
            if (currentIndex < 0)
            {
                currentIndex = viewerShots.FindIndex(delegate(ScreenshotInfo item)
                {
                    return string.Equals(item.Path, shot.Path, StringComparison.OrdinalIgnoreCase);
                });
            }
            if (currentIndex < 0)
            {
                viewerShots.Add(shot);
                currentIndex = viewerShots.Count - 1;
            }
            BitmapImage bitmap = LoadImageOriginal(shot.Path);
            double screenMaxWidth = Math.Max(420, SystemParameters.WorkArea.Width * 0.82);
            double screenMaxHeight = Math.Max(520, SystemParameters.WorkArea.Height * 0.86);
            double imageWidth = Math.Max(1, bitmap.PixelWidth);
            double imageHeight = Math.Max(1, bitmap.PixelHeight);
            double toolbarHeight = 48;
            double chromeWidth = 42;
            double chromeHeight = 96;
            double fitScale = Math.Min(1.0, Math.Min((screenMaxWidth - chromeWidth) / imageWidth, (screenMaxHeight - chromeHeight) / imageHeight));
            if (double.IsNaN(fitScale) || fitScale <= 0) fitScale = 0.45;
            double viewerWidth = Math.Min(screenMaxWidth, Math.Max(420, imageWidth * fitScale + chromeWidth));
            double viewerHeight = Math.Min(screenMaxHeight, Math.Max(520, imageHeight * fitScale + chromeHeight));
            double zoom = fitScale;
            Window viewer = new Window
            {
                Title = "截图 " + FormatElapsed(shot.ElapsedSec),
                Width = viewerWidth,
                Height = viewerHeight,
                MinWidth = 360,
                MinHeight = 420,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                Background = AppBackground,
                FontFamily = FontFamily
            };
            Grid root = new Grid { Background = AppBackground };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(toolbarHeight) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid toolbar = new Grid { Margin = new Thickness(12, 0, 12, 0) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.MouseLeftButtonDown += delegate
            {
                try { viewer.DragMove(); } catch { }
            };
            TextBlock title = new TextBlock
            {
                Foreground = TextPrimary,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            toolbar.Children.Add(title);
            StackPanel controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            ScrollViewer scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Style = DarkScrollViewerStyle()
            };
            Image image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(10),
                LayoutTransform = new ScaleTransform(zoom, zoom)
            };
            Action<double> setZoom = delegate(double nextZoom)
            {
                zoom = Math.Max(0.1, Math.Min(4.0, nextZoom));
                image.LayoutTransform = new ScaleTransform(zoom, zoom);
            };
            Button previousButton = null;
            Button nextButton = null;
            Action updateNavigationState = delegate
            {
                ScreenshotInfo current = viewerShots[currentIndex];
                string position = string.Format(CultureInfo.InvariantCulture, "{0}/{1}", currentIndex + 1, viewerShots.Count);
                title.Text = FormatElapsed(current.ElapsedSec) + "  " + System.IO.Path.GetFileName(current.Path) + "  " + position;
                viewer.Title = "截图 " + FormatElapsed(current.ElapsedSec) + "  " + position;
                if (previousButton != null) previousButton.IsEnabled = currentIndex > 0;
                if (nextButton != null) nextButton.IsEnabled = currentIndex < viewerShots.Count - 1;
            };
            Action<int> showAt = delegate(int nextIndex)
            {
                if (nextIndex < 0 || nextIndex >= viewerShots.Count || nextIndex == currentIndex) return;
                ScreenshotInfo nextShot = viewerShots[nextIndex];
                if (!File.Exists(nextShot.Path)) return;
                BitmapImage nextBitmap = LoadImageOriginal(nextShot.Path);
                currentIndex = nextIndex;
                image.Source = nextBitmap;
                double availableWidth = scroll.ViewportWidth > 40 ? scroll.ViewportWidth - 20 : screenMaxWidth - chromeWidth;
                double availableHeight = scroll.ViewportHeight > 40 ? scroll.ViewportHeight - 20 : screenMaxHeight - chromeHeight;
                fitScale = Math.Min(1.0, Math.Min(availableWidth / Math.Max(1, nextBitmap.PixelWidth), availableHeight / Math.Max(1, nextBitmap.PixelHeight)));
                if (double.IsNaN(fitScale) || fitScale <= 0) fitScale = 0.45;
                setZoom(fitScale);
                scroll.ScrollToHorizontalOffset(0);
                scroll.ScrollToVerticalOffset(0);
                updateNavigationState();
            };
            previousButton = ViewerButton("\u2190", delegate { showAt(currentIndex - 1); });
            previousButton.ToolTip = "上一张（左方向键）";
            nextButton = ViewerButton("\u2192", delegate { showAt(currentIndex + 1); });
            nextButton.ToolTip = "下一张（右方向键）";
            controls.Children.Add(previousButton);
            controls.Children.Add(nextButton);
            controls.Children.Add(ViewerButton("-", delegate { setZoom(zoom / 1.2); }));
            controls.Children.Add(ViewerButton("+", delegate { setZoom(zoom * 1.2); }));
            controls.Children.Add(ViewerButton("适应", delegate { setZoom(fitScale); }));
            controls.Children.Add(ViewerButton("100%", delegate { setZoom(1.0); }));
            controls.Children.Add(ViewerButton("×", delegate { viewer.Close(); }));
            Grid.SetColumn(controls, 1);
            toolbar.Children.Add(controls);
            root.Children.Add(toolbar);
            scroll.Content = image;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            viewer.Content = root;
            viewer.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Left)
                {
                    showAt(currentIndex - 1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right)
                {
                    showAt(currentIndex + 1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    viewer.Close();
                    e.Handled = true;
                }
            };
            updateNavigationState();
            viewer.Show();
        }

        private static Button ViewerButton(string text, RoutedEventHandler handler)
        {
            Button button = new Button
            {
                Content = text,
                Foreground = TextPrimary,
                Background = Brush(28, 30, 42),
                BorderBrush = BorderBrushDark,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(6, 0, 0, 0),
                MinWidth = 48,
                Cursor = Cursors.Hand,
                Style = DarkButtonStyle()
            };
            button.Click += handler;
            return button;
        }

        private static BitmapImage LoadImage(string path)
        {
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 160;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapImage LoadPackImage(string assetPath)
        {
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri("pack://application:,,,/" + assetPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapImage LoadImageOriginal(string path)
        {
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static string ProcessBundleFromName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return "";
            int colon = processName.IndexOf(':');
            return colon > 0 ? processName.Substring(0, colon) : processName;
        }

        private static string AppIconGlyph(string key)
        {
            string icon = (key ?? "").ToLowerInvariant();
            if (icon == "wechat") return "微";
            if (icon == "tencent") return "T";
            if (icon == "android") return "A";
            return "•";
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private sealed class ExportStats
        {
            public bool HasFpsStats;
            public double FpsAvg;
            public double FpsGe18Percent;
            public double FpsGe25Percent;
            public double FpsVariance;
            public double FpsStdDev;
            public double FpsDropPerHour;
            public double FpsMinimum;
            public double FpsMedian;
            public double FpsMedianRangePercent;
            public string FpsSourceTier;
            public int FpsSampleCount;
            public int FpsExcludedSampleCount;
            public double FpsObservationSec;
            public bool HasJankStats;
            public double JankPer10Min;
            public double BigJankPer10Min;
            public int JankSampleCount;
            public int JankExcludedSampleCount;
            public double JankObservationSec;
            public bool HasStutterStats;
            public double StutterPercent;
            public bool HasMemoryStats;
            public double MemoryAvgMb;
            public double MemoryPeakMb;
            public bool HasCpuStats;
            public double CpuAvg;
            public double CpuLe60Percent;
            public double CpuLe80Percent;
        }

        private sealed class SessionDocument
        {
            public SessionDocument()
            {
                Format = "";
                AppVersion = "";
                SelectedBundleId = "";
                Samples = new List<PerfSample>();
                Screenshots = new List<SessionScreenshot>();
            }

            public string Format { get; set; }
            public int Version { get; set; }
            public string AppVersion { get; set; }
            public DateTime SavedAt { get; set; }
            public DateTime StartedAt { get; set; }
            public DeviceInfo Device { get; set; }
            public AppInfo App { get; set; }
            public ProcessInfo Process { get; set; }
            public string SelectedBundleId { get; set; }
            public double? SelectedTime { get; set; }
            public bool FollowLatest { get; set; }
            public bool CaptureScreenshots { get; set; }
            public bool CaptureTemperature { get; set; }
            public bool CaptureThermalState { get; set; }
            public List<PerfSample> Samples { get; set; }
            public List<SessionScreenshot> Screenshots { get; set; }
        }

        private sealed class IconKeyToGlyphConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return AppIconGlyph(System.Convert.ToString(value) ?? "");
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class RecommendedProcessBrushConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                bool recommended = value is bool && (bool)value;
                return recommended ? AccentBlue : TextPrimary;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class RecommendedProcessTextBrushConverter : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                bool recommended = values.Length > 0 && values[0] is bool && (bool)values[0];
                bool selected = values.Length > 1 && values[1] is bool && (bool)values[1];
                if (selected) return Brushes.White;
                return recommended ? AccentBlue : TextPrimary;
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            {
                return new object[] { Binding.DoNothing, Binding.DoNothing };
            }
        }

        private sealed class RecommendedProcessVisibilityConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return value is bool && (bool)value ? Visibility.Visible : Visibility.Collapsed;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class EmptyTextVisibilityConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                string text = System.Convert.ToString(value) ?? "";
                return string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class IconPathVisibilityConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                bool hasIcon = HasIconPath(value);
                bool fallback = string.Equals(System.Convert.ToString(parameter), "fallback", StringComparison.OrdinalIgnoreCase);
                return hasIcon ^ fallback ? Visibility.Visible : Visibility.Collapsed;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }

            private static bool HasIconPath(object value)
            {
                string path = System.Convert.ToString(value) ?? "";
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
        }

        private sealed class IconPathToImageConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                string path = System.Convert.ToString(value) ?? "";
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                try
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 96;
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
                catch
                {
                    return null;
                }
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class SessionScreenshot
        {
            public SessionScreenshot()
            {
                OriginalPath = "";
                ArchivePath = "";
            }

            public DateTime Timestamp { get; set; }
            public double ElapsedSec { get; set; }
            public string OriginalPath { get; set; }
            public string ArchivePath { get; set; }
        }

    }
}
