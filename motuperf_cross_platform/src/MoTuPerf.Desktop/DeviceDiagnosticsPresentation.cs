using System;
using System.Collections.Generic;
using System.Linq;
using CSharpIosPerfMonitor;
using MoTuPerf.Platform;

namespace MoTuPerf.Desktop
{
    internal sealed class DeviceDiagnosticsSnapshot
    {
        public DeviceDiagnosticsSnapshot(
            string overallStatus,
            string androidSummary,
            string iosSummary,
            string androidDiagnostic,
            string iosDiagnostic,
            bool appleDriverMissing,
            bool appleDriverActionAvailable,
            bool isReady)
        {
            OverallStatus = overallStatus ?? "";
            AndroidSummary = androidSummary ?? "";
            IosSummary = iosSummary ?? "";
            AndroidDiagnostic = androidDiagnostic ?? "";
            IosDiagnostic = iosDiagnostic ?? "";
            AppleDriverMissing = appleDriverMissing;
            AppleDriverActionAvailable = appleDriverActionAvailable;
            IsReady = isReady;
        }

        public string OverallStatus { get; }
        public string AndroidSummary { get; }
        public string IosSummary { get; }
        public string AndroidDiagnostic { get; }
        public string IosDiagnostic { get; }
        public bool AppleDriverMissing { get; }
        public bool AppleDriverActionAvailable { get; }
        public bool IsReady { get; }
    }

    internal static class DeviceDiagnosticsFormatter
    {
        public static DeviceDiagnosticsSnapshot Loading()
        {
            return new DeviceDiagnosticsSnapshot(
                "正在检测设备...",
                "检测中...",
                "检测中...",
                "",
                "",
                false,
                false,
                false);
        }

        public static DeviceDiagnosticsSnapshot FromReport(DeviceDiscoveryReport report)
        {
            if (report == null) return Error("设备检测未返回结果。", "设备检测未返回结果。", "设备检测未返回结果。");
            List<DeviceInfo> devices = report.Devices ?? new List<DeviceInfo>();
            int androidCount = devices.Count(DeviceLookupService.IsAndroid);
            int iosCount = devices.Count(delegate(DeviceInfo device) { return !DeviceLookupService.IsAndroid(device); });
            return new DeviceDiagnosticsSnapshot(
                devices.Count == 0 ? "未检测到设备" : "已连接 " + devices.Count + " 台设备",
                PlatformSummary("Android", androidCount, report.AndroidDiagnostic, false),
                PlatformSummary("iOS", iosCount, report.IosDiagnostic, report.AppleDriverMissing),
                EmptyDiagnostic(report.AndroidDiagnostic),
                EmptyDiagnostic(report.IosDiagnostic),
                report.AppleDriverMissing,
                report.AppleDriverActionAvailable,
                true);
        }

        public static DeviceDiagnosticsSnapshot Error(string overallStatus, string androidDiagnostic, string iosDiagnostic)
        {
            return new DeviceDiagnosticsSnapshot(
                overallStatus,
                "检测失败，可查看详情",
                "检测失败，可查看详情",
                EmptyDiagnostic(androidDiagnostic),
                EmptyDiagnostic(iosDiagnostic),
                false,
                false,
                true);
        }

        private static string PlatformSummary(string platform, int count, string diagnostic, bool driverMissing)
        {
            if (count > 0) return "已连接 " + count + " 台设备";
            if (driverMissing) return "缺少 Apple 设备驱动";
            if (ContainsAny(diagnostic, "unauthorized", "未授权", "授权", "信任", "锁屏", "开发者模式", "USB 调试"))
            {
                return platform == "Android" ? "需要开启 USB 调试并授权" : "需要信任设备并解锁";
            }
            if (ContainsAny(diagnostic, "超时", "组件", "服务", "ADB", "连接", "读取失败", "检测失败"))
            {
                return "检测异常，可查看详情";
            }
            return "未发现设备";
        }

        private static string EmptyDiagnostic(string diagnostic)
        {
            return string.IsNullOrWhiteSpace(diagnostic) ? "本轮没有返回额外诊断。" : diagnostic.Trim();
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return terms.Any(term => value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
