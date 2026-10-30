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
            bool isReady,
            bool harmonyHdcActionAvailable = false)
            : this(overallStatus, androidSummary, iosSummary, "未发现设备", androidDiagnostic, iosDiagnostic, "本轮没有返回额外诊断。", appleDriverMissing, appleDriverActionAvailable, isReady, harmonyHdcActionAvailable)
        {
        }

        public DeviceDiagnosticsSnapshot(
            string overallStatus,
            string androidSummary,
            string iosSummary,
            string harmonySummary,
            string androidDiagnostic,
            string iosDiagnostic,
            string harmonyDiagnostic,
            bool appleDriverMissing,
            bool appleDriverActionAvailable,
            bool isReady,
            bool harmonyHdcActionAvailable = false)
        {
            OverallStatus = overallStatus ?? "";
            AndroidSummary = androidSummary ?? "";
            IosSummary = iosSummary ?? "";
            HarmonySummary = harmonySummary ?? "";
            AndroidDiagnostic = androidDiagnostic ?? "";
            IosDiagnostic = iosDiagnostic ?? "";
            HarmonyDiagnostic = harmonyDiagnostic ?? "";
            AppleDriverMissing = appleDriverMissing;
            AppleDriverActionAvailable = appleDriverActionAvailable;
            HarmonyHdcActionAvailable = harmonyHdcActionAvailable;
            IsReady = isReady;
        }

        public string OverallStatus { get; }
        public string AndroidSummary { get; }
        public string IosSummary { get; }
        public string HarmonySummary { get; }
        public string AndroidDiagnostic { get; }
        public string IosDiagnostic { get; }
        public string HarmonyDiagnostic { get; }
        public bool AppleDriverMissing { get; }
        public bool AppleDriverActionAvailable { get; }
        public bool HarmonyHdcActionAvailable { get; }
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
                "检测中...",
                "",
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
            int harmonyCount = devices.Count(DeviceLookupService.IsHarmony);
            int iosCount = devices.Count(DeviceLookupService.IsIos);
            return new DeviceDiagnosticsSnapshot(
                devices.Count == 0 ? "未检测到设备" : "已连接 " + devices.Count + " 台设备",
                PlatformSummary("Android", androidCount, report.AndroidDiagnostic, false),
                PlatformSummary("iOS", iosCount, report.IosDiagnostic, report.AppleDriverMissing),
                PlatformSummary("鸿蒙", harmonyCount, report.HarmonyDiagnostic, false),
                EmptyDiagnostic(report.AndroidDiagnostic),
                EmptyDiagnostic(report.IosDiagnostic),
                EmptyDiagnostic(report.HarmonyDiagnostic),
                report.AppleDriverMissing,
                report.AppleDriverActionAvailable,
                true,
                IsHarmonyHdcMissing(report.HarmonyDiagnostic));
        }

        public static DeviceDiagnosticsSnapshot Error(string overallStatus, string androidDiagnostic, string iosDiagnostic)
        {
            return new DeviceDiagnosticsSnapshot(
                overallStatus,
                "检测失败，可查看详情",
                "检测失败，可查看详情",
                "检测失败，可查看详情",
                EmptyDiagnostic(androidDiagnostic),
                EmptyDiagnostic(iosDiagnostic),
                "检测失败，可查看详情",
                false,
                false,
                true);
        }

        private static string PlatformSummary(string platform, int count, string diagnostic, bool driverMissing)
        {
            if (count > 0) return "已连接 " + count + " 台设备";
            if (driverMissing) return "缺少 Apple 设备驱动";
            if (platform == "鸿蒙" && ContainsAny(diagnostic, "未找到 HDC", "未找到或无法启动 HDC")) return "需要准备鸿蒙连接工具";
            if (ContainsAny(diagnostic, "unauthorized", "未授权", "授权", "信任", "锁屏", "开发者模式", "USB 调试"))
            {
                return platform == "鸿蒙" ? "需要开启 HDC 调试并授权" : platform == "Android" ? "需要开启 USB 调试并授权" : "需要信任设备并解锁";
            }
            if (ContainsAny(diagnostic, "超时", "组件", "服务", "ADB", "HDC", "连接", "读取失败", "检测失败"))
            {
                return "检测异常，可查看详情";
            }
            return "未发现设备";
        }

        internal static bool IsHarmonyHdcMissing(string diagnostic)
        {
            return ContainsAny(diagnostic,
                "未找到 HDC",
                "未找到或无法启动 HDC",
                "未找到 HDC 运行组件");
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
