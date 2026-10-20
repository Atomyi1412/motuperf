using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using CSharpIosPerfMonitor;
using MoTuPerf.Desktop;
using Xunit;

namespace MoTuPerf.Desktop.UiTests
{
    public sealed class PickerTestApplication : Application
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<PickerTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();

        public override void Initialize()
        {
            var resources = new App();
            resources.Initialize();
            var dictionary = resources.Resources;
            resources.Resources = new ResourceDictionary();
            Resources = dictionary;
            var styles = resources.Styles.ToArray();
            resources.Styles.Clear();
            foreach (var style in styles) Styles.Add(style);
            AppThemeManager.Apply(this, "dark", false);
        }
    }

    [CollectionDefinition("Picker UI", DisableParallelization = true)]
    public sealed class PickerUiCollection { }

    [Collection("Picker UI")]
    public sealed class DevicePickerInteractionTests
    {
        private static readonly HeadlessUnitTestSession Ui = HeadlessUnitTestSession.StartNew(typeof(PickerTestApplication), AvaloniaTestIsolationLevel.PerAssembly);
        private static Task OnUi(Func<Task> body) => Ui.Dispatch(async () => { await body(); return true; }, CancellationToken.None);
        private static DeviceInfo Device(string id) => new DeviceInfo { Platform = "android", Udid = id, Name = id };
        private static AppInfo AppRow(string bundle) => new AppInfo { Platform = "android", BundleId = bundle, Name = bundle };
        private static ProcessInfo Process(int pid, string bundle, string device = "A") => new ProcessInfo
        {
            Pid = pid, BundleId = bundle, Name = bundle, Platform = "android", DeviceUdid = device, AndroidStartTimeTicks = 100
        };
        private static async Task Until(Func<bool> predicate)
        {
            for (int i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(10); }
            Assert.True(predicate(), "UI condition did not complete within two seconds");
        }

        [Fact]
        public Task BasicDeviceCanLoadTargetsBeforeMetadataCompletesAndLaunchModeOwnsConfirmation() => OnUi(async () =>
        {
            var source = new FakeLookup();
            var details = new TaskCompletionSource<DeviceDiscoveryReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Discovery = (token, progress) => { progress?.Invoke(source.Report); return details.Task; };
            var owner = new Window(); owner.Show();
            var window = new DevicePickerWindow(null, source);
            Task<DeviceSelection> result = window.ShowDialog<DeviceSelection>(owner);
            try
            {
                await Until(() => window.FindControl<ListBox>("ProcessList").SelectedItem != null);
                Assert.False(details.Task.IsCompleted);
                window.FindControl<Button>("LaunchTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var apps = window.FindControl<ListBox>("AppList");
                apps.SelectedItem = apps.Items.Cast<AppInfo>().Single(app => app.BundleId == "net.second");
                window.FindControl<Button>("ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DeviceSelection selected = await result;
                Assert.Equal("net.second", selected.App.BundleId);
                Assert.Equal(11, selected.Process.Pid);
            }
            finally { details.TrySetResult(source.Report); window.Close(); owner.Close(); }
        });

        [Fact]
        public Task TransientDiscoveryFailurePreservesSelectionButConfirmationStillChecksConnection() => OnUi(async () =>
        {
            var source = new FakeLookup();
            var window = new DevicePickerWindow(null, source); window.Show();
            try
            {
                await Until(() => window.FindControl<ListBox>("ProcessList").SelectedItem != null);
                source.Report = new DeviceDiscoveryReport { AndroidDiagnostic = "ADB query timed out" };
                source.Online = null;
                await window.LoadDevicesAsync(monitor: true);
                Assert.Equal("A", ((DeviceInfo)window.FindControl<ComboBox>("DeviceList").SelectedItem).Udid);
                Assert.NotNull(window.FindControl<ListBox>("ProcessList").SelectedItem);
                window.FindControl<Button>("ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => window.FindControl<TextBlock>("DeviceCountText").Text.Contains("无法确认"));
                Assert.Null(window.FindControl<ListBox>("ProcessList").SelectedItem);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task GroupsAndRolesRenderWithinThreeThemesAtSupportedSizes() => OnUi(async () =>
        {
            string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../.outputs/device-picker-v0450"));
            Directory.CreateDirectory(output);
            foreach (string theme in new[] { "dark", "light", "retro-sage" })
            {
                AppThemeManager.Apply(Application.Current, theme, false);
                var source = new FakeLookup();
                source.ReadApps = (_, _) => Task.FromResult(new[] { AppRow("io.first"), AppRow("net.second") }
                    .Concat(Enumerable.Range(1, 30).Select(index => AppRow("net.long.application.name.with.many.segments." + index))).ToList());
                source.ReadProcesses = (device, _) =>
                {
                    var foreground = Process(10, "io.first", device.Udid);
                    foreground.ForegroundApplication = true;
                    return Task.FromResult(new[] { foreground, Process(11, "net.second", device.Udid) }
                        .Concat(Enumerable.Range(1, 30).Select(index => Process(100 + index,
                            "net.long.application.name.with.many.segments." + index + ":background_worker_with_a_long_name", device.Udid))).ToList());
                };
                var window = new DevicePickerWindow(null, source);
                window.Show();
                try
                {
                    await Until(() => window.FindControl<ListBox>("ProcessList").SelectedItem != null);
                    foreach (int width in new[] { 680, 900 })
                    {
                        window.Width = width;
                        window.UpdateLayout();
                        await Task.Delay(30);
                        using var bitmap = new RenderTargetBitmap(new PixelSize(width, 640));
                        bitmap.Render(window);
                        bitmap.Save(Path.Combine(output, theme + "-" + width + ".png"), PngBitmapEncoderOptions.Default);
                        Assert.True(window.FindControl<Button>("ConfirmButton").Bounds.Width > 0);
                        var headers = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Text == "io.first · 当前前台").ToArray();
                        Assert.NotEmpty(headers);
                        Assert.All(headers, text => Assert.True(text.Bounds.Width <= width));
                        window.FindControl<Button>("LaunchTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        using var appsBitmap = new RenderTargetBitmap(new PixelSize(width, 640));
                        appsBitmap.Render(window);
                        appsBitmap.Save(Path.Combine(output, theme + "-apps-" + width + ".png"), PngBitmapEncoderOptions.Default);
                        window.FindControl<Button>("ProcessTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                }
                finally { window.Close(); }
            }
            AppThemeManager.Apply(Application.Current, "dark", false);
        });

        [Fact]
        public Task NonWechatWebKitRequiresVerifiedOwnerAndRefreshKeepsManualApp() => OnUi(async () =>
        {
            var source = new FakeLookup();
            source.ReadProcesses = (_, _) => Task.FromResult(new List<ProcessInfo>
            {
                new ProcessInfo { Pid = 7, Platform = "ios", Name = "io.first", BundleId = "io.first", ForegroundApplication = true },
                new ProcessInfo { Pid = 8, Platform = "ios", Name = "com.apple.WebKit.WebContent", OwnerBundleId = "io.first", OwnerPid = 7, OwnershipVerified = true },
                new ProcessInfo { Pid = 9, Platform = "ios", Name = "com.apple.WebKit.GPU", OwnerBundleId = "io.first", OwnerPid = 7 },
                new ProcessInfo { Pid = 12, Platform = "ios", Name = "com.apple.WebKit.Networking", OwnerBundleId = "io.first", OwnerPid = 7, OwnershipVerified = true, OwnershipAmbiguous = true }
            });
            source.Report.Devices[0].Platform = "ios";
            var window = new DevicePickerWindow(null, source);
            window.Show();
            try
            {
                var list = window.FindControl<ListBox>("ProcessList");
                await Until(() => list.Items.Count == 2);
                Assert.Contains(list.Items.Cast<ProcessInfo>(), process => process.Pid == 8);
                Assert.DoesNotContain(list.Items.Cast<ProcessInfo>(), process => process.Pid == 9);
                Assert.DoesNotContain(list.Items.Cast<ProcessInfo>(), process => process.Pid == 12);
                window.FindControl<Button>("LaunchTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var apps = window.FindControl<ListBox>("AppList");
                apps.SelectedItem = apps.Items.Cast<AppInfo>().Single(app => app.BundleId == "net.second");
                await window.LoadTargetsAsync();
                Assert.Equal("net.second", ((AppInfo)apps.SelectedItem).BundleId);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task FastProcessesRemainSelectableWhenSlowAppsFail() => OnUi(async () =>
        {
            var source = new FakeLookup();
            var apps = new TaskCompletionSource<List<AppInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.ReadApps = (_, _) => apps.Task;
            var window = new DevicePickerWindow(null, source);
            window.Show();
            try
            {
                var list = window.FindControl<ListBox>("ProcessList");
                await Until(() => list.SelectedItem != null);
                Assert.True(window.FindControl<Button>("ConfirmButton").IsEnabled);
                apps.SetException(new IOException("application permission denied"));
                await Until(() => window.FindControl<TextBlock>("DeviceCountText").Text.Contains("application permission denied"));
                Assert.Equal(10, ((ProcessInfo)list.SelectedItem).Pid);
                Assert.True(window.FindControl<Button>("ConfirmButton").IsEnabled);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task RefreshPreservesManualDeviceAndProcessButRejectsReusedPid() => OnUi(async () =>
        {
            var source = new FakeLookup();
            source.Report.Devices.Add(Device("B"));
            var window = new DevicePickerWindow(null, source);
            window.Show();
            try
            {
                var devices = window.FindControl<ComboBox>("DeviceList");
                await Until(() => devices.SelectedItem != null);
                devices.SelectedItem = source.Report.Devices[1];
                await window.LoadTargetsAsync();
                await window.LoadDevicesAsync();
                Assert.Equal("B", ((DeviceInfo)devices.SelectedItem).Udid);
                var list = window.FindControl<ListBox>("ProcessList");
                list.SelectedItem = list.Items.Cast<ProcessInfo>().Single(item => item.Pid == 11);
                await window.LoadTargetsAsync();
                Assert.Equal(11, ((ProcessInfo)list.SelectedItem).Pid);
                source.StartTicks = 101;
                await window.LoadTargetsAsync();
                Assert.Null(list.SelectedItem);
                Assert.False(window.FindControl<Button>("ConfirmButton").IsEnabled);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task DisconnectDoesNotSwitchToAnotherDeviceAndNewDeviceDoesNotStealSelection() => OnUi(async () =>
        {
            var source = new FakeLookup();
            var window = new DevicePickerWindow(null, source);
            window.Show();
            try
            {
                await Until(() => window.FindControl<ListBox>("ProcessList").SelectedItem != null);
                source.Report.Devices.Add(Device("B"));
                await window.LoadDevicesAsync(monitor: true);
                Assert.Equal("A", ((DeviceInfo)window.FindControl<ComboBox>("DeviceList").SelectedItem).Udid);
                source.Report.Devices.RemoveAt(0);
                source.Online = false;
                await window.LoadDevicesAsync(monitor: true);
                Assert.Null(window.FindControl<ComboBox>("DeviceList").SelectedItem);
                Assert.Empty(window.FindControl<ListBox>("ProcessList").Items);
                Assert.False(window.FindControl<Button>("ConfirmButton").IsEnabled);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task AppSelectionWorksWhenProcessesFailAndOfflineConfirmIsBlocked() => OnUi(async () =>
        {
            var source = new FakeLookup { ReadProcesses = (_, _) => Task.FromException<List<ProcessInfo>>(new IOException("process denied")) };
            var window = new DevicePickerWindow(null, source);
            window.Show();
            try
            {
                await Until(() => window.FindControl<Button>("LaunchButton").IsEnabled);
                Assert.False(window.FindControl<Button>("ConfirmButton").IsEnabled);
                source.ReadProcesses = null;
                await window.LoadTargetsAsync();
                window.FindControl<Button>("LaunchTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                source.Online = false;
                window.FindControl<Button>("ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => window.FindControl<TextBlock>("DeviceCountText").Text.Contains("已离线"));
                Assert.Null(window.FindControl<ListBox>("ProcessList").SelectedItem);
                Assert.False(window.FindControl<Button>("ConfirmButton").IsEnabled);
                source.Online = true;
                await window.LoadTargetsAsync();
                Assert.True(window.FindControl<Button>("ConfirmButton").IsEnabled);
            }
            finally { window.Close(); }
        });

        [Fact]
        public Task LateResultsCannotRestoreThePreviousDeviceAndClosingCancelsDiscovery() => OnUi(async () =>
        {
            var source = new FakeLookup();
            source.Report.Devices.Add(Device("B"));
            var late = new TaskCompletionSource<List<ProcessInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.ReadProcesses = (device, _) => device.Udid == "A" ? late.Task : Task.FromResult(new List<ProcessInfo> { Process(22, "io.second", "B") });
            var window = new DevicePickerWindow(null, source);
            window.Show();
            await Until(() => window.FindControl<ComboBox>("DeviceList").SelectedItem != null);
            window.FindControl<ComboBox>("DeviceList").SelectedItem = source.Report.Devices[1];
            await Until(() => (window.FindControl<ListBox>("ProcessList").SelectedItem as ProcessInfo)?.Pid == 22);
            late.SetResult(new List<ProcessInfo> { Process(99, "io.stale") });
            await Task.Delay(30);
            Assert.Equal(22, ((ProcessInfo)window.FindControl<ListBox>("ProcessList").SelectedItem).Pid);
            window.Close();
            Assert.True(source.DiscoveryToken.IsCancellationRequested);
        });

        private sealed class FakeLookup : IDevicePickerLookup
        {
            public DeviceDiscoveryReport Report = new DeviceDiscoveryReport();
            public Func<DeviceInfo, CancellationToken, Task<List<AppInfo>>> ReadApps;
            public Func<DeviceInfo, CancellationToken, Task<List<ProcessInfo>>> ReadProcesses;
            public long StartTicks = 100;
            public bool? Online = true;
            public CancellationToken DiscoveryToken;
            public Func<CancellationToken, Action<DeviceDiscoveryReport>, Task<DeviceDiscoveryReport>> Discovery;
            public FakeLookup() { Report.Devices.Add(Device("A")); }
            public Task<DeviceDiscoveryReport> DiscoverDevicesAsync(CancellationToken token, Action<DeviceDiscoveryReport> onProgress = null, bool includeDetails = true)
            {
                DiscoveryToken = token;
                return Discovery == null ? Task.FromResult(Report) : Discovery(token, onProgress);
            }
            public Task<List<AppInfo>> ListAppsAsync(DeviceInfo device, CancellationToken token) => ReadApps != null
                ? ReadApps(device, token) : Task.FromResult(new List<AppInfo> { AppRow("io.first"), AppRow("net.second") });
            public Task<List<ProcessInfo>> ListProcessesAsync(DeviceInfo device, CancellationToken token)
            {
                if (ReadProcesses != null) return ReadProcesses(device, token);
                var first = Process(10, "io.first", device.Udid); first.ForegroundApplication = true;
                var second = Process(11, "net.second", device.Udid); second.AndroidStartTimeTicks = StartTicks;
                return Task.FromResult(new List<ProcessInfo> { first, second });
            }
            public Task<HarmonyTargetInventory> ListTargetsAsync(DeviceInfo device, CancellationToken token) => throw new InvalidOperationException();
            public Task<ProcessResult> LaunchAppAsync(DeviceInfo device, AppInfo app, CancellationToken token) => Task.FromResult(new ProcessResult(0, "", ""));
            public Task HydrateAppIconsAsync(DeviceInfo device, IList<AppInfo> apps, IList<ProcessInfo> processes, int maxIcons, CancellationToken token) => Task.CompletedTask;
            public Task<bool?> IsDeviceOnlineAsync(string udid, string platform, CancellationToken token) => Task.FromResult(Online);
        }
    }
}
