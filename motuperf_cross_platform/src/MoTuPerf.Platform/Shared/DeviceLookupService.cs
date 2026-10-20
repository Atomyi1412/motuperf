using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpIosPerfMonitor
{
    public interface IDevicePickerLookup
    {
        Task<DeviceDiscoveryReport> DiscoverDevicesAsync(CancellationToken token, Action<DeviceDiscoveryReport> onProgress = null, bool includeDetails = true);
        Task<List<AppInfo>> ListAppsAsync(DeviceInfo device, CancellationToken token);
        Task<List<ProcessInfo>> ListProcessesAsync(DeviceInfo device, CancellationToken token);
        Task<HarmonyTargetInventory> ListTargetsAsync(DeviceInfo device, CancellationToken token);
        Task<ProcessResult> LaunchAppAsync(DeviceInfo device, AppInfo app, CancellationToken token);
        Task HydrateAppIconsAsync(DeviceInfo device, IList<AppInfo> apps, IList<ProcessInfo> processes, int maxIcons, CancellationToken token);
        Task<bool?> IsDeviceOnlineAsync(string udid, string platform, CancellationToken token);
    }

    public sealed class DeviceLookupService : IDevicePickerLookup
    {
        private readonly IosLookupService _ios = new IosLookupService();
        private readonly AndroidLookupService _android = new AndroidLookupService();
        private readonly HarmonyLookupService _harmony = new HarmonyLookupService();

        public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken token)
        {
            DeviceDiscoveryReport report = await DiscoverDevicesAsync(token);
            return report.Devices;
        }

        public async Task<DeviceDiscoveryReport> DiscoverDevicesAsync(CancellationToken token,
            Action<DeviceDiscoveryReport> onProgress = null, bool includeDetails = true)
        {
            PlatformDeviceDiscovery ios = new PlatformDeviceDiscovery();
            PlatformDeviceDiscovery android = new PlatformDeviceDiscovery();
            PlatformDeviceDiscovery harmony = new PlatformDeviceDiscovery();
            object gate = new object();
            DeviceDiscoveryReport Snapshot()
            {
                var snapshot = new DeviceDiscoveryReport
                {
                    IosDiagnostic = ios.Diagnostic, AndroidDiagnostic = android.Diagnostic,
                    HarmonyDiagnostic = harmony.Diagnostic, AppleDriverMissing = ios.AppleDriverMissing,
                    AppleDriverActionAvailable = ios.AppleDriverActionAvailable
                };
                snapshot.Devices.AddRange(ios.Devices);
                snapshot.Devices.AddRange(android.Devices);
                snapshot.Devices.AddRange(harmony.Devices);
                foreach (DeviceInfo device in snapshot.Devices) device.Recommended = false;
                return snapshot;
            }
            void Publish(int platform, PlatformDeviceDiscovery result)
            {
                lock (gate)
                {
                    if (platform == 0) ios = result;
                    else if (platform == 1) android = result;
                    else harmony = result;
                    if (!token.IsCancellationRequested) onProgress?.Invoke(Snapshot());
                }
            }
            async Task Run(int platform, Task<PlatformDeviceDiscovery> query)
            {
                Publish(platform, await query);
            }
            await Task.WhenAll(
                Run(0, _ios.DiscoverDevicesAsync(token, includeDetails, result => Publish(0, result))),
                Run(1, _android.DiscoverDevicesAsync(token, includeDetails, result => Publish(1, result))),
                Run(2, _harmony.DiscoverDevicesAsync(token, includeDetails, result => Publish(2, result))));
            lock (gate) return Snapshot();
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
