using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpIosPerfMonitor
{
    public sealed class DeviceLookupService
    {
        private readonly IosLookupService _ios = new IosLookupService();
        private readonly AndroidLookupService _android = new AndroidLookupService();
        private readonly HarmonyLookupService _harmony = new HarmonyLookupService();

        public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken token)
        {
            DeviceDiscoveryReport report = await DiscoverDevicesAsync(token);
            return report.Devices;
        }

        public async Task<DeviceDiscoveryReport> DiscoverDevicesAsync(CancellationToken token)
        {
            Task<PlatformDeviceDiscovery> iosTask = _ios.DiscoverDevicesAsync(token);
            Task<PlatformDeviceDiscovery> androidTask = _android.DiscoverDevicesAsync(token);
            Task<PlatformDeviceDiscovery> harmonyTask = _harmony.DiscoverDevicesAsync(token);
            await Task.WhenAll(iosTask, androidTask, harmonyTask);
            DeviceDiscoveryReport report = new DeviceDiscoveryReport
            {
                IosDiagnostic = iosTask.Result.Diagnostic,
                AndroidDiagnostic = androidTask.Result.Diagnostic,
                HarmonyDiagnostic = harmonyTask.Result.Diagnostic,
                AppleDriverMissing = iosTask.Result.AppleDriverMissing,
                AppleDriverActionAvailable = iosTask.Result.AppleDriverActionAvailable
            };
            report.Devices.AddRange(iosTask.Result.Devices);
            report.Devices.AddRange(androidTask.Result.Devices);
            report.Devices.AddRange(harmonyTask.Result.Devices);
            for (int i = 0; i < report.Devices.Count; i++)
            {
                report.Devices[i].Recommended = i == 0;
            }
            return report;
        }

        public Task<List<AppInfo>> ListAppsAsync(DeviceInfo device, CancellationToken token)
        {
            if (IsHarmony(device)) return _harmony.ListAppsAsync(device == null ? "" : device.Udid, token);
            if (IsAndroid(device)) return _android.ListAppsAsync(device == null ? "" : device.Udid, token);
            return _ios.ListAppsAsync(
                device == null ? "" : device.Udid,
                device == null ? "" : device.ProductVersion,
                token);
        }

        public Task<List<ProcessInfo>> ListProcessesAsync(DeviceInfo device, CancellationToken token)
        {
            if (IsHarmony(device)) return _harmony.ListProcessesAsync(device == null ? "" : device.Udid, token);
            if (IsAndroid(device)) return _android.ListProcessesAsync(device == null ? "" : device.Udid, token);
            return _ios.ListProcessesAsync(
                device == null ? "" : device.Udid,
                device == null ? "" : device.ProductVersion,
                token);
        }

        public Task<HarmonyTargetInventory> ListTargetsAsync(DeviceInfo device, CancellationToken token)
        {
            if (!IsHarmony(device)) return Task.FromResult<HarmonyTargetInventory>(null);
            return _harmony.ListTargetsAsync(device == null ? "" : device.Udid, token);
        }

        public Task<bool> IsAndroidHomeProcessAsync(DeviceInfo device, ProcessInfo process, CancellationToken token)
        {
            if (IsHarmony(device)) return Task.FromResult(false);
            if (!IsAndroid(device)) return Task.FromResult(false);
            return _android.IsHomeProcessAsync(device == null ? "" : device.Udid, process, token);
        }

        public Task<bool?> IsDeviceOnlineAsync(string udid, string platform, CancellationToken token)
        {
            if (IsHarmony(platform)) return _harmony.ProbeOnlineAsync(udid, token);
            return IsAndroid(platform)
                ? _android.ProbeOnlineAsync(udid, token)
                : _ios.ProbeOnlineAsync(udid, token);
        }

        public async Task<ProcessResult> LaunchAppAsync(DeviceInfo device, AppInfo app, CancellationToken token)
        {
            if (app == null || string.IsNullOrWhiteSpace(app.BundleId))
            {
                return new ProcessResult(1, "", "未选择有效的 APP。");
            }
            if (device == null || string.IsNullOrWhiteSpace(device.Udid))
            {
                return new ProcessResult(1, "", "未选择有效的设备。");
            }
            if (IsHarmony(device))
            {
                return await _harmony.LaunchAppAsync(device.Udid, app, token);
            }
            if (!IsAndroid(device))
            {
                return await _ios.LaunchAppAsync(device.Udid, device.ProductVersion, app.BundleId, token);
            }
            return await _android.LaunchAppAsync(device.Udid, app.BundleId, token);
        }

        public async Task HydrateAppIconsAsync(DeviceInfo device, IList<AppInfo> apps, IList<ProcessInfo> processes, int maxIcons, CancellationToken token)
        {
            if (IsHarmony(device))
            {
                await _harmony.HydrateAppIconsAsync(device == null ? "" : device.Udid, apps, processes, maxIcons, token);
                return;
            }
            if (!IsAndroid(device)) return;
            await _android.HydrateAppIconsAsync(device == null ? "" : device.Udid, apps, processes, maxIcons, token);
        }

        public static bool IsAndroid(DeviceInfo device)
        {
            return string.Equals(device == null ? "" : device.Platform, "android", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAndroid(string platform)
        {
            return string.Equals(platform ?? "", "android", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsHarmony(DeviceInfo device)
        {
            return IsHarmony(device == null ? "" : device.Platform);
        }

        public static bool IsHarmony(string platform)
        {
            return string.Equals(platform ?? "", "harmony", StringComparison.OrdinalIgnoreCase)
                || string.Equals(platform ?? "", "openharmony", StringComparison.OrdinalIgnoreCase)
                || string.Equals(platform ?? "", "harmonyos", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsIos(DeviceInfo device)
        {
            return string.Equals(device == null ? "" : device.Platform, "ios", StringComparison.OrdinalIgnoreCase);
        }

    }
}
