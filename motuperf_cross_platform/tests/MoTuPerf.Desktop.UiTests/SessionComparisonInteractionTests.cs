using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using CSharpIosPerfMonitor;
using MoTuPerf.Desktop;
using Xunit;

namespace MoTuPerf.Desktop.UiTests
{
    public sealed partial class DevicePickerInteractionTests
    {
        private static ComparisonInput CompareInput(string name, string platform, double cpu) => new ComparisonInput
        {
            Name = name,
            Document = new SessionDocument
            {
                Format = "motuperf-session", Version = 5, StartedAt = new DateTime(2026, 10, 20, 10, 0, 0),
                Device = new DeviceInfo { Name = name, Platform = platform, ProductVersion = "27", Resolution = "1920x1080" },
                App = new AppInfo { Name = "逃离101", BundleId = "com.example.game" },
                Samples = new List<PerfSample>
                {
                    new PerfSample { ElapsedSec = 1, HasCpu = true, CpuUpdated = true, CpuPercent = cpu,
                        HasFps = true, FpsUpdated = true, Fps = 60, OrderedFrames = true,
                        HasFrameObservation = true, FrameObservationMs = 1000, FrameCount = 60,
                        HasFrameTimeMean = true, FrameTimeMeanMs = 1000.0 / 60, HasFrameTimeMax = true, FrameTimeMaxMs = 42,
                        HasJank = true, Jank = 1, BigJank = 0, Source = "ui-test-ordered", FrameSource = "ui-test-ordered",
                        FpsScope = platform == "ios" ? "screen" : "process_surface",
                        HasCpuCoreUsage = true, CpuCoreUpdated = true, CpuCoreCount = 8, CpuCorePercents = Enumerable.Range(0, 8).Select(i => cpu + i).ToList(),
                        HasMemory = true, MemoryUpdated = true, MemoryMetric = platform == "ios" ? "physical_footprint" : "pss", MemoryMb = 256,
                        HasTemperature = true, TemperatureUpdated = true, TemperatureCelsius = new Dictionary<string, double> { ["Battery"] = 36.5 } },
                    new PerfSample { ElapsedSec = 2, HasCpu = true, CpuUpdated = true, CpuPercent = cpu + 20 }
                }
            }
        };

        private static SessionDocument CurveDocument(string appName, string platform, double cpu)
        {
            int thermalMax = string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase) ? 6 : 3;
            return new SessionDocument
            {
                Format = "motuperf-session",
                Version = 5,
                StartedAt = new DateTime(2026, 10, 21, 10, 0, 0),
                Device = new DeviceInfo
                {
                    Name = appName + " 设备", MarketName = platform + " 测试机", Platform = platform,
                    ProductVersion = "27", Resolution = "1920x1080"
                },
                App = new AppInfo { Name = appName, BundleId = "com.example." + appName },
                Samples = Enumerable.Range(0, 20).Select(index => new PerfSample
                {
                    ElapsedSec = index,
                    HasCpu = true, CpuUpdated = true, CpuPercent = cpu + index % 5,
                    HasCpuNormalized = true, CpuNormalizedUpdated = true, CpuNormalizedPercent = cpu / 2 + index % 3,
                    HasCpuCoreUsage = true, CpuCoreUpdated = true, CpuCoreCount = 8,
                    CpuCorePercents = Enumerable.Range(0, 8).Select(core => cpu + core + index % 4).ToList(),
                    HasFps = true, FpsUpdated = true, Fps = 56 + index % 5,
                    OrderedFrames = true, HasFrameObservation = true, FrameObservationMs = 1000,
                    FrameCount = 56 + index % 5, Source = "ui-test-ordered", FrameSource = "ui-test-ordered",
                    FpsScope = platform == "ios" ? "screen" : "process_surface",
                    HasFrameTime = true, FrameTimeMs = 17 + index % 8,
                    HasFrameTimeMean = true, FrameTimeMeanMs = 16.5 + index % 4,
                    HasFrameTimeMax = true, FrameTimeMaxMs = 24 + index % 12,
                    HasFrameTimeP95 = true, FrameTimeP95Ms = 22 + index % 8,
                    HasJank = true, Jank = index % 4, BigJank = index % 7 == 0 ? 1 : 0,
                    HasStutter = true, StutterPercent = 1.5 + index % 3,
                    HasMemory = true, MemoryUpdated = true,
                    MemoryMetric = platform == "ios" ? "physical_footprint" : "pss", MemoryMb = 240 + index * 2,
                    HasTemperature = true, TemperatureUpdated = true,
                    TemperatureCelsius = new Dictionary<string, double>
                    {
                        ["Battery"] = 35 + index * 0.2,
                        ["CPU"] = 40 + index * 0.3,
                        ["NPU"] = 38 + index * 0.4
                    },
                    HasThermalState = true, ThermalStateUpdated = true, ThermalStateLevel = Math.Min(thermalMax, index / 7)
                }).ToList()
            };
        }

