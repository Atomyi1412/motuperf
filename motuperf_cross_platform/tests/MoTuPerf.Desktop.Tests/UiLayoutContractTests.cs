using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class UiLayoutContractTests
    {
        private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        [Fact]
        public void MainWorkstationKeepsReferenceGeometryAndExplicitTabTextStates()
        {
            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement window = main.Root;

            Assert.Equal("1500", Attribute(window, "Width"));
            Assert.Equal("940", Attribute(window, "Height"));
            Assert.Equal("1180", Attribute(window, "MinWidth"));
            Assert.Equal("760", Attribute(window, "MinHeight"));

            XElement liveTab = Named(main, "LiveDataTabButton");
            XElement selectedTab = Named(main, "SelectedDataTabButton");
            Assert.Contains("activeTab", Attribute(liveTab, "Classes"));
            Assert.DoesNotContain("activeTab", Attribute(selectedTab, "Classes"));
            Assert.Equal("dataTabText", Attribute(liveTab.Elements().Single(), "Classes"));
            Assert.Equal("dataTabText", Attribute(selectedTab.Elements().Single(), "Classes"));

            Assert.Contains(main.Descendants(Avalonia + "Grid"), delegate(XElement grid)
            {
                return Attribute(grid, "ColumnDefinitions") == "36,48,*"
                    && Attribute(grid, "Margin") == "14,12";
            });

            XDocument app = LoadXaml("src", "MoTuPerf.Desktop", "App.axaml");
            AssertStyle(app, "Button.dataTab.activeTab", "BorderBrush", "{DynamicResource Theme.Accent}");
            AssertStyle(app, "Button.dataTab.activeTab TextBlock.dataTabText", "Foreground", "{DynamicResource Theme.TextPrimary}");
            AssertStyle(app, "Button.dataTab:pointerover TextBlock.dataTabText", "Foreground", "{DynamicResource Theme.TextPrimary}");

            Assert.DoesNotContain(main.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text") == "统计指标");
            Assert.DoesNotContain(main.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text").StartsWith("勾选指标后", StringComparison.Ordinal));
            XElement parametersScrollViewer = Named(main, "ParametersScrollViewer");
            Assert.Equal("2", Attribute(parametersScrollViewer, "Grid.Row"));
            Assert.Equal("0,0,0,20", Attribute(parametersScrollViewer, "Margin"));
            XElement parametersContent = Named(main, "ParametersExpandedContent");
            Assert.Equal("48,Auto,*", Attribute(parametersContent, "RowDefinitions"));
            Assert.Equal("{Binding !IsParametersCollapsed}", Attribute(parametersContent, "IsVisible"));
            Assert.Contains(parametersContent.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text") == "参数面板");
            Assert.Contains(parametersContent.Descendants(Avalonia + "Border"), element => Attribute(element, "Classes") == "parameterHeaderDivider");
            XElement dataPanel = Named(main, "ParameterDataPanel");
            Assert.Equal(parametersContent, dataPanel.Parent);
            Assert.Equal("1", Attribute(dataPanel, "Grid.Row"));
            Assert.Equal("14,20,14,12", Attribute(dataPanel, "Margin"));
            Assert.DoesNotContain(parametersScrollViewer.Descendants(Avalonia + "Border"), element => Attribute(element, "Classes") == "dataPanel");
            Assert.Contains(parametersScrollViewer.Descendants(Avalonia + "ItemsControl"), element => Attribute(element, "ItemsSource") == "{Binding Metrics}");
            Assert.Empty(parametersScrollViewer.Descendants(Avalonia + "ScrollViewer"));
            XElement metrics = parametersScrollViewer.Descendants(Avalonia + "ItemsControl")
                .Single(element => Attribute(element, "ItemsSource") == "{Binding Metrics}");
            Assert.Equal("14,0,28,0", Attribute(metrics, "Margin"));
            Assert.DoesNotContain(metrics.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text") == "◉");
            Assert.All(main.Descendants(Avalonia + "UniformGrid").Where(element => Attribute(element, "Columns") == "2"), element =>
            {
                Assert.Equal("8", Attribute(element, "ColumnSpacing"));
                Assert.Equal("6", Attribute(element, "RowSpacing"));
            });
            Assert.Equal("captureButtonText", Attribute(main.Descendants(Avalonia + "TextBlock").Single(element => Attribute(element, "Text") == "{Binding CaptureLabel}"), "Classes"));
            AssertStyle(app, "TextBlock.captureStartIcon", "Foreground", "{DynamicResource Theme.Accent}");
            AssertStyle(app, "TextBlock.captureButtonText", "Foreground", "{DynamicResource Theme.TextPrimary}");
            AssertStyle(app, "Button.captureToolbarButton:pointerover TextBlock.captureButtonText", "Foreground", "{DynamicResource Theme.TextPrimary}");
            AssertStyle(app, "ScrollBar", "AllowAutoHide", "False");
            AssertStyle(app, "ScrollBar:horizontal", "Height", "12");
            AssertStyle(app, "ScrollBar:horizontal /template/ Grid#Root", "Background", "{DynamicResource Theme.ScrollTrack}");
            AssertStyle(app, "ScrollBar:horizontal /template/ Border#HorizontalRoot", "Background", "{DynamicResource Theme.ScrollTrack}");
            AssertStyle(app, "ScrollBar:horizontal /template/ Rectangle#TrackRect", "Fill", "{DynamicResource Theme.ScrollTrack}");
            AssertStyle(app, "ScrollBar:horizontal[IsExpanded=true] /template/ Rectangle#TrackRect", "Fill", "{DynamicResource Theme.ScrollTrack}");
            AssertStyle(app, "ScrollBar:horizontal /template/ RepeatButton#PART_LineUpButton", "Width", "0");
            AssertStyle(app, "ScrollBar:horizontal /template/ RepeatButton#PART_LineDownButton", "Width", "0");
            AssertStyle(app, "ScrollBar:horizontal /template/ Thumb", "RenderTransform", "none");
            XElement horizontalThumbStyle = app.Descendants(Avalonia + "Style")
                .Single(element => Attribute(element, "Selector") == "ScrollBar:horizontal /template/ Thumb");
            Assert.DoesNotContain(horizontalThumbStyle.Elements(Avalonia + "Setter"), element => Attribute(element, "Property") == "Margin");
            AssertStyle(app, "ScrollBar:horizontal /template/ Thumb /template/ Border", "Background", "{DynamicResource Theme.ScrollThumb}");
            AssertStyle(app, "ScrollBar:horizontal /template/ Thumb /template/ Border", "Margin", "2,3");
            AssertStyle(app, "ScrollBar:horizontal /template/ Thumb:pointerover /template/ Border", "Background", "{DynamicResource Theme.ScrollThumbHover}");
            AssertStyle(app, "ScrollBar:vertical", "Width", "14");
            AssertStyle(app, "ScrollBar:vertical /template/ Grid#Root", "Background", "{DynamicResource Theme.ScrollTrack}");
            AssertStyle(app, "ScrollBar:vertical /template/ Border#VerticalRoot", "Background", "{DynamicResource Theme.ScrollTrack}");
            AssertStyle(app, "ScrollBar:vertical /template/ Rectangle#TrackRect", "Fill", "{DynamicResource Theme.ScrollTrack}");
            AssertStyle(app, "ScrollBar:vertical[IsExpanded=true] /template/ Rectangle#TrackRect", "Fill", "{DynamicResource Theme.ScrollTrack}");
            AssertStyle(app, "ScrollBar:vertical /template/ Thumb", "RenderTransform", "none");
            XElement verticalThumbStyle = app.Descendants(Avalonia + "Style")
                .Single(element => Attribute(element, "Selector") == "ScrollBar:vertical /template/ Thumb");
            Assert.DoesNotContain(verticalThumbStyle.Elements(Avalonia + "Setter"), element => Attribute(element, "Property") == "Margin");
            AssertStyle(app, "ScrollBar:vertical /template/ Thumb /template/ Border", "Background", "{DynamicResource Theme.ScrollThumb}");
            AssertStyle(app, "ScrollBar:vertical /template/ Thumb /template/ Border", "Margin", "3,2");

            XElement checkBoxStyle = app.Descendants(Avalonia + "Style")
                .Single(element => Attribute(element, "Selector") == "CheckBox");
            XElement checkBoxTemplate = checkBoxStyle.Descendants(Avalonia + "ControlTemplate").Single();
            XElement checkBoxSurface = checkBoxTemplate.Descendants(Avalonia + "Border")
                .Single(element => (string)element.Attribute(Xaml + "Name") == "PART_CheckBox");
            Assert.Equal("16", Attribute(checkBoxSurface, "Width"));
            Assert.Equal("16", Attribute(checkBoxSurface, "Height"));
            Assert.Equal("{DynamicResource Theme.CheckboxBackground}", Attribute(checkBoxSurface, "Background"));
            Assert.Equal("{DynamicResource Theme.BorderStrong}", Attribute(checkBoxSurface, "BorderBrush"));
            Assert.Equal("0", Attribute(checkBoxSurface, "CornerRadius"));
            XElement checkGlyph = checkBoxTemplate.Descendants(Avalonia + "Path")
                .Single(element => (string)element.Attribute(Xaml + "Name") == "PART_CheckGlyph");
            Assert.Equal("{DynamicResource Theme.CheckboxGlyph}", Attribute(checkGlyph, "Fill"));
            Assert.Equal("0", Attribute(checkGlyph, "Opacity"));
            AssertStyle(app, "CheckBox:checked /template/ Path#PART_CheckGlyph", "Opacity", "1");
            AssertStyle(app, "CheckBox:pointerover /template/ Border#PART_CheckBox", "Background", "{DynamicResource Theme.CheckboxHover}");
            AssertStyle(app, "CheckBox:pressed /template/ Border#PART_CheckBox", "Background", "{DynamicResource Theme.CheckboxPressed}");

            Assert.Equal("Vertical", Attribute(Named(main, "FpsLegendPanel"), "Orientation"));
            Assert.Equal("Vertical", Attribute(Named(main, "CoreCpuLegendPanel"), "Orientation"));
            Assert.Equal("Vertical", Attribute(Named(main, "TemperatureLegendPanel"), "Orientation"));
            XElement thermalChart = Named(main, "ThermalStateChart");
            Assert.Equal("{Binding ThermalAxisMax}", Attribute(thermalChart, "ThermalStateMax"));
            Assert.Equal("{Binding ThermalMetricTitle}", Attribute(thermalChart, "ThermalStateTitle"));
            Assert.Equal("{Binding ThermalMetricLegend}", Attribute(thermalChart, "ThermalStateLegend"));
            Assert.Equal("{Binding ThermalWaitingText}", Attribute(thermalChart, "ThermalStateWaitingText"));
            Assert.Contains(main.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text") == "{Binding ThermalMetricTitle}");
            Assert.Contains(main.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text") == "{Binding ThermalMetricLegend}");
            Assert.True(main.Descendants(Avalonia + "Border").Count(element => Attribute(element, "Classes") == "chartLegendSwatch") >= 5);
            Assert.DoesNotContain(main.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text").StartsWith("━", StringComparison.Ordinal));
            XElement deviceInfoOverlay = Named(main, "DeviceInfoOverlay");
            Assert.Equal("Right", Attribute(deviceInfoOverlay, "HorizontalAlignment"));
            Assert.Equal("Bottom", Attribute(deviceInfoOverlay, "VerticalAlignment"));
            Assert.Equal("4", Attribute(deviceInfoOverlay, "Grid.RowSpan"));
            Assert.Equal("{Binding IsDeviceInfoVisible}", Attribute(Named(main, "DeviceInfoPanel"), "IsVisible"));
            Assert.Equal("{Binding DeviceInfoText}", Attribute(Named(main, "DeviceInfoText"), "Text"));
            XElement deviceInfoButton = Named(main, "DeviceInfoButton");
            Assert.Contains("deviceInfoButton", Attribute(deviceInfoButton, "Classes"));
            Assert.Equal("ToggleDeviceInfo", Attribute(deviceInfoButton, "Click"));
            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");
            Assert.Contains("Width = 11", windowCode);
            Assert.Contains("Height = 11", windowCode);
            Assert.DoesNotContain("Text = \"━ \" + label", windowCode);
        }

        [Fact]
        public void DevicePickerKeepsWpfReferenceGeometryAndCompactProcessTable()
        {
            XDocument dialog = LoadXaml("src", "MoTuPerf.Desktop", "DevicePickerWindow.axaml");
            XElement window = dialog.Root;

            Assert.Equal("680", Attribute(window, "Width"));
            Assert.Equal("640", Attribute(window, "Height"));
            Assert.Equal("680", Attribute(window, "MinWidth"));
            Assert.Equal("640", Attribute(window, "MinHeight"));

            Assert.Equal("dialogTabText", Attribute(Named(dialog, "ProcessTab").Elements().Single(), "Classes"));
            Assert.Equal("dialogTabText", Attribute(Named(dialog, "LaunchTab").Elements().Single(), "Classes"));
            Assert.Contains(dialog.Descendants(Avalonia + "Grid"), grid => Attribute(grid, "Margin") == "32,32,38,0"
                && Attribute(grid, "ColumnDefinitions") == "112,*"
                && Attribute(grid, "RowDefinitions") == "46,Auto,48,42,*");
            Assert.Equal("Wrap", Attribute(Named(dialog, "DeviceDiagnosticText"), "TextWrapping"));
            Assert.Equal("92", Attribute(Named(dialog, "DeviceDiagnosticRegion"), "MaxHeight"));
            Assert.True(dialog.Descendants(Avalonia + "Grid").Count(grid => Attribute(grid, "ColumnDefinitions") == "*,48") >= 2);
            Assert.Contains(dialog.Descendants(Avalonia + "Grid"), grid => Attribute(grid, "ColumnDefinitions") == "*,88");
            Assert.Contains(dialog.Descendants(Avalonia + "Grid"), grid => Attribute(grid, "ColumnDefinitions") == "*,112,112");
            Assert.Contains(dialog.Descendants(Avalonia + "Grid"), grid => Attribute(grid, "RowDefinitions") == "32,26,*");
            Assert.Contains(dialog.Descendants(Avalonia + "Grid"), grid => Attribute(grid, "ColumnDefinitions") == "34,64,*");
            Assert.Contains(dialog.Descendants(Avalonia + "Border"), border => Attribute(border, "Classes") == "processRow"
                && Attribute(border, "MinHeight") == "26");
            Assert.Contains(dialog.Descendants(Avalonia + "TextBlock"), text => Attribute(text, "Text") == "PID");
            Assert.Contains(dialog.Descendants(Avalonia + "TextBlock"), text => Attribute(text, "Text") == "Process");
            Assert.Contains("processList", Attribute(Named(dialog, "ProcessList"), "Classes"));
            Assert.Equal("122,0,0,0", Attribute(Named(dialog, "AutoStartCheckBox"), "Margin"));
            XElement driverButton = Named(dialog, "AppleDriverButton");
            Assert.Contains("driverDownloadButton", Attribute(driverButton, "Classes"));
            Assert.Equal("下载苹果设备驱动", Attribute(driverButton, "Content"));
            Assert.Equal("DownloadAppleDriver", Attribute(driverButton, "Click"));
            Assert.Equal("False", Attribute(driverButton, "IsVisible"));
            Assert.DoesNotContain(dialog.Descendants(), element => (string)element.Attribute(Xaml + "Name") == "StatusText");
            Assert.DoesNotContain(dialog.Descendants(Avalonia + "TextBlock"), text => Attribute(text, "Text") == "{Binding Platform}");

            XDocument app = LoadXaml("src", "MoTuPerf.Desktop", "App.axaml");
            AssertStyle(app, "Button.iconDialogButton", "Width", "38");
            AssertStyle(app, "Button.iconDialogButton", "Margin", "10,0,0,0");
            AssertStyle(app, "Button.dialogLaunchButton", "Width", "78");
            AssertStyle(app, "Button.dialogLaunchButton", "Margin", "6,0,0,0");
            AssertStyle(app, "Button.dialogFooterButton", "Width", "100");
            AssertStyle(app, "Button.dialogFooterButton", "MinWidth", "92");
            AssertStyle(app, "Button.dialogFooterButton", "Margin", "6,0");
            AssertStyle(app, "Button.dialogTab", "MinWidth", "130");
            AssertStyle(app, "Button.dialogTab", "Height", "38");
            AssertStyle(app, "Button.dialogTab", "BorderThickness", "1");
            AssertStyle(app, "Button.dialogTab.activeDialogTab TextBlock.dialogTabText", "Foreground", "{DynamicResource Theme.Accent}");
            AssertStyle(app, "Button.dialogCloseButton", "Width", "42");
            AssertStyle(app, "ListBox.processList > ListBoxItem", "MinHeight", "26");
            AssertStyle(app, "ListBox.dialogList > ListBoxItem:selected", "Background", "{DynamicResource Theme.Accent}");
            AssertStyle(app, "ListBox.processList > ListBoxItem:selected Border.processRow", "Background", "{DynamicResource Theme.Accent}");
            AssertStyle(app, "TextBox.dialogSearch /template/ Border#PART_BorderElement", "Background", "{DynamicResource Theme.DialogInputBackground}");
            AssertStyle(app, "TextBox.dialogSearch /template/ Button#PART_ClearButton", "IsVisible", "False");
            Assert.All(new[] { Named(dialog, "ProcessSearch"), Named(dialog, "AppSearch") }, search =>
            {
                Assert.Equal(string.Empty, Attribute(search, "PlaceholderText"));
                Assert.Equal("Hidden", Attribute(search, "ScrollViewer.VerticalScrollBarVisibility"));
                Assert.Equal("Hidden", Attribute(search, "ScrollViewer.HorizontalScrollBarVisibility"));
            });
            Assert.Equal(1, dialog.Descendants(Avalonia + "TextBlock")
                .Count(text => Attribute(text, "Text") == "请输入进程名称进行搜索"));
            Assert.Equal(1, dialog.Descendants(Avalonia + "TextBlock")
                .Count(text => Attribute(text, "Text") == "请输入应用名称进行搜索"));

            string dialogCode = LoadText("src", "MoTuPerf.Desktop", "DevicePickerWindow.axaml.cs");
            Assert.Contains("ProcessTargetMatcher.IsIosDefaultPickerProcess(process, selectedBundle)", dialogCode);
            Assert.Contains("if (!DeviceLookupService.IsAndroid(device))", dialogCode);
            Assert.DoesNotContain("ThenBy(delegate(ProcessInfo process) { return process.Pid; })", dialogCode);
            Assert.Contains("report.AppleDriverActionAvailable", dialogCode);
            Assert.Contains("修复苹果设备驱动", dialogCode);
            Assert.Contains("AppleDriverDownloadService", dialogCode);
            Assert.Contains("已下载完成，是否现在启动安装", dialogCode);
        }

        [Fact]
        public void LaunchAppTabKeepsAppSelectionIndependentFromTheHiddenProcessList()
        {
            string dialogCode = LoadText("src", "MoTuPerf.Desktop", "DevicePickerWindow.axaml.cs");

            Assert.Contains("if (!_launchMode) RunSelectionSync(ApplyProcessFilter);", dialogCode);
            Assert.Contains("if (_synchronizingSelections || _launchMode) return;", dialogCode);
            Assert.Contains("AppInfo summaryApp = _launchMode", dialogCode);
            Assert.Contains("else ApplyProcessFilter();", dialogCode);
            Assert.Contains("private void RunSelectionSync(Action action)", dialogCode);
        }

        [Fact]
        public void AsyncPickerResultsAreBoundToTheCurrentDeviceLoadGeneration()
        {
            string dialogCode = LoadText("src", "MoTuPerf.Desktop", "DevicePickerWindow.axaml.cs");

            Assert.Contains("private long _loadGeneration", dialogCode);
            Assert.Contains("NewLoadToken(out generation)", dialogCode);
            Assert.Contains("if (!IsCurrentLoad(generation)) return;", dialogCode);
            Assert.Contains("if (!IsCurrentLoad(generation, deviceUdid)) return;", dialogCode);
            Assert.Contains("_apps.ToArray()", dialogCode);
            Assert.Contains("_processes.ToArray()", dialogCode);
            Assert.Contains("IList<AppInfo> apps", dialogCode);
            Assert.Contains("IList<ProcessInfo> processes", dialogCode);
        }

        [Fact]
        public void EveryButtonUsesTheSharedThemeHoverSurface()
        {
            XDocument app = LoadXaml("src", "MoTuPerf.Desktop", "App.axaml");

            AssertStyle(app, "Button:pointerover /template/ ContentPresenter#PART_ContentPresenter", "Background", "{DynamicResource Theme.HoverBackground}");
            AssertStyle(app, "Button:pointerover /template/ ContentPresenter#PART_ContentPresenter", "BorderBrush", "{DynamicResource Theme.Border}");
            AssertStyle(app, "Button:pointerover /template/ ContentPresenter#PART_ContentPresenter", "Foreground", "{DynamicResource Theme.TextPrimary}");
            AssertStyle(app, "Button:pressed /template/ ContentPresenter#PART_ContentPresenter", "Background", "{DynamicResource Theme.PressedBackground}");
            AssertStyle(app, "Button:pressed /template/ ContentPresenter#PART_ContentPresenter", "BorderBrush", "{DynamicResource Theme.BorderStrong}");

            XElement[] hoverStyles = app.Descendants(Avalonia + "Style")
                .Where(element => Attribute(element, "Selector").StartsWith("Button", StringComparison.Ordinal)
                    && Attribute(element, "Selector").Contains(":pointerover", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(hoverStyles);
            Assert.All(hoverStyles.SelectMany(style => style.Elements(Avalonia + "Setter")
                .Where(setter => Attribute(setter, "Property") == "Background")), setter =>
                Assert.Equal("{DynamicResource Theme.HoverBackground}", Attribute(setter, "Value")));
            Assert.DoesNotContain(hoverStyles.SelectMany(style => style.Elements(Avalonia + "Setter")), setter =>
                Attribute(setter, "Value").Equals("#B93B58", StringComparison.OrdinalIgnoreCase)
                || Attribute(setter, "Value").Equals("#000000", StringComparison.OrdinalIgnoreCase)
                || Attribute(setter, "Value").Equals("Black", StringComparison.OrdinalIgnoreCase));

            string[] viewFiles =
            {
                "MainWindow.axaml",
                "DevicePickerWindow.axaml",
                "ScreenshotViewerWindow.axaml",
                "HelpWindow.axaml",
                "ChangelogWindow.axaml",
                "ConfirmDialogWindow.axaml",
                "ExportCsvDialogWindow.axaml"
            };
            foreach (string viewFile in viewFiles)
            {
                XDocument view = LoadXaml("src", "MoTuPerf.Desktop", viewFile);
                Assert.All(view.Descendants(Avalonia + "Button"), button =>
                    Assert.False(string.IsNullOrWhiteSpace(Attribute(button, "Classes")), viewFile + " contains an unclassed Button."));
            }

            AssertStyle(app, "Button.dataTab.activeTab:pointerover /template/ ContentPresenter#PART_ContentPresenter", "BorderBrush", "{DynamicResource Theme.Accent}");
            AssertStyle(app, "Button.dialogTab.activeDialogTab:pointerover /template/ ContentPresenter#PART_ContentPresenter", "BorderBrush", "{DynamicResource Theme.Accent}");
            AssertStyle(app, "TextBlock.captureStartIcon", "Foreground", "{DynamicResource Theme.Accent}");
            AssertStyle(app, "TextBlock.captureStopIcon", "Foreground", "#F45B86");
        }

        [Fact]
        public void ReleaseVersionUsesThreeNumericPartsAndStaysSynchronized()
        {
            XDocument project = XDocument.Load(ResolvePath("src", "MoTuPerf.Desktop", "MoTuPerf.Desktop.csproj"));
            string version = project.Descendants("Version").Single().Value;
            string[] components = version.Split('.');

            Assert.Equal("0.24.2", version);
            Assert.Equal(3, components.Length);
            Assert.All(components, component => Assert.True(int.TryParse(component, out _)));
            Assert.DoesNotContain("-", version);

            string viewModel = LoadText("src", "MoTuPerf.Desktop", "MainWindowViewModel.cs");
            string macBuild = LoadText("packaging", "macos-arm64", "build-app.sh");
            string macReadme = LoadText("packaging", "macos-arm64", "README.md");
            Assert.Contains("return \"v" + version + "\";", viewModel);
            Assert.Contains("MOTUPERF_VERSION:-" + version, macBuild);
            Assert.Contains("MoTuPerf-v" + version + "-osx-arm64.dmg", macReadme);
            Assert.DoesNotContain("v" + version + "-", viewModel, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("preview", macBuild, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MainTitleBarUsesTheDedicatedOutlinedBrandAsset()
        {
            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement logo = main.Descendants(Avalonia + "Image")
                .Single(element => Attribute(element, "Source").Contains("motu-logo", StringComparison.Ordinal));

            Assert.Equal("154", Attribute(logo, "Width"));
            Assert.Equal("38", Attribute(logo, "Height"));
            Assert.Equal("Uniform", Attribute(logo, "Stretch"));
            Assert.Equal("avares://MoTuPerf.CrossPlatform/Assets/motu-logo-titlebar.png", Attribute(logo, "Source"));

            string assetPath = ResolvePath("src", "MoTuPerf.Desktop", "Assets", "motu-logo-titlebar.png");
            Assert.True(new FileInfo(assetPath).Length > 10_000, "Outlined title-bar logo must be a real raster asset.");
        }

        [Fact]
        public void MainWindowUsesOnePixelThemeAwareOuterFrame()
        {
            XDocument app = LoadXaml("src", "MoTuPerf.Desktop", "App.axaml");
            AssertStyle(app, "Border.mainWindowFrame", "Background", "{DynamicResource Theme.WindowBackground}");
            AssertStyle(app, "Border.mainWindowFrame", "BorderBrush", "{DynamicResource Theme.BorderStrong}");
            AssertStyle(app, "Border.mainWindowFrame", "BorderThickness", "1");

            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement frame = main.Root.Elements(Avalonia + "Border").Single();
            Assert.Equal("mainWindowFrame", Attribute(frame, "Classes"));

            XElement layout = frame.Elements(Avalonia + "Grid").Single();
            Assert.Equal("40,64,*", Attribute(layout, "RowDefinitions"));
            Assert.Equal("{DynamicResource Theme.WindowBackground}", Attribute(layout, "Background"));
            Assert.Equal("BorderOnly", Attribute(main.Root, "WindowDecorations"));
            Assert.Equal("True", Attribute(main.Root, "ExtendClientAreaToDecorationsHint"));
            Assert.Equal("0", Attribute(main.Root, "ExtendClientAreaTitleBarHeightHint"));
            AssertStyle(app, "Window", "Win32Properties.WindowCornerPreference", "Round");
            AssertStyle(app, "Border.mainWindowFrame", "CornerRadius", "8");
            AssertStyle(app, "Border.mainWindowFrame", "ClipToBounds", "True");
            AssertStyle(app, "Window[WindowState=Maximized] Border.mainWindowFrame, Window[WindowState=FullScreen] Border.mainWindowFrame", "CornerRadius", "0");
        }

        [Fact]
        public void SecondaryWindowsUseOneVisibleSharedFrame()
        {
            XDocument app = LoadXaml("src", "MoTuPerf.Desktop", "App.axaml");
            AssertStyle(app, "Border.secondaryWindowFrame", "Background", "{DynamicResource Theme.DialogBackground}");
            AssertStyle(app, "Border.secondaryWindowFrame", "BorderBrush", "{DynamicResource Theme.DialogBorder}");
            AssertStyle(app, "Border.secondaryWindowFrame", "BorderThickness", "1");
            AssertStyle(app, "Border.secondaryWindowFrame", "CornerRadius", "8");
            AssertStyle(app, "Border.secondaryWindowFrame", "ClipToBounds", "True");
            AssertStyle(app, "Window[WindowState=Maximized] Border.secondaryWindowFrame, Window[WindowState=FullScreen] Border.secondaryWindowFrame", "CornerRadius", "0");
            AssertStyle(app, "Border.dialogTitleBar", "Background", "{DynamicResource Theme.TitleBarBackground}");
            AssertStyle(app, "Border.dialogFooter", "Background", "{DynamicResource Theme.DialogFooterBackground}");
            AssertStyle(app, "ListBox.dialogList", "Background", "{DynamicResource Theme.DialogSurface}");
            AssertStyle(app, "Border.helpSection", "Background", "{DynamicResource Theme.DialogSurface}");

            string[] secondaryWindows =
            {
                "DevicePickerWindow.axaml",
                "ConfirmDialogWindow.axaml",
                "DataCleanupWindow.axaml",
                "DataCleanupProgressWindow.axaml",
                "DataDirectorySettingsWindow.axaml",
                "ExportCsvDialogWindow.axaml",
                "HelpWindow.axaml",
                "ChangelogWindow.axaml",
                "UpdatePromptWindow.axaml",
                "UpdateDownloadWindow.axaml",
                "ScreenshotViewerWindow.axaml"
            };
            foreach (string viewFile in secondaryWindows)
            {
                XDocument view = LoadXaml("src", "MoTuPerf.Desktop", viewFile);
                XElement frame = view.Root.Elements(Avalonia + "Border").Single();
                Assert.Equal("secondaryWindowFrame", Attribute(frame, "Classes"));
                Assert.Equal("BorderOnly", Attribute(view.Root, "WindowDecorations"));
                Assert.Equal("True", Attribute(view.Root, "ExtendClientAreaToDecorationsHint"));
                Assert.Equal("0", Attribute(view.Root, "ExtendClientAreaTitleBarHeightHint"));
                XElement layout = frame.Elements(Avalonia + "Grid").Single();
                XElement titleBar = layout.Elements(Avalonia + "Border").First();
                Assert.Equal("dialogTitleBar", Attribute(titleBar, "Classes"));
                Assert.DoesNotContain("Theme.ToolbarBackground", view.ToString());
            }

            XDocument help = LoadXaml("src", "MoTuPerf.Desktop", "HelpWindow.axaml");
            Assert.Equal("700", Attribute(help.Root, "Width"));
            Assert.Equal("640", Attribute(help.Root, "Height"));
            Assert.Equal(7, help.Descendants(Avalonia + "Border")
                .Count(element => Attribute(element, "Classes") == "helpSection"));
            XElement helpCloseButton = help.Descendants(Avalonia + "Button")
                .Single(element => Attribute(element, "Click") == "CloseWindow"
                    && Attribute(element, "Classes").Contains("closeButton", StringComparison.Ordinal));
            XElement helpTitleGrid = helpCloseButton.Parent;
            Assert.Equal("*,46", Attribute(helpTitleGrid, "ColumnDefinitions"));
            string helpText = string.Join(" ", help.Descendants(Avalonia + "TextBlock").Select(element => Attribute(element, "Text")));
            Assert.Contains("Windows x64", helpText);
            Assert.Contains("macOS Apple Silicon", helpText);
            Assert.Contains("USB 调试", helpText);
            Assert.Contains("开发者模式", helpText);
            Assert.Contains("Apple 设备驱动", helpText);
            Assert.Contains("所选 PID", helpText);
            Assert.Contains("不按所选 PID 过滤", helpText);
            Assert.Contains("Process CPU Raw", helpText);
            Assert.Contains("Thermal Status", helpText);
            Assert.Contains("0 正常、1 轻微、2 中度、3 严重、4 临界、5 紧急、6 关机", helpText);
            Assert.Contains("每 3 秒", helpText);
            Assert.Contains("缩放只影响查看", helpText);
            Assert.Contains("按秒汇总", helpText);
            Assert.Contains("原始明细", helpText);
            Assert.Contains("软件安装目录下", helpText);
            Assert.DoesNotContain("框选曲线可放大时间范围", helpText);
            Assert.Contains("点击或拖动任意曲线可移动时间游标", helpText);
            Assert.DoesNotContain(help.Descendants(Avalonia + "Button"), button => Attribute(button, "Content") == "查看在线版本更新日志");
            Assert.DoesNotContain("OpenVersionLog", LoadText("src", "MoTuPerf.Desktop", "HelpWindow.axaml.cs"));

            XDocument changelog = LoadXaml("src", "MoTuPerf.Desktop", "ChangelogWindow.axaml");
            Assert.Equal("更新日志", Attribute(changelog.Root, "Title"));
            Assert.Contains(changelog.Descendants(Avalonia + "Border"), border => Attribute(border, "Classes") == "secondaryWindowFrame");
            string changelogText = string.Join(" ", changelog.Descendants(Avalonia + "TextBlock").Select(element => Attribute(element, "Text")));
            Assert.Contains("v0.24.0", changelogText);
            Assert.Contains("v0.24.2", changelogText);
            Assert.Contains("v0.24.1", changelogText);
            Assert.DoesNotContain("v0.23.0", changelogText);
            Assert.Contains(changelog.Descendants(Avalonia + "Button"), button => Attribute(button, "Content") == "查看完整在线更新日志"
                && Attribute(button, "Click") == "OpenVersionLog");

            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement toolbar = main.Descendants(Avalonia + "Grid")
                .Single(grid => Attribute(grid, "ColumnDefinitions") == "Auto,*,250");
            XElement buttonPanel = toolbar.Elements(Avalonia + "StackPanel").First();
            string[] toolbarLabels = buttonPanel.Descendants(Avalonia + "TextBlock")
                .Select(element => Attribute(element, "Text"))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
            Assert.True(Array.IndexOf(toolbarLabels, "帮助") >= 0);
            Assert.True(Array.IndexOf(toolbarLabels, "更新日志") > Array.IndexOf(toolbarLabels, "帮助"));
            Assert.Contains("ShowChangelog", LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs"));
        }

        [Fact]
        public void ScreenshotViewerUsesDisplayedImageOrientationSizing()
        {
            string viewerCode = LoadText("src", "MoTuPerf.Desktop", "ScreenshotViewerWindow.axaml.cs");
            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");

            Assert.Contains("ScreenshotViewerWindow(IEnumerable<ScreenshotItemViewModel> screenshots, ScreenshotItemViewModel selected)", viewerCode);
            Assert.Contains("event Action<ScreenshotItemViewModel> ScreenshotSelected", viewerCode);
            Assert.Contains("ScreenshotSelected?.Invoke(_screenshots[_index]);", viewerCode);
            Assert.Contains("viewer.ScreenshotSelected += viewModel.SelectScreenshot;", LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs"));
            Assert.Contains("image.PixelSize.Width > image.PixelSize.Height", viewerCode);
            Assert.Contains("return IsLandscapeImage(image) ? 1220 : 820;", viewerCode);
            Assert.Contains("return IsLandscapeImage(image) ? 820 : 920;", viewerCode);
            Assert.Contains(main.Descendants(Avalonia + "Border"), element => Attribute(element, "Classes") == "screenshotCard"
                && Attribute(element, "Width") == "{Binding CardWidth}");
        }

        [Theory]
        [InlineData(ScreenshotOrientation.Portrait, 1242, 2208, 0, false)]
        [InlineData(ScreenshotOrientation.Unknown, 1242, 2208, 0, false)]
        [InlineData(ScreenshotOrientation.Unknown, 2208, 1242, 0, true)]
        [InlineData(ScreenshotOrientation.LandscapeHomeToRight, 1242, 2208, -90, true)]
        [InlineData(ScreenshotOrientation.LandscapeHomeToLeft, 1242, 2208, 90, true)]
        [InlineData(ScreenshotOrientation.LandscapeHomeToRight, 2208, 1242, 0, true)]
        public void ScreenshotDisplayOrientationAvoidsDoubleRotation(
            ScreenshotOrientation orientation,
            int width,
            int height,
            int expectedRotation,
            bool expectedLandscape)
        {
            Assert.Equal(expectedRotation, ScreenshotDisplayOrientation.RotationDegrees(orientation, width, height));
            Assert.Equal(expectedLandscape, ScreenshotDisplayOrientation.IsLandscape(orientation, width, height));
        }

        [Fact]
        public void CsvExportDialogOffersSummaryByDefaultAndRawDetailAsAnExplicitChoice()
        {
            XDocument dialog = LoadXaml("src", "MoTuPerf.Desktop", "ExportCsvDialogWindow.axaml");
            XElement window = dialog.Root;
            Assert.Equal("500", Attribute(window, "Width"));
            Assert.Equal("230", Attribute(window, "Height"));

            XElement frameLayout = window.Elements(Avalonia + "Border").Single()
                .Elements(Avalonia + "Grid").Single();
            Assert.Equal("46,*,52", Attribute(frameLayout, "RowDefinitions"));

            XElement content = frameLayout.Elements(Avalonia + "StackPanel").Single();
            Assert.Equal("18,12", Attribute(content, "Margin"));
            Assert.Equal("10", Attribute(content, "Spacing"));

            XElement modeGrid = content.Elements(Avalonia + "Grid").Single();
            Assert.Equal("*,*", Attribute(modeGrid, "ColumnDefinitions"));
            Assert.Equal("12", Attribute(modeGrid, "ColumnSpacing"));

            XElement summary = dialog.Descendants(Avalonia + "Button").Single(element => Attribute(element, "Click") == "ExportSummary");
            XElement raw = dialog.Descendants(Avalonia + "Button").Single(element => Attribute(element, "Click") == "ExportRawDetail");
            Assert.Contains("recommended", Attribute(summary, "Classes"));
            Assert.DoesNotContain("recommended", Attribute(raw, "Classes"));
            Assert.Equal("Stretch", Attribute(summary, "HorizontalAlignment"));
            Assert.Equal("Stretch", Attribute(raw, "HorizontalAlignment"));

            XDocument app = LoadXaml("src", "MoTuPerf.Desktop", "App.axaml");
            AssertStyle(app, "Button.exportModeButton", "Height", "76");
            AssertStyle(app, "Button.exportModeButton", "Padding", "16,8");

            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");
            Assert.Contains("new ExportCsvDialogWindow().ShowDialog<CSharpIosPerfMonitor.CsvExportMode?>", windowCode);
            Assert.Contains("CsvExportService.WriteToFile(document, mode.Value, path)", windowCode);
            Assert.Contains("motuperf-raw-", windowCode);
            Assert.Contains("if (_allowClose) return;", windowCode);
            Assert.Contains("if (_closeFlowRunning) { e.Cancel = true; return; }", windowCode);
            Assert.Contains("文件操作正在进行，请等待完成后再关闭软件", windowCode);
        }

        [Fact]
        public void ChartPointerDragKeepsLegacyModeAndOptInZoom()
        {
            string chartCode = LoadText("src", "MoTuPerf.Desktop", "PerformanceChartControl.cs");
            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");

            Assert.Contains("e.Pointer.Capture(this)", chartCode);
            Assert.Contains("if (!ZoomEnabled) SelectTimeAt(point)", chartCode);
            Assert.Contains("_isRangeSelecting", chartCode);
            Assert.Contains("ZoomRangeSelected", chartCode);
            Assert.Contains("ZoomRangeSelected", windowCode);
            Assert.DoesNotContain("TimeRangeSelected", chartCode);
            Assert.DoesNotContain("TimeRangeSelected", windowCode);
            Assert.DoesNotContain("SelectTimeRange", windowCode);
        }

        [Fact]
        public void TimelineProvidesOptInZoomAndStableResetSlot()
        {
            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement timelineGrid = main.Descendants(Avalonia + "Grid")
                .Single(element => Attribute(element, "ColumnDefinitions") == "220,*,180");
            Assert.Contains(timelineGrid.Descendants(Avalonia + "CheckBox"), element => Attribute(element, "Content") == "缩放曲线");
            XElement reset = Named(main, "ResetChartZoomButton");
            Assert.Equal("{Binding ShowChartZoomReset}", Attribute(reset, "IsVisible"));
            Assert.Equal("ResetChartZoom", Attribute(reset, "Click"));
            Assert.Equal("{Binding IsChartZoomEnabled, Mode=TwoWay}", Attribute(Named(main, "ChartZoomCheckBox"), "IsChecked"));
            Assert.Equal(8, main.Descendants().Count(element => element.Name.LocalName == "PerformanceChartControl" && Attribute(element, "ZoomEnabled") == "{Binding IsChartZoomEnabled}"));

            XDocument app = LoadXaml("src", "MoTuPerf.Desktop", "App.axaml");
            AssertStyle(app, "Button.chartZoomResetButton:pointerover", "Background", "{DynamicResource Theme.HoverBackground}");
        }

        [Fact]
        public void OpeningSessionUsesAVisibleBlockingLoadingOverlay()
        {
            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement overlay = Named(main, "SessionLoadingOverlay");

            Assert.Equal("0", Attribute(overlay, "Grid.Row"));
            Assert.Equal("3", Attribute(overlay, "Grid.RowSpan"));
            Assert.Equal("{Binding IsFileOperationInProgress}", Attribute(overlay, "IsVisible"));
            Assert.Equal("True", Attribute(overlay, "IsHitTestVisible"));
            Assert.Equal("100", Attribute(overlay, "ZIndex"));
            Assert.Contains(overlay.Descendants(Avalonia + "ProgressBar"), element => Attribute(element, "IsIndeterminate") == "{Binding IsFileOperationIndeterminate}"
                && Attribute(element, "Value") == "{Binding FileOperationProgress}"
                && Attribute(element, "Maximum") == "100");
            Assert.Contains(overlay.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text") == "{Binding FileOperationProgressText}"
                && Attribute(element, "IsVisible") == "{Binding IsFileOperationProgressVisible}");
            Assert.Contains(overlay.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text") == "{Binding FileOperationText}");
            Assert.Contains(overlay.Descendants(Avalonia + "TextBlock"), element => Attribute(element, "Text") == "{Binding FileOperationDetailText}");

            XDocument app = LoadXaml("src", "MoTuPerf.Desktop", "App.axaml");
            AssertStyle(app, "Border.sessionLoadingOverlay", "Background", "{DynamicResource Theme.WindowBackground}");
            AssertStyle(app, "ProgressBar.sessionLoadingProgress", "Foreground", "{DynamicResource Theme.Accent}");

            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");
            Assert.Contains("BeginSessionLoading()", windowCode);
            Assert.Contains("EndFileOperation()", windowCode);
            Assert.Contains("finally { viewModel.EndFileOperation(); }", windowCode);
            Assert.Contains("if (viewModel.IsFileOperationInProgress)", windowCode);
        }

        [Fact]
        public void SavingAndExportingUseTheBlockingFileOperationOverlay()
        {
            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");

            Assert.Contains("BeginFileOperation(\"正在保存现场文件，请稍候...\"", windowCode);
            Assert.Contains("正在写入曲线、设备信息和截图，请勿关闭软件", windowCode);
            Assert.Contains("SessionArchiveService.Save(path, document)", windowCode);
            Assert.Contains("正在导出原始明细，请稍候...", windowCode);
            Assert.Contains("正在导出秒级汇总，请稍候...", windowCode);
            Assert.Contains("CsvExportService.WriteToFile(document, mode.Value, path)", windowCode);
            Assert.Contains("finally { viewModel.EndFileOperation(); }", windowCode);
        }

        [Fact]
        public void ChangelogWindowLinksToTheCompleteOnlineVersionLog()
        {
            XDocument changelog = LoadXaml("src", "MoTuPerf.Desktop", "ChangelogWindow.axaml");
            XElement link = changelog.Descendants(Avalonia + "Button").Single(button => Attribute(button, "Content") == "查看完整在线更新日志");
            Assert.Equal("OpenVersionLog", Attribute(link, "Click"));
            string changelogCode = LoadText("src", "MoTuPerf.Desktop", "ChangelogWindow.axaml.cs");
            Assert.Contains("OpenVersionLog", changelogCode);
            Assert.Contains("EJ6Hdr6lbokuUsxr8FecLA7Qneg", changelogCode);
        }

        [Fact]
        public void BundledChangelogMatchesLatestThreeReleaseNotes()
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(ResolvePath("src", "MoTuPerf.Desktop", "MoTuPerf.Desktop.csproj"))))!;
            string markdown = File.ReadAllText(Path.Combine(root, "..", "CHANGELOG.md"));
            var sections = System.Text.RegularExpressions.Regex.Matches(markdown,
                @"(?ms)^## v(?<version>\d+\.\d+\.\d+)(?: - (?<date>[^\r\n]+))?\r?\n(?<body>.*?)(?=^## |\z)").Cast<System.Text.RegularExpressions.Match>().Take(3).ToArray();
            XDocument changelog = LoadXaml("src", "MoTuPerf.Desktop", "ChangelogWindow.axaml");
            XElement[] cards = changelog.Descendants(Avalonia + "Border").Where(border => Attribute(border, "Classes") == "helpSection").ToArray();
            Assert.Equal(3, sections.Length);
            Assert.Equal(3, cards.Length);
            string version = XDocument.Load(ResolvePath("src", "MoTuPerf.Desktop", "MoTuPerf.Desktop.csproj")).Descendants("Version").Single().Value;
            Assert.Equal(version, sections[0].Groups["version"].Value);
            for (int index = 0; index < sections.Length; index++)
            {
                XElement[] text = cards[index].Descendants(Avalonia + "TextBlock").ToArray();
                Assert.Equal("v" + sections[index].Groups["version"].Value, Attribute(text[0], "Text"));
                Assert.Equal(sections[index].Groups["date"].Value, Attribute(text[1], "Text"));
                string[] expected = sections[index].Groups["body"].Value.Split('\n')
                    .Select(line => line.Trim()).Where(line => line.StartsWith("- "))
                    .Select(line => line.Substring(2).Replace("`", "")).ToArray();
                string[] actual = Attribute(text[2], "Text").Split('\n').Select(line => line.TrimStart('\u2022', ' ')).ToArray();
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void ReleaseDialogsFitContentAndManualChecksBypassSkippedVersion()
        {
            foreach (string name in new[] { "ChangelogWindow", "UpdatePromptWindow" })
            {
                XDocument dialog = LoadXaml("src", "MoTuPerf.Desktop", name + ".axaml");
                Assert.Equal("Height", Attribute(dialog.Root, "SizeToContent"));
                Assert.Null(dialog.Root.Attribute("Height"));
                Assert.Null(dialog.Root.Attribute("MinHeight"));
                Assert.NotNull(dialog.Root.Attribute("MaxHeight"));
                Assert.Contains(dialog.Descendants(Avalonia + "ScrollViewer"), scroll => Attribute(scroll, "VerticalScrollBarVisibility") == "Auto");
                Assert.Contains("ContentDialogSizing.Attach(this)", LoadText("src", "MoTuPerf.Desktop", name + ".axaml.cs"));
            }
            Assert.Contains("!manual && !result.Manifest.Mandatory && viewModel.IsUpdateSkipped", LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs"));
            string promptCode = LoadText("src", "MoTuPerf.Desktop", "UpdatePromptWindow.axaml.cs");
            Assert.Contains("manifest.ReleaseNotes", promptCode);
            Assert.Contains("此版本暂未提供更新内容", promptCode);
        }

        [Fact]
        public void StartingAfterOpeningASessionRequiresFreshDeviceSelection()
        {
            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");
            string viewModelCode = LoadText("src", "MoTuPerf.Desktop", "MainWindowViewModel.cs");
            string helpCode = LoadText("src", "MoTuPerf.Desktop", "HelpWindow.axaml");

            Assert.Contains("CaptureSelectionNeedsPicker", windowCode);
            Assert.Contains("await OpenDevicePickerAsync(true)", windowCode);
            Assert.Contains("startAfterSelection || selection.AutoStart", windowCode);
            Assert.Contains("_selectionNeedsRefresh", viewModelCode);
            Assert.Contains("现场文件中的设备和进程仅用于查看", viewModelCode);
            Assert.Contains("现场文件中的设备和进程是历史记录，仅用于还原查看", helpCode);
        }

        [Fact]
        public void FileToolbarActionsAreDisabledDuringCapture()
        {
            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement open = main.Descendants(Avalonia + "Button").Single(element => Attribute(element, "Click") == "OpenSession");
            XElement save = main.Descendants(Avalonia + "Button").Single(element => Attribute(element, "Click") == "SaveSession");
            XElement export = main.Descendants(Avalonia + "Button").Single(element => Attribute(element, "Click") == "ExportCsv");
            Assert.Equal("{Binding CanUseFileActions}", Attribute(open, "IsEnabled"));
            Assert.Equal("{Binding CanUseFileActions}", Attribute(save, "IsEnabled"));
            Assert.Equal("{Binding CanUseFileActions}", Attribute(export, "IsEnabled"));

            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");
            Assert.Contains("请先停止采集再保存现场文件", windowCode);
            Assert.Contains("请先停止采集再导出数据", windowCode);
        }

        [Fact]
        public void OpeningSessionDefersHeavyRestorationWorkOffTheUiThread()
        {
            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");
            string viewModelCode = LoadText("src", "MoTuPerf.Desktop", "MainWindowViewModel.cs");

            Assert.Contains("await viewModel.ApplySessionDocumentAsync(document)", windowCode);
            Assert.Contains("PreparedSessionData prepared = await Task.Run", viewModelCode);
            Assert.Contains("return PrepareSessionData(document);", viewModelCode);
            Assert.Contains("await AddScreenshotItemsAsync(prepared.Screenshots);", viewModelCode);
            Assert.Contains("_ = LoadScreenshotImagesAsync();", viewModelCode);
            Assert.DoesNotContain("await LoadInitialScreenshotImagesAsync();", viewModelCode);
            Assert.Contains("ScreenshotItemViewModel[] items = await Task.Run", viewModelCode);
            Assert.Contains("Screenshots = _screenshotItems.ToArray();", viewModelCode);
            Assert.Contains("internal ScreenshotItemViewModel(ScreenshotInfo screenshot, bool loadImage)", viewModelCode);
            Assert.Contains("ScreenshotImageLoader.Load(Path, Screenshot.Orientation, ThumbnailLongEdge)", viewModelCode);
            Assert.Contains("private const int ThumbnailLongEdge = 100;", viewModelCode);
            Assert.Contains("await Task.Delay(40);", viewModelCode);
            string mainXaml = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            Assert.Contains("<VirtualizingStackPanel Orientation=\"Horizontal\" />", mainXaml);
            Assert.Contains("public Task<Bitmap> LoadFullImageAsync()", viewModelCode);
        }

        [Fact]
        public void ThemeSelectorIsDropdownWithNamedColorSwatchesAndChartsUseThemePalette()
        {
            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement selector = Named(main, "ThemeSelectorButton");
            Assert.Contains("themeSelectorButton", Attribute(selector, "Classes"));
            Assert.Equal("{Binding ThemeSelectorToolTip}", Attribute(selector, "ToolTip.Tip"));
            Assert.True(string.IsNullOrWhiteSpace(Attribute(selector, "Click")));
            Assert.Contains(selector.Descendants(Avalonia + "TextBlock"),
                element => Attribute(element, "Text") == "\uE790"
                    && Attribute(element, "Classes").Contains("themeSelectorIcon", StringComparison.Ordinal));
            Assert.DoesNotContain(selector.Descendants(),
                element => Attribute(element, "Text") == "{Binding CurrentThemeName}"
                    || Attribute(element, "Background") == "{Binding CurrentThemePreviewColor}");

            XElement flyout = selector.Descendants(Avalonia + "Flyout").Single();
            Assert.Equal("themeFlyoutPresenter", Attribute(flyout, "FlyoutPresenterClasses"));
            XDocument app = LoadXaml("src", "MoTuPerf.Desktop", "App.axaml");
            AssertStyle(app, "FlyoutPresenter.themeFlyoutPresenter", "Background", "{DynamicResource Theme.PanelBackground}");
            AssertStyle(app, "FlyoutPresenter.themeFlyoutPresenter", "BorderBrush", "{DynamicResource Theme.BorderStrong}");
            AssertStyle(app, "FlyoutPresenter.themeFlyoutPresenter", "BorderThickness", "0");
            AssertStyle(app, "FlyoutPresenter.themeFlyoutPresenter", "Padding", "0");
            XElement options = flyout.Descendants(Avalonia + "ItemsControl").Single();
            Assert.Equal("{Binding ThemeOptions}", Attribute(options, "ItemsSource"));
            XElement optionButton = options.Descendants(Avalonia + "Button").Single();
            Assert.Contains("themeOptionButton", Attribute(optionButton, "Classes"));
            Assert.Equal("SelectTheme", Attribute(optionButton, "Click"));
            Assert.Contains(optionButton.Descendants(Avalonia + "Border"),
                element => Attribute(element, "Background") == "{Binding PreviewColor}");
            Assert.Contains(optionButton.Descendants(Avalonia + "TextBlock"),
                element => Attribute(element, "Text") == "{Binding DisplayName}");

            string chartCode = LoadText("src", "MoTuPerf.Desktop", "PerformanceChartControl.cs");
            Assert.Contains("ThemeResourceBrush(\"Theme.ChartBackground\"", chartCode);
            Assert.Contains("ThemeResourceBrush(\"Theme.ChartGrid\"", chartCode);
            Assert.Contains("ThemeResourceBrush(\"Theme.TextPrimary\"", chartCode);
            Assert.Contains("ThemeResourceBrush(\"Theme.Cursor\"", chartCode);
            Assert.DoesNotContain("private static readonly IBrush BackgroundBrush", chartCode);

            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");
            Assert.Contains("AppThemeManager.Select(Application.Current, option.Id)", windowCode);
            Assert.DoesNotContain("AppThemeManager.SelectNext", windowCode);

            string viewModel = LoadText("src", "MoTuPerf.Desktop", "MainWindowViewModel.cs");
            Assert.Contains("return AppThemeManager.Themes", viewModel);
            Assert.Contains("return \"选择主题\"", viewModel);

            XElement defaultTitleBarBrush = app.Descendants(Avalonia + "SolidColorBrush")
                .Single(element => (string)element.Attribute(Xaml + "Key") == "Theme.TitleBarBackground");
            Assert.Equal("#181A24", defaultTitleBarBrush.Value);
        }

        [Fact]
        public void SettingsMenuOffersDataCleanupAndDirectorySelectionDialogs()
        {
            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement settings = Named(main, "SettingsButton");
            Assert.Equal("软件设置", Attribute(settings, "ToolTip.Tip"));
            XElement flyout = settings.Descendants(Avalonia + "Flyout").Single();
            Assert.Equal("themeFlyoutPresenter", Attribute(flyout, "FlyoutPresenterClasses"));
            Assert.Contains(flyout.Descendants(Avalonia + "Button"), button => button.Descendants(Avalonia + "TextBlock").Any(text => Attribute(text, "Text") == "清理数据")
                && Attribute(button, "Click") == "ShowDataCleanup");
            Assert.Contains(flyout.Descendants(Avalonia + "Button"), button => button.Descendants(Avalonia + "TextBlock").Any(text => Attribute(text, "Text") == "设置目录")
                && Attribute(button, "Click") == "ShowDataDirectorySettings");

            XDocument cleanup = LoadXaml("src", "MoTuPerf.Desktop", "DataCleanupWindow.axaml");
            Assert.Equal("620", Attribute(cleanup.Root, "Width"));
            Assert.Contains(cleanup.Descendants(Avalonia + "TextBlock"), text => Attribute(text, "Text") == "选择需要清理的数据");
            Assert.Contains(cleanup.Descendants(Avalonia + "TextBlock"), text => Attribute(text, "Text") == "{Binding SizeLabel}");
            Assert.Contains(cleanup.Descendants(Avalonia + "Grid"), grid => Attribute(grid, "ColumnDefinitions") == "34,*,126");
            XElement cleanupList = Named(cleanup, "CategoryList");
            Assert.Equal("0,0,16,0", Attribute(cleanupList, "Padding"));
            Assert.Contains(cleanup.Descendants(Avalonia + "Button"), button => Attribute(button, "Content") == "打开数据目录"
                && Attribute(button, "Click") == "OpenDirectory");

            XDocument cleanupProgress = LoadXaml("src", "MoTuPerf.Desktop", "DataCleanupProgressWindow.axaml");
            Assert.Equal("500", Attribute(cleanupProgress.Root, "Width"));
            Assert.Equal("300", Attribute(cleanupProgress.Root, "Height"));
            Assert.Contains(cleanupProgress.Descendants(Avalonia + "Border"), border => Attribute(border, "Classes") == "secondaryWindowFrame");
            Assert.Contains(cleanupProgress.Descendants(Avalonia + "ProgressBar"), progress => Attribute(progress, "Maximum") == "100"
                && Attribute(progress, "Value") == "{Binding Percent}");
            Assert.Contains(cleanupProgress.Descendants(Avalonia + "TextBlock"), text => Attribute(text, "Text") == "{Binding EstimateText}");
            Assert.Contains(cleanupProgress.Descendants(Avalonia + "TextBlock"), text => Attribute(text, "Text") == "清理完成前无法进行其他操作");
            Assert.DoesNotContain(cleanupProgress.Descendants(Avalonia + "Button"), button => true);

            XDocument directory = LoadXaml("src", "MoTuPerf.Desktop", "DataDirectorySettingsWindow.axaml");
            Assert.Contains(directory.Descendants(Avalonia + "TextBlock"), text => Attribute(text, "Text") == "当前数据保存目录");
            Assert.Equal("Browse", Attribute(directory.Descendants(Avalonia + "Button").Single(button => Attribute(button, "Content") == "浏览..."), "Click"));
            Assert.Equal("Apply", Attribute(directory.Descendants(Avalonia + "Button").Single(button => Attribute(button, "Content") == "保存"), "Click"));
            string windowCode = LoadText("src", "MoTuPerf.Desktop", "MainWindow.axaml.cs");
            Assert.Contains("ChangeDataDirectoryAsync", windowCode);
        }

        [Fact]
        public void RetroSageTextureIsPackagedDecorativeAndThemeControlled()
        {
            const string textureSource = "avares://MoTuPerf.CrossPlatform/Assets/retro-sage-texture.png";
            XDocument main = LoadXaml("src", "MoTuPerf.Desktop", "MainWindow.axaml");
            XElement texture = main.Descendants(Avalonia + "Image")
                .Single(element => Attribute(element, "Source") == textureSource);

            Assert.Equal("{DynamicResource Theme.WorkspaceTextureOpacity}", Attribute(texture, "Opacity"));
            Assert.Equal("False", Attribute(texture, "IsHitTestVisible"));
            Assert.Equal("UniformToFill", Attribute(texture, "Stretch"));

            XDocument project = XDocument.Load(ResolvePath("src", "MoTuPerf.Desktop", "MoTuPerf.Desktop.csproj"));
            Assert.Contains(project.Descendants("AvaloniaResource"),
                element => Attribute(element, "Include") == "Assets\\retro-sage-texture.png");

            string assetPath = ResolvePath("src", "MoTuPerf.Desktop", "Assets", "retro-sage-texture.png");
            Assert.True(new FileInfo(assetPath).Length > 10_000, "Retro texture must be a real packaged raster asset.");
        }

        private static void AssertStyle(XDocument document, string selector, string property, string value)
        {
            XElement style = document.Descendants(Avalonia + "Style")
                .Single(element => Attribute(element, "Selector") == selector);
            XElement setter = style.Elements(Avalonia + "Setter")
                .Single(element => Attribute(element, "Property") == property);
            Assert.Equal(value, Attribute(setter, "Value"));
        }

        private static XElement Named(XDocument document, string name)
        {
            return document.Descendants().Single(element => (string)element.Attribute(Xaml + "Name") == name);
        }

        private static string Attribute(XElement element, string name)
        {
            return (string)element.Attribute(name) ?? string.Empty;
        }

        private static XDocument LoadXaml(params string[] relativePath)
        {
            return XDocument.Load(ResolvePath(relativePath), LoadOptions.PreserveWhitespace);
        }

        private static string LoadText(params string[] relativePath)
        {
            return File.ReadAllText(ResolvePath(relativePath));
        }

        private static string ResolvePath(params string[] relativePath)
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MoTuPerf.CrossPlatform.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            string path = relativePath.Aggregate(directory.FullName, Path.Combine);
            Assert.True(File.Exists(path), "Expected UI source file: " + path);
            return path;
        }
    }
}
