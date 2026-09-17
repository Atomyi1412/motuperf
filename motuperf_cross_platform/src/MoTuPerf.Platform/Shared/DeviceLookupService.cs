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

        public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken token)
        {
            DeviceDiscoveryReport report = await DiscoverDevicesAsync(token);
            return report.Devices;
        }

        public async Task<DeviceDiscoveryReport> DiscoverDevicesAsync(CancellationToken token)
        {
            Task<PlatformDeviceDiscovery> iosTask = _ios.DiscoverDevicesAsync(token);
            Task<PlatformDeviceDiscovery> androidTask = _android.DiscoverDevicesAsync(token);
            await Task.WhenAll(iosTask, androidTask);
            DeviceDiscoveryReport report = new DeviceDiscoveryReport
            {
                IosDiagnostic = iosTask.Result.Diagnostic,
                AndroidDiagnostic = androidTask.Result.Diagnostic,
                AppleDriverMissing = iosTask.Result.AppleDriverMissing,
                AppleDriverActionAvailable = iosTask.Result.AppleDriverActionAvailable
            };
            report.Devices.AddRange(iosTask.Result.Devices);
            report.Devices.AddRange(androidTask.Result.Devices);
            for (int i = 0; i < report.Devices.Count; i++)
            {
                report.Devices[i].Recommended = i == 0;
            }
            return report;
        }

        public Task<List<AppInfo>> ListAppsAsync(DeviceInfo device, CancellationToken token)
        {
            if (IsAndroid(device)) return _android.ListAppsAsync(device == null ? "" : device.Udid, token);
            return _ios.ListAppsAsync(
                device == null ? "" : device.Udid,
                device == null ? "" : device.ProductVersion,
                token);
        }

        public Task<List<ProcessInfo>> ListProcessesAsync(DeviceInfo device, CancellationToken token)
        {
            if (IsAndroid(device)) return _android.ListProcessesAsync(device == null ? "" : device.Udid, token);
            return _ios.ListProcessesAsync(
                device == null ? "" : device.Udid,
                device == null ? "" : device.ProductVersion,
                token);
        }

        public Task<bool> IsAndroidHomeProcessAsync(DeviceInfo device, ProcessInfo process, CancellationToken token)
        {
            if (!IsAndroid(device)) return Task.FromResult(false);
            return _android.IsHomeProcessAsync(device == null ? "" : device.Udid, process, token);
        }

        public Task<bool?> IsDeviceOnlineAsync(string udid, string platform, CancellationToken token)
        {
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
            if (!IsAndroid(device))
            {
                return await _ios.LaunchAppAsync(device.Udid, device.ProductVersion, app.BundleId, token);
            }
            return await _android.LaunchAppAsync(device.Udid, app.BundleId, token);
        }

        public async Task HydrateAppIconsAsync(DeviceInfo device, IList<AppInfo> apps, IList<ProcessInfo> processes, int maxIcons, CancellationToken token)
        {
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

    }
}
