using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
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
        public Task MainToolbarFitsAndOpensComparison() => OnUi(async () =>
        {
            var window = new MainWindow { Width = 1180, Height = 760 };
            window.Show();
            try
            {
                await Task.Delay(30);
                window.UpdateLayout();
                var compare = window.GetVisualDescendants().OfType<Button>().Single(button =>
                    button.Content is StackPanel stack && stack.Children.OfType<TextBlock>().Any(text => text.Text == "对比分析"));
                var toolbarActions = (StackPanel)compare.Parent;
                var toolbar = (Grid)toolbarActions.Parent;
                Assert.All(toolbar.Children, child => Assert.True(child.Bounds.Right <= toolbar.Bounds.Width + 1));
                Assert.True(toolbarActions.Bounds.Right <= toolbar.Children[1].Bounds.Left + 1);
                string output = Path.Combine(ScreenshotDirectory(), "comparison-v0470");
                Directory.CreateDirectory(output);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760)))
                {
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, "main-1180.png"), PngBitmapEncoderOptions.Default);
                }
                compare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => window.OwnedWindows.OfType<SessionComparisonWindow>().Any());
                window.OwnedWindows.OfType<SessionComparisonWindow>().Single().Close();
            }
            finally { window.Close(); }
        });
    }
}