        private static Button MetricButton(Border section, string label) =>
            section.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));

        private static IReadOnlyDictionary<string, string> ReportCells(Border section) =>
            section.GetVisualDescendants().OfType<TextBlock>()
                .Select(text => new
                {
                    Id = Avalonia.Automation.AutomationProperties.GetAutomationId(text),
                    Text = text.Text ?? ""
                })
                .Where(cell => !string.IsNullOrWhiteSpace(cell.Id) && cell.Id.Contains("-", StringComparison.Ordinal))
                .GroupBy(cell => cell.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Text, StringComparer.Ordinal);

        private static SessionDocument OverviewStatsDocument(string appName, string platform)
        {
            bool android = string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase);
            return new SessionDocument
            {
                Format = "motuperf-session",
                Version = 5,
                StartedAt = new DateTime(2026, 10, 22, 10, 0, 0),
                Device = new DeviceInfo
                {
                    Name = appName + " 设备", MarketName = platform + " 测试机", Platform = platform,
                    ProductVersion = "27", Resolution = "1920x1080"
                },
                App = new AppInfo { Name = appName, BundleId = "com.example.overview." + platform },
                Samples = Enumerable.Range(0, 20).Select(index =>
                {
                    double interval = index == 19 ? 111 : 10;
                    return new PerfSample
                    {
                        ElapsedSec = index,
                        HasCpu = true, CpuUpdated = true, CpuPercent = 30 + index % 4,
                        HasFps = true, FpsUpdated = true, Fps = 1000.0 / interval,
                        OrderedFrames = true, HasFrameObservation = true,
                        FrameCount = 1, FrameObservationMs = interval, FrameIntervalsMs = new[] { interval },
                        Source = "ui-test-ordered", FrameSource = "ui-test-ordered",
                        FpsScope = android ? "process_surface" : "screen",
                        HasFrameTimeMean = true, FrameTimeMeanMs = interval,
                        HasFrameTimeMax = true, FrameTimeMaxMs = interval,
                        HasJank = true, Jank = index == 19 ? 2 : 0, BigJank = index == 19 ? 1 : 0,
                        HasStutter = true, StutterPercent = index == 19 ? 5 : 0,
                        JankTimeMs = index == 19 ? 50 : 0,
                        HasMemory = true, MemoryUpdated = true,
                        MemoryMetric = android ? "pss" : "physical_footprint", MemoryMb = android ? 210 : 180,
                        HasTemperature = true, TemperatureUpdated = true,
                        TemperatureCelsius = new Dictionary<string, double> { ["Battery"] = android ? 39 : 36 },
                        HasThermalState = android, ThermalStateUpdated = android,
                        ThermalStateLevel = android ? 4 : 0
                    };
                }).ToList()
            };
        }

        [Fact]
        public Task EmptyComparisonToolbarFillsAvailableWidth() => OnUi(async () =>
        {
            string output = Path.Combine(ScreenshotDirectory(), "comparison-empty-v0491");
            Directory.CreateDirectory(output);
            foreach (string theme in new[] { "dark", "light", "retro-sage" })
            {
                AppThemeManager.Apply(Application.Current, theme, false);
                foreach (int width in new[] { 900, 1160 })
                {
                    var window = new SessionComparisonWindow { Width = width, Height = 700 };
                    window.Show();
                    try
                    {
                        await Task.Delay(20);
                        window.UpdateLayout();
                        var toolbar = (StackPanel)window.FindControl<Expander>("SelectionExpander").Parent;
                        var selection = window.FindControl<Expander>("SelectionExpander");
                        var scroll = window.FindControl<ScrollViewer>("TableScroll");
                        var header = window.FindControl<Grid>("TableHeader");
                        Assert.Equal(toolbar.Bounds.Width, selection.Bounds.Width, 1);
                        Assert.Equal(scroll.Viewport.Width, header.Bounds.Width, 1);
                        using var bitmap = new RenderTargetBitmap(new PixelSize(width, 700));
                        bitmap.Render(window);
                        bitmap.Save(Path.Combine(output, theme + "-" + width + ".png"), PngBitmapEncoderOptions.Default);
                    }
                    finally { window.Close(); }
                }
            }
        });

        [Fact]
        public Task ComparisonShowsFiveColumnsAcrossThemesAndExportsThroughClick() => OnUi(async () =>
        {
            string output = Path.Combine(ScreenshotDirectory(), "comparison-v0490");
            Directory.CreateDirectory(output);
            foreach (string theme in new[] { "dark", "light", "retro-sage" })
            {
                AppThemeManager.Apply(Application.Current, theme, false);
                foreach (int width in new[] { 900, 1160 })
                {
                    var window = new SessionComparisonWindow { Width = width, Height = 700 };
                    window.Show();
                    try
                    {
                        for (int i = 0; i < 5; i++) await window.ReplaceAsync(i,
                            CompareInput("测试记录 " + (i + 1) + " - 长文件名-性能发热对比", i == 0 ? "ios" : "android", 20 + i * 5));
                        await Task.Delay(40);
                        window.UpdateLayout();
                        var scroll = window.FindControl<ScrollViewer>("TableScroll");
                        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
                        var header = window.FindControl<Grid>("TableHeader");
                        Assert.Equal(7, header.ColumnDefinitions.Count);
                        Assert.All(header.Children, cell => Assert.True(cell.Bounds.Right <= header.Bounds.Width + 1));
                        Assert.Equal(header.Bounds.Width, window.FindControl<StackPanel>("TableRows").Bounds.Width, 1);
                        var selection = window.FindControl<Expander>("SelectionExpander");
                        Assert.True(selection.IsExpanded);
                        selection.IsExpanded = false;
                        await Task.Delay(20);
                        window.UpdateLayout();
                        double expandedViewport = scroll.Viewport.Height;
                        using (var collapsedBitmap = new RenderTargetBitmap(new PixelSize(width, 700)))
                        {
                            collapsedBitmap.Render(window);
                            collapsedBitmap.Save(Path.Combine(output, theme + "-" + width + "-collapsed.png"), PngBitmapEncoderOptions.Default);
                        }
                        selection.IsExpanded = true;
                        await Task.Delay(20);
                        window.UpdateLayout();
                        Assert.True(scroll.Viewport.Height < expandedViewport);
                        var options = window.FindControl<Grid>("OptionsPanel");
                        Assert.All(options.Children, cell => Assert.True(cell.Bounds.Right <= options.Bounds.Width + 1));
                        var body = window.FindControl<StackPanel>("TableRows");
                        var details = body.Children.OfType<Expander>().Single();
                        Assert.False(details.IsExpanded);
                        Assert.DoesNotContain(body.Children.OfType<Grid>(), grid => grid.GetVisualDescendants()
                            .OfType<TextBlock>().Any(text => text.Text == "帧来源"));
                        scroll.ScrollToEnd();
                        window.UpdateLayout();
                        var detailsToggle = details.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single();
                        var detailsPoint = detailsToggle.TranslatePoint(new Point(25, detailsToggle.Bounds.Height / 2), window).Value;
                        window.MouseDown(detailsPoint, Avalonia.Input.MouseButton.Left);
                        window.MouseUp(detailsPoint, Avalonia.Input.MouseButton.Left);
                        Assert.True(details.IsExpanded);
                        await Task.Delay(40);
                        window.UpdateLayout();
                        scroll.ScrollToEnd();
                        await Task.Delay(40);
                        window.UpdateLayout();
                        var detailPanel = Assert.IsType<StackPanel>(details.Content);
                        Assert.Equal(6, detailPanel.Children.Count);
                        Assert.All(detailPanel.Children, row => Assert.Equal(body.Bounds.Width, row.Bounds.Width, 1));
                        using (var expanded = new RenderTargetBitmap(new PixelSize(width, 700)))
                        {
                            expanded.Render(window);
                            expanded.Save(Path.Combine(output, theme + "-" + width + "-details.png"), PngBitmapEncoderOptions.Default);
                        }
                        details.IsExpanded = false;
                        scroll.ScrollToHome();
                        await Task.Delay(20);
                        using (var bitmap = new RenderTargetBitmap(new PixelSize(width, 700)))
                        {
                            bitmap.Render(window);
                            bitmap.Save(Path.Combine(output, theme + "-" + width + ".png"), PngBitmapEncoderOptions.Default);
                        }
                        var peak = window.FindControl<RadioButton>("PeakMode");
                        details.IsExpanded = true;
                        peak.IsChecked = true;
                        await Until(() => !window.FindControl<Border>("BusyOverlay").IsVisible);
                        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "40.00");
                        Assert.True(body.Children.OfType<Expander>().Single().IsExpanded);
                        body.Children.OfType<Expander>().Single().IsExpanded = false;
                        var end = window.FindControl<TextBox>("EndTimeInput");
                        end.Text = "1.5";
                        await Until(() => !window.FindControl<Button>("ExportButton").IsEnabled);
                        Assert.False(window.FindControl<Button>("ExportButton").IsEnabled);
                        var apply = options.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "应用"));
                        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Until(() => !window.FindControl<Border>("BusyOverlay").IsVisible);
                        Assert.True(window.FindControl<Button>("ExportButton").IsEnabled);
                        string csv = Path.Combine(output, theme + "-" + width + ".csv");
                        window.ExportPathPicker = () => Task.FromResult(csv);
                        window.FindControl<Button>("ExportButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Until(() => !window.FindControl<Border>("BusyOverlay").IsVisible);
                        Assert.Contains("峰值", File.ReadAllText(csv));
                        Assert.Contains("20.00", File.ReadAllText(csv));
                        await window.ReplaceAsync(4, null);
                        Assert.Equal(6, header.ColumnDefinitions.Count);
                        end.Text = "-1";
                        await Task.Delay(20);
                        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.False(window.FindControl<Button>("ExportButton").IsEnabled);
                        Assert.Contains("结束时间", window.FindControl<TextBlock>("StatusText").Text);
                        await window.ReplaceAsync(4, CompareInput("替换现场", "android", 30));
                        await Task.Delay(20);
                        Assert.Equal("1.5", end.Text);
                        Assert.True(window.FindControl<Button>("ExportButton").IsEnabled);
                    }
                    finally { window.Close(); }
                }
            }
        });

        [Fact]
        public Task ComparisonSlotMenuReusesLoadedRecords() => OnUi(async () =>
        {
            var window = new SessionComparisonWindow { Width = 1160, Height = 700 };
            window.Show();
            try
            {
                await window.ReplaceAsync(0, CompareInput("第一条记录", "android", 20));
                await window.ReplaceAsync(1, CompareInput("第二条记录", "ios", 40));
                var slot = window.SlotButtonForTest(0);
                slot.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(30);
                var menu = slot.ContextMenu;
                Assert.NotNull(menu);
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.Header is TextBlock text && text.Text == "第二条记录");
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "导入其他路径文件…"));
                var reuse = menu.Items.OfType<MenuItem>().Single(item => item.Header is TextBlock text && text.Text == "第二条记录");
                reuse.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Until(() => !window.FindControl<Border>("BusyOverlay").IsVisible);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "A · 第二条记录");
                menu.Close();
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task ComparisonModeSwitchesBothWaysAndExportsAppliedMode() => OnUi(async () =>
        {
            var window = new SessionComparisonWindow();
            window.Show();
            try
            {
                await window.ReplaceAsync(0, CompareInput("A", "ios", 20));
                await window.ReplaceAsync(1, CompareInput("B", "android", 50));
                string CpuValue()
                {
                    var label = window.GetVisualDescendants().OfType<TextBlock>().Single(text =>
                        Avalonia.Automation.AutomationProperties.GetAutomationId(text) == "进程 CPU Raw");
                    var border = (Border)label.Parent;
                    var grid = (Grid)border.Parent;
                    int row = Grid.GetRow(border);
                    var value = grid.Children.OfType<Border>().Single(cell => Grid.GetRow(cell) == row && Grid.GetColumn(cell) == 2);
                    return ((TextBlock)value.Child).Text;
                }
                Assert.Equal("30.00", CpuValue());
                foreach (bool peak in new[] { true, false, true, false })
                {
                    var button = window.FindControl<RadioButton>(peak ? "PeakMode" : "AverageMode");
                    window.UpdateLayout();
                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window).Value;
                    window.MouseDown(point, Avalonia.Input.MouseButton.Left);
                    window.MouseUp(point, Avalonia.Input.MouseButton.Left);
                    await Until(() => !window.FindControl<Border>("BusyOverlay").IsVisible);
                    Assert.True(button.IsChecked == true);
                    Assert.False(window.FindControl<RadioButton>(peak ? "AverageMode" : "PeakMode").IsChecked == true);
                    Assert.Equal(peak ? "40.00" : "30.00", CpuValue());
                    Assert.Contains(peak ? "峰值" : "均值", window.FindControl<TextBlock>("StatusText").Text);
                    string output = Path.Combine(ScreenshotDirectory(), "comparison-v0490");
                    Directory.CreateDirectory(output);
                    string csv = Path.Combine(output, peak ? "peak.csv" : "average.csv");
                    window.ExportPathPicker = () => Task.FromResult(csv);
                    window.FindControl<Button>("ExportButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => !window.FindControl<Border>("BusyOverlay").IsVisible);
                    string text = File.ReadAllText(csv);
                    Assert.Contains(peak ? "峰值" : "均值", text);
                    Assert.Contains(peak ? "40.00" : "30.00", text);
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1160, 760));
                    window.UpdateLayout();
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, peak ? "peak.png" : "average.png"), PngBitmapEncoderOptions.Default);
                }
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task ComparisonLoadsArchiveAndPreservesSlotOnFailure() => OnUi(async () =>
        {
            string output = Path.Combine(ScreenshotDirectory(), "comparison-v0470");
            Directory.CreateDirectory(output);
            string archive = Path.Combine(output, "load-test.motuperf");
            SessionArchiveService.Save(archive, CompareInput("测试", "android", 50).Document);
            var window = new SessionComparisonWindow();
            window.Show();
            try
            {
                await window.LoadFileAsync(0, archive);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "60.00");
                await window.LoadFileAsync(0, Path.Combine(output, "missing.motuperf"));
                Assert.Contains("读取失败", window.FindControl<TextBlock>("StatusText").Text);
                Assert.False(window.FindControl<Border>("BusyOverlay").IsVisible);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "60.00");
                await window.ReplaceAsync(0, null);
                Assert.False(window.FindControl<Button>("ExportButton").IsEnabled);
            }
            finally { window.Close(); }
        });

        [Fact]
        public void ComparisonChartCompactionKeepsTimeOrderAndValueExtremes()
        {
            var points = Enumerable.Range(0, 1000).Select(index => new ComparisonChartPoint
            {
                TimeSec = index,
                Value = index == 317 ? -50 : index == 641 ? 250 : Math.Sin(index / 10.0) * 20
            }).ToList();
            IReadOnlyList<ComparisonChartPoint> compact = ComparisonChartControl.CompactForTest(points, 100);
            Assert.InRange(compact.Count, 4, 100);
            Assert.All(Enumerable.Range(1, compact.Count - 1), index => Assert.True(compact[index - 1].TimeSec < compact[index].TimeSec));
            Assert.Contains(compact, point => point.TimeSec == 317 && point.Value == -50);
            Assert.Contains(compact, point => point.TimeSec == 641 && point.Value == 250);
        }

        [Fact]
        public Task ComparisonChartCentersLegendBelowRangeSliderAndSupportsZoomPanReset() => OnUi(async () =>
        {
            string firstName = "A · 逃离101 长时间稳定性测试";
            string secondName = "B · 逃离101 发热对比测试";
            var series = new[]
            {
                new ComparisonChartSeries
                {
                    Name = firstName,
                    Color = "#FF6B9A",
                    Points = Enumerable.Range(0, 121).Select(index => new ComparisonChartPoint
                    {
                        TimeSec = index * 0.5,
                        Value = 42 + Math.Sin(index / 7.0) * 8
                    }).ToList()
                },
                new ComparisonChartSeries
                {
                    Name = secondName,
                    Color = "#4DB5E0",
                    Points = Enumerable.Range(0, 121).Select(index => new ComparisonChartPoint
                    {
                        TimeSec = index * 0.5,
                        Value = 34 + Math.Cos(index / 9.0) * 6
                    }).ToList()
                }
            };
            var chart = new ComparisonChartControl { Series = series, Unit = "FPS" };
            var window = new Window { Content = chart, Width = 860, Height = 360 };
            window.Show();
            try
            {
                await Task.Delay(30);
                window.UpdateLayout();
                string output = Path.Combine(ScreenshotDirectory(), "comparison-range-v0520");
                Directory.CreateDirectory(output);
                using (var initial = new RenderTargetBitmap(new PixelSize(860, 360)))
                {
                    initial.Render(window);
                    initial.Save(Path.Combine(output, "range-full-860.png"), PngBitmapEncoderOptions.Default);
                }

                Rect track = chart.RangeTrackBoundsForTest;
                Rect firstLegend = chart.LegendBoundsForTest(firstName);
                Rect secondLegend = chart.LegendBoundsForTest(secondName);
                Assert.True(track.Width > 600);
                Assert.True(firstLegend.Top > track.Bottom);
                Assert.True(secondLegend.Top > track.Bottom);
                Assert.InRange((firstLegend.Left + secondLegend.Right) / 2, chart.Bounds.Width / 2 - 30, chart.Bounds.Width / 2 + 30);

                Point TrackPoint(double fraction) => chart.TranslatePoint(
                    new Point(track.Left + track.Width * fraction, track.Top + track.Height / 2), window).Value;
                Point startHandle = TrackPoint(0.01);
                Point zoomStart = TrackPoint(0.25);
                window.MouseDown(startHandle, Avalonia.Input.MouseButton.Left);
                window.MouseMove(zoomStart, Avalonia.Input.RawInputModifiers.LeftMouseButton);
                window.MouseUp(zoomStart, Avalonia.Input.MouseButton.Left);
                await Task.Delay(20);
                window.UpdateLayout();
                Assert.InRange(chart.VisibleStartFractionForTest, 0.23, 0.27);
                Assert.Equal(1, chart.VisibleEndFractionForTest, 3);

                double zoomedWidth = chart.VisibleEndFractionForTest - chart.VisibleStartFractionForTest;
                Rect selection = chart.RangeSelectionBoundsForTest;
                Point panStart = chart.TranslatePoint(new Point(selection.Center.X, selection.Center.Y), window).Value;
                Point panEnd = chart.TranslatePoint(new Point(selection.Center.X - track.Width * 0.1, selection.Center.Y), window).Value;
                window.MouseDown(panStart, Avalonia.Input.MouseButton.Left);
                window.MouseMove(panEnd, Avalonia.Input.RawInputModifiers.LeftMouseButton);
                window.MouseUp(panEnd, Avalonia.Input.MouseButton.Left);
                await Task.Delay(20);
                window.UpdateLayout();
                Assert.InRange(chart.VisibleEndFractionForTest - chart.VisibleStartFractionForTest, zoomedWidth - 0.005, zoomedWidth + 0.005);
                Assert.InRange(chart.VisibleStartFractionForTest, 0.13, 0.17);

                using (var zoomed = new RenderTargetBitmap(new PixelSize(860, 360)))
                {
                    zoomed.Render(window);
                    zoomed.Save(Path.Combine(output, "range-zoomed-panned-860.png"), PngBitmapEncoderOptions.Default);
                }

                Rect movedLegend = chart.LegendBoundsForTest(firstName);
                Point legendPoint = chart.TranslatePoint(new Point(movedLegend.Center.X, movedLegend.Center.Y), window).Value;
                window.MouseDown(legendPoint, Avalonia.Input.MouseButton.Left);
                window.MouseUp(legendPoint, Avalonia.Input.MouseButton.Left);
                Assert.False(chart.IsSeriesVisibleForTest(firstName));

                Rect reset = chart.RangeResetBoundsForTest;
                Point resetPoint = chart.TranslatePoint(new Point(reset.Center.X, reset.Center.Y), window).Value;
                window.MouseDown(resetPoint, Avalonia.Input.MouseButton.Left);
                window.MouseUp(resetPoint, Avalonia.Input.MouseButton.Left);
                Assert.Equal(0, chart.VisibleStartFractionForTest, 3);
                Assert.Equal(1, chart.VisibleEndFractionForTest, 3);
                chart.SetSeriesVisible(firstName, true);

                string previousTheme = AppThemeManager.Current.Id;
                try
                {
                    foreach (string theme in new[] { "dark", "light", "retro-sage" })
                    {
                        AppThemeManager.Apply(Application.Current, theme, false);
                        ComparisonChartControl.RefreshThemeResources();
                        window.UpdateLayout();
                        using var themed = new RenderTargetBitmap(new PixelSize(860, 360));
                        themed.Render(window);
                        themed.Save(Path.Combine(output, "range-" + theme + "-860.png"), PngBitmapEncoderOptions.Default);
                    }
                }
                finally
                {
                    AppThemeManager.Apply(Application.Current, previousTheme, false);
                    ComparisonChartControl.RefreshThemeResources();
                }
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task IntegratedWorkspaceRefreshesCurrentSessionAndKeepsFiveEqualSlots() => OnUi(async () =>
        {
            string previousTheme = AppThemeManager.Current.Id;
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(CurveDocument("第一版", "ios", 20));
                Assert.Equal("1 / 5", workspace.FindControl<TextBlock>("SlotSummaryText").Text);
                await workspace.ReplaceAsync(1, new ComparisonInput
                {
                    Name = "vivo-大神-20260827-113215", Document = CurveDocument("微信", "android", 35)
                });
                Assert.Equal("2 / 5", workspace.FindControl<TextBlock>("SlotSummaryText").Text);
                Assert.Equal("vivo-大神-20260827-113215", workspace.SlotCardForTest(1).GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "SlotTitle-1").Text);
                Assert.Equal("微信", workspace.SlotCardForTest(1).GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "SlotApplication-1").Text);
                Assert.Equal("2026-10-21 10:00 · 20 样本", workspace.SlotCardForTest(1).GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "SlotMeta-1").Text);
                Border overviewSessionCell = workspace.SectionForTest("overview").GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "SessionCell-1");
                TextBlock overviewSessionLabel = Assert.IsType<TextBlock>(overviewSessionCell.Child);
                string overviewSessionText = overviewSessionLabel.Text;
                Assert.StartsWith("B · vivo-大神-20260827-113215", overviewSessionText);
                Assert.EndsWith("\nandroid 测试机", overviewSessionText);
                Assert.DoesNotContain("B · 微信", overviewSessionText);
                Assert.Equal(TextWrapping.NoWrap, overviewSessionLabel.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, overviewSessionLabel.TextTrimming);

                await workspace.InitializeAsync(CurveDocument("第二版", "ios", 28));
                Assert.Equal("2 / 5", workspace.FindControl<TextBlock>("SlotSummaryText").Text);
                Assert.Equal("当前现场", workspace.SlotCardForTest(0).GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "SlotTitle-0").Text);
                Assert.Equal("第二版", workspace.SlotCardForTest(0).GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "SlotApplication-0").Text);
                Assert.Equal("2026-10-21 10:00 · 20 样本", workspace.SlotCardForTest(0).GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "SlotMeta-0").Text);
                Assert.DoesNotContain(workspace.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "第一版");

                for (int slot = 2; slot < 5; slot++)
                {
                    await workspace.ReplaceAsync(slot, new ComparisonInput
                    {
                        Name = "样本 " + (slot + 1), Document = CurveDocument("样本 " + (slot + 1), "android", 40 + slot)
                    });
                }
                window.UpdateLayout();
                var panel = workspace.FindControl<UniformGrid>("SessionCardsPanel");
                Assert.Equal(5, panel.Children.Count);
                Assert.True(panel.Bounds.Width > 1000);
                Assert.True(overviewSessionLabel.Bounds.Width >= 180);
                double firstWidth = workspace.SlotCardForTest(0).Bounds.Width;
                Assert.All(Enumerable.Range(0, 5), slot => Assert.InRange(workspace.SlotCardForTest(slot).Bounds.Width, firstWidth - 4, firstWidth + 4));

                string output = Path.Combine(ScreenshotDirectory(), "session-column-width-v0531");
                Directory.CreateDirectory(output);
                foreach (string theme in new[] { "dark", "light", "retro-sage" })
                {
                    AppThemeManager.Apply(Application.Current, theme, false);
                    window.UpdateLayout();
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, theme + "-1180.png"), PngBitmapEncoderOptions.Default);
                }
            }
            finally
            {
                window.Close();
                AppThemeManager.Apply(Application.Current, previousTheme, false);
            }
        });

        [Fact]
        public Task IntegratedWorkspaceLoadsArchiveAndPreservesSlotOnFailure() => OnUi(async () =>
        {
            string output = Path.Combine(ScreenshotDirectory(), "integrated-curve-analysis-v0510");
            Directory.CreateDirectory(output);
            string archive = Path.Combine(output, "integrated-load-test.motuperf");
            SessionArchiveService.Save(archive, CurveDocument("归档现场", "android", 50));

            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.LoadFileAsync(2, archive);
                Assert.Equal("1 / 5", workspace.FindControl<TextBlock>("SlotSummaryText").Text);
                Assert.Equal("integrated-load-test", workspace.SlotCardForTest(2).GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "SlotTitle-2").Text);
                Assert.Equal("归档现场", workspace.SlotCardForTest(2).GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "SlotApplication-2").Text);

                await workspace.LoadFileAsync(2, Path.Combine(output, "missing-integrated.motuperf"));
                Assert.Contains("读取失败", workspace.FindControl<TextBlock>("StatusText").Text);
                Assert.False(workspace.FindControl<Border>("BusyOverlay").IsVisible);
                Assert.Equal("1 / 5", workspace.FindControl<TextBlock>("SlotSummaryText").Text);
                Assert.Contains(workspace.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "integrated-load-test");
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task IntegratedWorkspaceUsesDirectAddAndRemoveActions() => OnUi(async () =>
        {
            string previousTheme = AppThemeManager.Current.Id;
            string output = Path.Combine(ScreenshotDirectory(), "integrated-session-actions-v0526");
            Directory.CreateDirectory(output);
            string archive = Path.Combine(output, "direct-add-session.motuperf");
            SessionArchiveService.Save(archive, CurveDocument("直接添加现场", "android", 42));

            var workspace = new SessionComparisonWorkspace { ImportPathPicker = () => Task.FromResult(archive) };
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(CurveDocument("当前应用", "ios", 24));
                window.UpdateLayout();
                Button removeCurrent = workspace.SlotCardForTest(0).GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "SlotRemove-0");
                Assert.Equal("×", removeCurrent.Content);
                Button addSecond = workspace.SlotCardForTest(1).GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "SlotAdd-1");
                Assert.Equal("添加现场", addSecond.Content);
                StackPanel emptyGroup = workspace.SlotCardForTest(1).GetVisualDescendants().OfType<StackPanel>()
                    .Single(panel => panel.Name == "SlotAddGroup-1");
                Border emptyCard = workspace.SlotCardForTest(1);
                Point emptyGroupOrigin = emptyGroup.TranslatePoint(new Point(0, 0), emptyCard).Value;
                double emptyGroupTop = emptyGroupOrigin.Y;
                double emptyGroupBottom = emptyCard.Bounds.Height - emptyGroupTop - emptyGroup.Bounds.Height;
                Assert.InRange(Math.Abs(emptyGroupTop - emptyGroupBottom), 0, 1.5);
                Assert.Null(workspace.SlotCardForTest(0).ContextMenu);
                Assert.Null(workspace.SlotCardForTest(1).ContextMenu);
                Assert.DoesNotContain(workspace.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("sessionMenuButton"));

                string emptyOutput = Path.Combine(ScreenshotDirectory(), "empty-slot-centering-v0532");
                Directory.CreateDirectory(emptyOutput);
                foreach (string theme in new[] { "dark", "light", "retro-sage" })
                {
                    AppThemeManager.Apply(Application.Current, theme, false);
                    window.UpdateLayout();
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(emptyOutput, theme + "-1180.png"), PngBitmapEncoderOptions.Default);
                }
                AppThemeManager.Apply(Application.Current, previousTheme, false);
                window.UpdateLayout();

                addSecond.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.FindControl<TextBlock>("SlotSummaryText").Text == "2 / 5");
                Assert.Equal("direct-add-session", workspace.SlotCardForTest(1).GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "SlotTitle-1").Text);
                Assert.Single(workspace.SlotCardForTest(1).GetVisualDescendants().OfType<Button>(),
                    button => button.Name == "SlotRemove-1" && Equals(button.Content, "×"));

                window.UpdateLayout();
                foreach (string theme in new[] { "dark", "light", "retro-sage" })
                {
                    AppThemeManager.Apply(Application.Current, theme, false);
                    window.UpdateLayout();
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, theme + "-1180.png"), PngBitmapEncoderOptions.Default);
                }

                Button removeSecond = workspace.SlotCardForTest(1).GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "SlotRemove-1");
                removeSecond.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.FindControl<TextBlock>("SlotSummaryText").Text == "1 / 5");
                Assert.Single(workspace.SlotCardForTest(1).GetVisualDescendants().OfType<Button>(),
                    button => button.Name == "SlotAdd-1" && Equals(button.Content, "添加现场"));
            }
            finally
            {
                window.Close();
                AppThemeManager.Apply(Application.Current, previousTheme, false);
            }
        });

        [Fact]
        public Task IntegratedWorkspaceLongPageNavigatesSectionsAndSwitchesMetrics() => OnUi(async () =>
        {
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(CurveDocument("A 应用", "ios", 20));
                await workspace.ReplaceAsync(1, new ComparisonInput { Name = "B 应用", Document = CurveDocument("B 应用", "android", 50) });

                Assert.Null(workspace.FindControl<Button>("AverageButton"));
                Assert.Null(workspace.FindControl<Button>("PeakButton"));
                Assert.Null(workspace.FindControl<TextBox>("StartTimeInput"));
                Assert.Null(workspace.FindControl<Button>("ExportButton"));
                Assert.All(new[] { "overview", "fps", "frametime", "cpu", "memory", "temperature", "thermal" },
                    category => Assert.NotNull(workspace.SectionForTest(category)));
                Assert.Equal(ComparisonMode.Average, workspace.ReportForTest.Mode);
                Assert.Equal(0, workspace.AppliedStartForTest);
                Assert.Null(workspace.AppliedEndForTest);

                var scroll = workspace.FindControl<ScrollViewer>("ContentScroll");
                window.UpdateLayout();
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);

                workspace.TabButtonForTest("fps").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => scroll.Offset.Y > 1);
                Assert.Equal("fps", workspace.ActiveCategoryForTest);
                var bigJank = workspace.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "BigJank"));
                bigJank.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("bigjank", workspace.ActiveMetricForTest);

                double fpsOffset = scroll.Offset.Y;
                workspace.TabButtonForTest("thermal").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => scroll.Offset.Y > fpsOffset);
                Assert.Equal("thermal", workspace.ActiveCategoryForTest);

                window.UpdateLayout();
                Point? temperaturePoint = workspace.SectionForTest("temperature").TranslatePoint(new Point(0, 0), scroll);
                Assert.True(temperaturePoint.HasValue);
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y + temperaturePoint.Value.Y - 8));
                await Until(() => workspace.ActiveCategoryForTest == "temperature");
                double temperatureOffset = scroll.Offset.Y;
                workspace.TabButtonForTest("thermal").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveCategoryForTest == "thermal" && scroll.Offset.Y > temperatureOffset);
                window.UpdateLayout();

                string output = Path.Combine(ScreenshotDirectory(), "integrated-curve-analysis-v0510");
                Directory.CreateDirectory(output);
                using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(output, "long-page-thermal-1180.png"), PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task IntegratedCurveTimeTrendAlignmentStretchesShorterSessionWithoutMutatingData() => OnUi(async () =>
        {
            string previousTheme = AppThemeManager.Current.Id;
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                SessionDocument shortDocument = CurveDocument("短现场", "ios", 20);
                shortDocument.Samples = shortDocument.Samples.Take(10).ToList();
                await workspace.InitializeAsync(shortDocument);
                await workspace.ReplaceAsync(1, new ComparisonInput
                {
                    Name = "长现场", Document = CurveDocument("长现场", "android", 50)
                });
                workspace.TabButtonForTest("fps").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveCategoryForTest == "fps");

                var content = workspace.FindControl<StackPanel>("ContentHost");
                ScrollViewer scroll = workspace.FindControl<ScrollViewer>("ContentScroll");
                Border fps = workspace.SectionForTest("fps");
                ComparisonChartControl chart = fps.GetVisualDescendants().OfType<ComparisonChartControl>().Single();
                CheckBox toggle = fps.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.Name == "TimeTrendToggle-fps");
                Point? chartPosition = chart.TranslatePoint(new Point(0, 0), scroll);
                Assert.True(chartPosition.HasValue);
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y + chartPosition.Value.Y - 250));
                await Task.Delay(30);
                window.UpdateLayout();

                Assert.False(toggle.IsChecked == true);
                Assert.Equal("Align Time Trends", toggle.Content);
                Assert.False(chart.AlignTimeTrends);
                Assert.True(chart.Series.Count >= 2,
                    "Expected two chart series, actual: " + string.Join(" | ", chart.Series.Select(series =>
                        (series.Name ?? "<null>") + ":" + (series.Points?.Count ?? 0))));
                Assert.True(chart.Series.Any(series => string.Equals(series.Name, "A · 当前现场", StringComparison.Ordinal)),
                    "Expected A session series, actual: " + string.Join(" | ", chart.Series.Select(series => series.Name ?? "<null>")));
                Assert.Equal(9, chart.DisplayedSeriesEndTimeForTest("A · 当前现场"), 2);
                Assert.Equal(19, chart.DisplayedSeriesEndTimeForTest("B · 长现场"), 2);
                ComparisonReport report = workspace.ReportForTest;

                toggle.IsChecked = true;
                await Until(() => chart.AlignTimeTrends
                    && Math.Abs(chart.DisplayedSeriesEndTimeForTest("A · 当前现场") - 19) < 0.01);
                window.UpdateLayout();

                List<CheckBox> toggles = content.GetVisualDescendants().OfType<CheckBox>()
                    .Where(control => control.Name != null && control.Name.StartsWith("TimeTrendToggle-", StringComparison.Ordinal))
                    .ToList();
                Assert.Equal(6, toggles.Count);
                Assert.All(toggles, control => Assert.True(control.IsChecked == true));
                Assert.All(content.GetVisualDescendants().OfType<ComparisonChartControl>(),
                    comparison => Assert.True(comparison.AlignTimeTrends));
                Assert.Same(report, workspace.ReportForTest);
                Assert.Equal(19, chart.DisplayedSeriesEndTimeForTest("A · 当前现场"), 2);
                Assert.Equal(19, chart.DisplayedSeriesEndTimeForTest("B · 长现场"), 2);
                Assert.Equal(9, chart.Series[0].Points.Last().TimeSec, 2);
                Assert.Equal(0, chart.VisibleStartFractionForTest, 3);
                Assert.Equal(1, chart.VisibleEndFractionForTest, 3);

                string output = Path.Combine(ScreenshotDirectory(), "time-trend-alignment-v0540");
                Directory.CreateDirectory(output);
                foreach (string theme in new[] { "dark", "light", "retro-sage" })
                {
                    AppThemeManager.Apply(Application.Current, theme, false);
                    ComparisonChartControl.RefreshThemeResources();
                    window.UpdateLayout();
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, theme + "-1180.png"), PngBitmapEncoderOptions.Default);
                }
                AppThemeManager.Apply(Application.Current, previousTheme, false);
                ComparisonChartControl.RefreshThemeResources();
                window.UpdateLayout();

                toggle.IsChecked = false;
                await Until(() => !chart.AlignTimeTrends
                    && Math.Abs(chart.DisplayedSeriesEndTimeForTest("A · 当前现场") - 9) < 0.01);
                window.UpdateLayout();
                Assert.All(toggles, control => Assert.False(control.IsChecked == true));
                Assert.All(content.GetVisualDescendants().OfType<ComparisonChartControl>(),
                    comparison => Assert.False(comparison.AlignTimeTrends));
                Assert.Equal(9, chart.DisplayedSeriesEndTimeForTest("A · 当前现场"), 2);
                Assert.Same(report, workspace.ReportForTest);
            }
            finally
            {
                window.Close();
                AppThemeManager.Apply(Application.Current, previousTheme, false);
            }
        });

        [Fact]
        public Task OverviewShowsStatisticsOnlyAndMetricSectionsKeepCharts() => OnUi(async () =>
        {
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(CurveDocument("A 应用", "ios", 20));
                await workspace.ReplaceAsync(1, new ComparisonInput
                {
                    Name = "B 应用", Document = CurveDocument("B 应用", "android", 50)
                });
                workspace.SetCategoryForTest("overview");
                window.UpdateLayout();

                Border overview = workspace.SectionForTest("overview");
                Assert.Empty(overview.GetVisualDescendants().OfType<ComparisonChartControl>());
                Assert.All(new[] { "fps", "frametime", "cpu" }, category =>
                    Assert.NotEmpty(workspace.SectionForTest(category).GetVisualDescendants().OfType<ComparisonChartControl>()));

                string output = Path.Combine(ScreenshotDirectory(), "integrated-curve-analysis-v0515");
                Directory.CreateDirectory(output);
                using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(output, "overview-statistics-only-1180.png"), PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task IntegratedCurveChartPlacesLegendUnderRangeSliderAndZoomsInPlace() => OnUi(async () =>
        {
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(CurveDocument("A 应用", "ios", 20));
                await workspace.ReplaceAsync(1, new ComparisonInput
                {
                    Name = "B 应用", Document = CurveDocument("B 应用", "android", 50)
                });
                workspace.TabButtonForTest("fps").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var scroll = workspace.FindControl<ScrollViewer>("ContentScroll");
                await Until(() => scroll.Offset.Y > 1);
                window.UpdateLayout();

                ComparisonChartControl chart = workspace.SectionForTest("fps")
                    .GetVisualDescendants().OfType<ComparisonChartControl>().Single();
                Point? chartPosition = chart.TranslatePoint(new Point(0, 0), scroll);
                Assert.True(chartPosition.HasValue);
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y + chartPosition.Value.Y - 250));
                await Task.Delay(30);
                window.UpdateLayout();
                Point? chartViewportPosition = chart.TranslatePoint(new Point(0, 0), scroll);
                Assert.True(chartViewportPosition.HasValue);
                if (chartViewportPosition.Value.Y > 110)
                    scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + chartViewportPosition.Value.Y - 110);
                await Task.Delay(30);
                window.UpdateLayout();

                Rect track = chart.RangeTrackBoundsForTest;
                Assert.Equal(360, chart.Bounds.Height, 1);
                Assert.True(track.Top > chart.Bounds.Height * 0.7);
                Rect firstLegend = chart.LegendBoundsForTest(chart.Series[0].Name);
                Rect secondLegend = chart.LegendBoundsForTest(chart.Series[1].Name);
                Assert.True(firstLegend.Top > track.Bottom);
                Assert.True(secondLegend.Top > track.Bottom);

                string output = Path.Combine(ScreenshotDirectory(), "comparison-range-v0520");
                Directory.CreateDirectory(output);
                using (var full = new RenderTargetBitmap(new PixelSize(1180, 760)))
                {
                    full.Render(window);
                    full.Save(Path.Combine(output, "integrated-range-full-1180.png"), PngBitmapEncoderOptions.Default);
                }

                Point start = chart.TranslatePoint(new Point(track.Left + 5, track.Center.Y), window).Value;
                Point end = chart.TranslatePoint(new Point(track.Left + track.Width * 0.35, track.Center.Y), window).Value;
                window.MouseDown(start, Avalonia.Input.MouseButton.Left);
                window.MouseMove(end, Avalonia.Input.RawInputModifiers.LeftMouseButton);
                window.MouseUp(end, Avalonia.Input.MouseButton.Left);
                await Task.Delay(20);
                window.UpdateLayout();
                Assert.InRange(chart.VisibleStartFractionForTest, 0.33, 0.37);

                using (var zoomed = new RenderTargetBitmap(new PixelSize(1180, 760)))
                {
                    zoomed.Render(window);
                    zoomed.Save(Path.Combine(output, "integrated-range-zoomed-1180.png"), PngBitmapEncoderOptions.Default);
                }
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task MetricSubtabsSwitchMatchingStatisticsTables() => OnUi(async () =>
        {
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(CurveDocument("A 应用", "ios", 20));
                await workspace.ReplaceAsync(1, new ComparisonInput
                {
                    Name = "B 应用", Document = CurveDocument("B 应用", "android", 50)
                });

                var scroll = workspace.FindControl<ScrollViewer>("ContentScroll");
                workspace.TabButtonForTest("fps").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveCategoryForTest == "fps" && scroll.Offset.Y > 1);
                window.UpdateLayout();
                string output = Path.Combine(ScreenshotDirectory(), "integrated-curve-analysis-v0514");
                Directory.CreateDirectory(output);

                IReadOnlyDictionary<string, string> cells = ReportCells(workspace.SectionForTest("fps"));
                Assert.Contains("FPS-A", cells.Keys);
                Assert.Contains("FPS Std-A", cells.Keys);
                Assert.Contains("FPS MedRange-A", cells.Keys);
                Assert.DoesNotContain("BigJank 发生率-A", cells.Keys);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760)))
                {
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, "fps-table-1180.png"), PngBitmapEncoderOptions.Default);
                }

                MetricButton(workspace.SectionForTest("fps"), "1%Low(FPS)")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                window.UpdateLayout();
                cells = ReportCells(workspace.SectionForTest("fps"));
                Assert.Equal(new[] { "FPS 1% Low-A", "FPS 1% Low-B" }, cells.Keys.OrderBy(key => key, StringComparer.Ordinal));
                Assert.Equal("--", cells["FPS 1% Low-A"]);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760)))
                {
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, "fps-1low-table-1180.png"), PngBitmapEncoderOptions.Default);
                }

                MetricButton(workspace.SectionForTest("fps"), "BigJank")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                window.UpdateLayout();
                cells = ReportCells(workspace.SectionForTest("fps"));
                Assert.Equal(new[] { "BigJank 发生率-A", "BigJank 发生率-B" }, cells.Keys.OrderBy(key => key, StringComparer.Ordinal));
                Assert.NotEqual("--", cells["BigJank 发生率-A"]);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760)))
                {
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, "fps-bigjank-table-1180.png"), PngBitmapEncoderOptions.Default);
                }

                MetricButton(workspace.SectionForTest("fps"), "Jank")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("fps"));
                Assert.Contains("Jank 发生率（不含 BigJank）-A", cells.Keys);
                Assert.DoesNotContain("BigJank 发生率-A", cells.Keys);

                MetricButton(workspace.SectionForTest("fps"), "Stutter")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("fps"));
                Assert.Contains("卡顿时长占比-A", cells.Keys);
                Assert.DoesNotContain("Jank 发生率（不含 BigJank）-A", cells.Keys);

                MetricButton(workspace.SectionForTest("fps"), "Smooth")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("fps"));
                Assert.Equal("--", cells["Smooth-A"]);
                Assert.Equal("--", cells["Smooth-B"]);

                MetricButton(workspace.SectionForTest("fps"), "SmallJank")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("fps"));
                Assert.Equal("--", cells["SmallJank 发生率-A"]);
                Assert.Equal("--", cells["SmallJank 发生率-B"]);

                workspace.TabButtonForTest("frametime").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("frametime"));
                Assert.Contains("FrameTime Max-A", cells.Keys);
                MetricButton(workspace.SectionForTest("frametime"), "FrameTime Mean")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("frametime"));
                Assert.Contains("FrameTime-A", cells.Keys);
                Assert.DoesNotContain("FrameTime Max-A", cells.Keys);
                MetricButton(workspace.SectionForTest("frametime"), "FrameTime P95")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("frametime"));
                Assert.Contains("FrameTime P95-A", cells.Keys);
                Assert.DoesNotContain("FrameTime-A", cells.Keys);

                workspace.TabButtonForTest("cpu").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                MetricButton(workspace.SectionForTest("cpu"), "进程 CPU Normalized")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("cpu"));
                Assert.Contains("进程 CPU Normalized-A", cells.Keys);
                Assert.DoesNotContain("进程 CPU Raw-A", cells.Keys);
                MetricButton(workspace.SectionForTest("cpu"), "设备核心 0")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("cpu"));
                Assert.Contains("设备 CPU 0-A", cells.Keys);
                Assert.DoesNotContain("进程 CPU Normalized-A", cells.Keys);

                workspace.TabButtonForTest("memory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                MetricButton(workspace.SectionForTest("memory"), "PSS")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("memory"));
                Assert.Contains("进程内存 / pss-B", cells.Keys);
                Assert.DoesNotContain("进程内存 / physical_footprint-A", cells.Keys);

                workspace.TabButtonForTest("temperature").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                MetricButton(workspace.SectionForTest("temperature"), "NPU")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("temperature"));
                Assert.Contains("温度 / NPU-A", cells.Keys);
                Assert.DoesNotContain("温度 / Battery-A", cells.Keys);

                workspace.TabButtonForTest("thermal").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                MetricButton(workspace.SectionForTest("thermal"), "Android")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                cells = ReportCells(workspace.SectionForTest("thermal"));
                Assert.Contains("热状态 / android（最高）-B", cells.Keys);
                Assert.DoesNotContain("热状态 / ios（最高）-A", cells.Keys);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task LanguageToggleTranslatesMetricDisplayWithoutChangingDataOrIds() => OnUi(async () =>
        {
            string previousTheme = AppThemeManager.Current.Id;
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(CurveDocument("A 应用", "ios", 20));
                await workspace.ReplaceAsync(1, new ComparisonInput
                {
                    Name = "B 应用", Document = CurveDocument("B 应用", "android", 50)
                });
                workspace.TabButtonForTest("fps").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveCategoryForTest == "fps");
                MetricButton(workspace.SectionForTest("fps"), "BigJank")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveMetricForTest == "bigjank");
                window.UpdateLayout();

                Button toggle = workspace.FindControl<Button>("LanguageToggleButton");
                Assert.NotNull(toggle);
                Assert.Equal("中文", toggle.Content);
                Assert.Equal("FPS", workspace.TabButtonForTest("fps").Content);
                Assert.Equal("FrameTime / Jank", workspace.TabButtonForTest("frametime").Content);
                ComparisonReport report = workspace.ReportForTest;
                IReadOnlyDictionary<string, string> englishCells = ReportCells(workspace.SectionForTest("fps"));
                Assert.Contains("BigJank 发生率-A", englishCells.Keys);
                Assert.Contains("BigJank 发生率-B", englishCells.Keys);

                string output = Path.Combine(ScreenshotDirectory(), "integrated-language-toggle-v0530");
                Directory.CreateDirectory(output);
                void SaveLanguageScreenshots(string language)
                {
                    foreach (string theme in new[] { "dark", "light", "retro-sage" })
                    {
                        AppThemeManager.Apply(Application.Current, theme, false);
                        window.UpdateLayout();
                        using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                        bitmap.Render(window);
                        bitmap.Save(Path.Combine(output, language + "-" + theme + "-1180.png"), PngBitmapEncoderOptions.Default);
                    }
                }

                SaveLanguageScreenshots("english");
                AppThemeManager.Apply(Application.Current, previousTheme, false);
                window.UpdateLayout();

                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Equals(toggle.Content, "English") && workspace.ActiveCategoryForTest == "fps");
                window.UpdateLayout();

                Assert.Equal("概览", workspace.TabButtonForTest("overview").Content);
                Assert.Equal("帧率", workspace.TabButtonForTest("fps").Content);
                Assert.Equal("帧耗时 / 卡顿", workspace.TabButtonForTest("frametime").Content);
                Assert.Equal("处理器", workspace.TabButtonForTest("cpu").Content);
                Assert.Equal("内存", workspace.TabButtonForTest("memory").Content);
                Assert.Equal("温度", workspace.TabButtonForTest("temperature").Content);
                Assert.Equal("系统热状态", workspace.TabButtonForTest("thermal").Content);

                Border section = workspace.SectionForTest("fps");
                CheckBox timeTrendToggle = section.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.Name == "TimeTrendToggle-fps");
                Assert.Equal("相同时间趋势", timeTrendToggle.Content);
                Assert.False(timeTrendToggle.IsChecked == true);
                Assert.Equal(new[] { "帧率", "稳帧指数", "1% 低帧率", "轻微卡顿", "卡顿", "严重卡顿", "卡顿时长占比" },
                    section.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("metricChip"))
                        .Select(button => button.Content as string).ToArray());
                Assert.Equal("fps", workspace.ActiveCategoryForTest);
                Assert.Equal("bigjank", workspace.ActiveMetricForTest);
                Assert.Same(report, workspace.ReportForTest);
                Assert.Contains("严重卡顿（次/10分钟）", section.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
                Assert.Equal("严重卡顿", section.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "ChartTitle-bigjank").Text);
                IReadOnlyDictionary<string, string> chineseCells = ReportCells(section);
                Assert.Equal(englishCells["BigJank 发生率-A"], chineseCells["BigJank 发生率-A"]);
                Assert.DoesNotContain("严重卡顿-A", chineseCells.Keys);

                SaveLanguageScreenshots("chinese");
                AppThemeManager.Apply(Application.Current, previousTheme, false);
                window.UpdateLayout();

                MetricButton(section, "帧率").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveMetricForTest == "fps");
                window.UpdateLayout();
                section = workspace.SectionForTest("fps");
                Assert.Contains("平均帧率", section.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
                Assert.Equal("帧率", section.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "ChartTitle-fps").Text);
                Assert.Contains("FPS-A", ReportCells(section).Keys);
                Assert.Contains("FPS Std-A", ReportCells(section).Keys);

                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Equals(toggle.Content, "中文") && workspace.ActiveCategoryForTest == "fps");
                window.UpdateLayout();
                section = workspace.SectionForTest("fps");
                Assert.Equal("FPS", workspace.TabButtonForTest("fps").Content);
                Assert.Equal("FrameTime / Jank", workspace.TabButtonForTest("frametime").Content);
                Assert.Equal("fps", workspace.ActiveMetricForTest);
                Assert.NotNull(MetricButton(section, "FPS"));
                Assert.Equal("FPS", section.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "ChartTitle-fps").Text);
                Assert.Contains("Avg(FPS)", section.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
                Assert.Same(report, workspace.ReportForTest);
                Assert.Equal("2 / 5", workspace.FindControl<TextBlock>("SlotSummaryText").Text);
            }
            finally
            {
                window.Close();
                AppThemeManager.Apply(Application.Current, previousTheme, false);
            }
        });

        [Fact]
        public Task OverviewTableFreezesSessionColumnAndKeepsScrollBarBelowRows() => OnUi(async () =>
        {
            string previousTheme = AppThemeManager.Current.Id;
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(OverviewStatsDocument("A 应用", "ios"));
                await workspace.ReplaceAsync(1, new ComparisonInput
                {
                    Name = "B 应用",
                    Document = OverviewStatsDocument("B 应用", "android")
                });
                workspace.TabButtonForTest("overview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveCategoryForTest == "overview");
                window.UpdateLayout();

                Border section = workspace.SectionForTest("overview");
                ScrollViewer metricScroll = section.GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(scroll => scroll.Name == "MetricTableScroll");
                ScrollBar horizontalBar = section.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(scrollBar => scrollBar.Name == "MetricTableScrollBar");
                Grid frozenColumn = section.GetVisualDescendants().OfType<Grid>()
                    .Single(grid => grid.Name == "FrozenSessionColumn");
                Border sessionHeader = section.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "SessionHeaderCell");
                Assert.Equal("现场", ((TextBlock)sessionHeader.Child).Text);

                await Until(() => horizontalBar.IsVisible && horizontalBar.Maximum > 100);
                window.UpdateLayout();
                Point sectionOrigin = sessionHeader.TranslatePoint(new Point(0, 0), section).Value;
                Point scrollOrigin = metricScroll.TranslatePoint(new Point(0, 0), section).Value;
                Point barOrigin = horizontalBar.TranslatePoint(new Point(0, 0), section).Value;
                Assert.True(barOrigin.Y >= scrollOrigin.Y + metricScroll.Bounds.Height - 1);
                Assert.True(frozenColumn.Bounds.Width >= 204);

                horizontalBar.Value = 0;
                window.UpdateLayout();
                TextBlock firstMetricHeader = section.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => string.Equals(text.Text, "Avg(FPS)", StringComparison.Ordinal));
                Point firstMetricOrigin = firstMetricHeader.TranslatePoint(new Point(0, 0), section).Value;
                Assert.True(firstMetricOrigin.X >= scrollOrigin.X - 0.1);

                horizontalBar.Value = Math.Min(horizontalBar.Maximum, 180);
                window.UpdateLayout();
                Point afterScroll = sessionHeader.TranslatePoint(new Point(0, 0), section).Value;
                Assert.InRange(Math.Abs(afterScroll.X - sectionOrigin.X), 0, 0.1);
                Assert.InRange(Math.Abs(afterScroll.Y - sectionOrigin.Y), 0, 0.1);
                Assert.InRange(metricScroll.Offset.X, 179, 181);

                string output = Path.Combine(ScreenshotDirectory(), "overview-table-freeze-v0522");
                Directory.CreateDirectory(output);
                foreach (string theme in new[] { "dark", "light", "retro-sage" })
                {
                    AppThemeManager.Apply(Application.Current, theme, false);
                    horizontalBar.Value = 0;
                    window.UpdateLayout();
                    using (var initial = new RenderTargetBitmap(new PixelSize(1180, 760)))
                    {
                        initial.Render(window);
                        initial.Save(Path.Combine(output, theme + "-initial-1180.png"), PngBitmapEncoderOptions.Default);
                    }

                    horizontalBar.Value = Math.Min(horizontalBar.Maximum, 180);
                    window.UpdateLayout();
                    using var scrolled = new RenderTargetBitmap(new PixelSize(1180, 760));
                    scrolled.Render(window);
                    scrolled.Save(Path.Combine(output, theme + "-scrolled-1180.png"), PngBitmapEncoderOptions.Default);
                }
            }
            finally
            {
                window.Close();
                AppThemeManager.Apply(Application.Current, previousTheme, false);
            }
        });

        [Fact]
        public Task TemperatureMetricSwitchKeepsCurrentScrollPosition() => OnUi(async () =>
        {
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(CurveDocument("A 应用", "ios", 20));
                await workspace.ReplaceAsync(1, new ComparisonInput { Name = "B 应用", Document = CurveDocument("B 应用", "android", 50) });

                var scroll = workspace.FindControl<ScrollViewer>("ContentScroll");
                workspace.TabButtonForTest("cpu").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveCategoryForTest == "cpu" && scroll.Offset.Y > 1);

                window.UpdateLayout();
                Point? temperaturePoint = workspace.SectionForTest("temperature").TranslatePoint(new Point(0, 0), scroll);
                Assert.True(temperaturePoint.HasValue);
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y + temperaturePoint.Value.Y - 38));
                window.UpdateLayout();
                await Task.Delay(30);

                var npu = workspace.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "NPU"));
                double before = scroll.Offset.Y;
                npu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveMetricForTest == "temperature-NPU");
                window.UpdateLayout();

                Assert.Equal("temperature", workspace.ActiveCategoryForTest);
                Assert.InRange(Math.Abs(scroll.Offset.Y - before), 0, 4);
                Point? remainingPoint = workspace.SectionForTest("temperature").TranslatePoint(new Point(0, 0), scroll);
                Assert.True(remainingPoint.HasValue);
                Assert.InRange(remainingPoint.Value.Y, -4, 44);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task ActiveAnalysisTextUsesReadableDarkThemeColors() => OnUi(async () =>
        {
            string previousTheme = AppThemeManager.Current.Id;
            AppThemeManager.Apply(Application.Current, "dark", false);
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(CurveDocument("A 应用", "ios", 20));
                await workspace.ReplaceAsync(1, new ComparisonInput { Name = "B 应用", Document = CurveDocument("B 应用", "android", 50) });

                workspace.TabButtonForTest("fps").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveCategoryForTest == "fps");
                var bigJank = workspace.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "BigJank"));
                bigJank.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.ActiveMetricForTest == "bigjank");
                window.UpdateLayout();

                Color expectedForeground = Color.Parse(AppThemeManager.ColorValue("Theme.TextPrimary"));
                Color expectedBackground = Color.Parse(AppThemeManager.ColorValue("Theme.SelectedBackground"));
                var activeTab = workspace.TabButtonForTest("fps");
                var activeMetric = workspace.GetVisualDescendants().OfType<Button>().Single(button =>
                    Equals(button.Content, "BigJank") && button.Classes.Contains("activeMetricChip"));
                string output = Path.Combine(ScreenshotDirectory(), "dark-theme-selection-v0512");
                Directory.CreateDirectory(output);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760)))
                {
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, "fps-bigjank-dark-1180.png"), PngBitmapEncoderOptions.Default);
                }
                Assert.Equal(expectedForeground, ((ISolidColorBrush)activeTab.Foreground).Color);
                Assert.Equal(expectedForeground, ((ISolidColorBrush)activeMetric.Foreground).Color);
                Assert.Equal(expectedBackground, ((ISolidColorBrush)activeMetric.Background).Color);
            }
            finally
            {
                window.Close();
                AppThemeManager.Apply(Application.Current, previousTheme, false);
            }
        });

        [Fact]
        public Task IntegratedWorkspaceAdaptsAcrossThemesAndSupportedWidths() => OnUi(async () =>
        {
            string output = Path.Combine(ScreenshotDirectory(), "integrated-curve-analysis-v0510", "matrix");
            Directory.CreateDirectory(output);
            foreach ((int width, int height) in new[] { (1180, 760), (1500, 940) })
            {
                var workspace = new SessionComparisonWorkspace();
                var window = new Window { Content = workspace, Width = width, Height = height };
                window.Show();
                try
                {
                    await workspace.InitializeAsync(CurveDocument("iPhone 17 Pro Max · 逃离101 · 长时发热", "ios", 24));
                    await workspace.ReplaceAsync(1, new ComparisonInput
                    {
                        Name = "Android 旗舰 · 逃离101 · 横屏高负载",
                        Document = CurveDocument("Android 旗舰 · 逃离101 · 横屏高负载", "android", 38)
                    });
                    for (int slot = 2; slot < 5; slot++)
                    {
                        await workspace.ReplaceAsync(slot, new ComparisonInput
                        {
                        Name = "长名称对比现场 " + (slot + 1) + " · 连续采集与温度变化",
                        Document = CurveDocument("长名称对比现场 " + (slot + 1) + " · 连续采集与温度变化",
                            slot % 2 == 0 ? "ios" : "android", 30 + slot)
                        });
                    }

                    foreach (string theme in new[] { "dark", "light", "retro-sage" })
                    {
                        AppThemeManager.Apply(Application.Current, theme, false);
                        ComparisonChartControl.RefreshThemeResources();
                        workspace.SetCategoryForTest("overview");
                        window.UpdateLayout();
                        var panel = workspace.FindControl<UniformGrid>("SessionCardsPanel");
                        Assert.True(panel.Bounds.Width > width - 60);
                        Assert.All(Enumerable.Range(0, 5), slot => Assert.True(workspace.SlotCardForTest(slot).Bounds.Width > 180));
                        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
                        bitmap.Render(window);
                        bitmap.Save(Path.Combine(output, theme + "-" + width + "-overview.png"), PngBitmapEncoderOptions.Default);
                    }

                    if (width == 1180)
                    {
                        AppThemeManager.Apply(Application.Current, "dark", false);
                        workspace.SetCategoryForTest("temperature");
                        window.UpdateLayout();
                        using (var bitmap = new RenderTargetBitmap(new PixelSize(width, height)))
                        {
                            bitmap.Render(window);
                            bitmap.Save(Path.Combine(output, "dark-1180-temperature.png"), PngBitmapEncoderOptions.Default);
                        }
                        workspace.SetCategoryForTest("thermal");
                        window.UpdateLayout();
                        using (var bitmap = new RenderTargetBitmap(new PixelSize(width, height)))
                        {
                            bitmap.Render(window);
                            bitmap.Save(Path.Combine(output, "dark-1180-thermal.png"), PngBitmapEncoderOptions.Default);
                        }
                    }
                }
                finally { window.Close(); }
            }
        });

        [Fact]
        public Task IntegratedWorkspaceOverviewShowsPerfDogStatisticsAndReliableCoverage() => OnUi(async () =>
        {
            var workspace = new SessionComparisonWorkspace();
            var window = new Window { Content = workspace, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await workspace.InitializeAsync(OverviewStatsDocument("iOS 现场", "ios"));
                await workspace.ReplaceAsync(1, new ComparisonInput
                {
                    Name = "Android 现场", Document = OverviewStatsDocument("Android 现场", "android")
                });
                window.UpdateLayout();

                IReadOnlyList<string> labels = workspace.OverviewLabelsForTest;
                Assert.Contains("FPS 1% Low", labels);
                Assert.Contains("FPS Std", labels);
                Assert.Contains("FrameTime >=100ms", labels);
                Assert.Contains("FrameTime Delta>100ms/h", labels);
                Assert.Contains("TinyJank 发生率", labels);
                Assert.Contains("SmallJank 发生率", labels);
                Assert.Contains("进程内存 / physical_footprint", labels);
                Assert.Contains("进程内存 / pss", labels);
                Assert.Contains("热状态 / android（最高）", labels);
                Assert.DoesNotContain("热状态 / ios（最高）", labels);

                ComparisonRow onePercentLow = workspace.ReportForTest.Rows.Single(row => row.Label == "FPS 1% Low");
                Assert.Equal("9.01", onePercentLow.Values[0]);
                Assert.Equal("9.01", onePercentLow.Values[1]);
                Assert.Equal("31.50", workspace.ReportForTest.Rows.Single(row => row.Label == "进程 CPU Raw").Values[0]);
                Assert.Equal("180.00", workspace.ReportForTest.Rows.Single(row => row.Label == "进程内存 / physical_footprint").Values[0]);
                Assert.Equal("210.00", workspace.ReportForTest.Rows.Single(row => row.Label == "进程内存 / pss").Values[1]);
                Assert.Equal("36.00", workspace.ReportForTest.Rows.Single(row => row.Label == "温度 / Battery").Values[0]);
                Assert.Equal("4.00", workspace.ReportForTest.Rows.Single(row => row.Label == "热状态 / android（最高）").Values[1]);
                Assert.Equal("--", workspace.ReportForTest.Rows.Single(row => row.Label == "TinyJank 发生率").Values[0]);
                var overviewSection = workspace.SectionForTest("overview");
                Assert.Equal("31.50", overviewSection.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => Avalonia.Automation.AutomationProperties.GetAutomationId(text) == "进程 CPU Raw-A").Text);
                Assert.Equal("180.00", overviewSection.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => Avalonia.Automation.AutomationProperties.GetAutomationId(text) == "进程内存 / physical_footprint-A").Text);
                Assert.Equal("4.00", overviewSection.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => Avalonia.Automation.AutomationProperties.GetAutomationId(text) == "热状态 / android（最高）-B").Text);

                ScrollViewer overviewTable = overviewSection
                    .GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(scroll => scroll.Name == "MetricTableScroll");
                ScrollBar overviewBar = overviewSection
                    .GetVisualDescendants().OfType<ScrollBar>()
                    .Single(scrollBar => scrollBar.Name == "MetricTableScrollBar");
                Assert.True(overviewTable.Extent.Width > overviewTable.Viewport.Width);
                Assert.True(overviewBar.IsVisible);

                string output = Path.Combine(ScreenshotDirectory(), "integrated-curve-analysis-v0513");
                Directory.CreateDirectory(output);
                using (var leftBitmap = new RenderTargetBitmap(new PixelSize(1180, 760)))
                {
                    leftBitmap.Render(window);
                    leftBitmap.Save(Path.Combine(output, "overview-left-verify-1180.png"), PngBitmapEncoderOptions.Default);
                }

                overviewBar.Value = overviewBar.Maximum;
                window.UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(output, "overview-right-verify-1180.png"), PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task MainTitleBarFitsAndOpensIntegratedCurveAnalysis() => OnUi(async () =>
        {
            string previousTheme = AppThemeManager.Current.Id;
            var window = new MainWindow { Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await Task.Delay(30);
                window.UpdateLayout();
                var curveButton = window.FindControl<Button>("CurveAnalysisButton");
                var curveButtonText = window.FindControl<TextBlock>("CurveAnalysisButtonText");
                var versionText = window.FindControl<TextBlock>("VersionText");
                var titleBarHeader = (StackPanel)curveButton.Parent;
                var titleBar = (Grid)titleBarHeader.Parent;
                var status = titleBar.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 1);
                Assert.All(titleBar.Children, child => Assert.True(child.Bounds.Right <= titleBar.Bounds.Width + 1));
                Assert.Same(titleBarHeader, versionText.Parent);
                Assert.True(curveButton.Bounds.Left >= versionText.Bounds.Right);
                Assert.True(titleBarHeader.Bounds.Right <= status.Bounds.Left + 1);
                Assert.Equal("曲线分析", curveButtonText.Text);
                Assert.Contains("curveAnalysisButton", curveButton.Classes);
                Assert.DoesNotContain("themeSelectorButton", curveButton.Classes);
                Assert.Equal("数据显示", window.FindControl<TextBlock>("DataDisplayTitle").Text);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button =>
                    button.Content is StackPanel stack && stack.Children.OfType<TextBlock>().Any(text => text.Text == "对比分析"));

                string output = Path.Combine(ScreenshotDirectory(), "integrated-curve-analysis-v0521");
                Directory.CreateDirectory(output);
                foreach (string theme in new[] { "dark", "light", "retro-sage" })
                {
                    AppThemeManager.Apply(Application.Current, theme, false);
                    window.UpdateLayout();
                    Assert.All(titleBar.Children, child => Assert.True(child.Bounds.Right <= titleBar.Bounds.Width + 1));
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, "main-" + theme + "-1180.png"), PngBitmapEncoderOptions.Default);
                }

                curveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var workspace = window.FindControl<SessionComparisonWorkspace>("CurveAnalysisWorkspace");
                await Until(() => workspace.IsVisible && !workspace.IsBusy);
                Assert.Equal("返回性能监控", curveButtonText.Text);
                Assert.Null(workspace.FindControl<Button>("BackButton"));
                Assert.DoesNotContain(workspace.GetVisualDescendants().OfType<Button>(), button =>
                    string.Equals(button.Content as string, "返回性能监控", StringComparison.Ordinal));
                Assert.False(window.FindControl<Border>("MainToolbar").IsVisible);
                Assert.False(window.FindControl<Grid>("MainContent").IsVisible);
                window.UpdateLayout();
                foreach (string theme in new[] { "dark", "light", "retro-sage" })
                {
                    AppThemeManager.Apply(Application.Current, theme, false);
                    window.UpdateLayout();
                    Assert.Equal("返回性能监控", curveButtonText.Text);
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, "analysis-" + theme + "-1180.png"), PngBitmapEncoderOptions.Default);
                }

                curveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !workspace.IsVisible);
                Assert.Equal("曲线分析", curveButtonText.Text);
                Assert.True(window.FindControl<Border>("MainToolbar").IsVisible);
                Assert.True(window.FindControl<Grid>("MainContent").IsVisible);
            }
            finally
            {
                window.Close();
                AppThemeManager.Apply(Application.Current, previousTheme, false);
            }
        });
    }
}
