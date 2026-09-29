using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class DeviceDiagnosticsPresentationTests
    {
        [Theory]
        [InlineData("未找到 HDC 运行组件，请安装官方鸿蒙 SDK。")]
        [InlineData("未找到或无法启动 HDC。请配置 PATH 或 MOTUPERF_HDC。")]
        public void MissingHdcHasActionableSummaryAndPreservesDetail(string reason)
        {
            var snapshot = DeviceDiagnosticsFormatter.FromReport(new DeviceDiscoveryReport { HarmonyDiagnostic = reason });
            Assert.Equal("需要准备鸿蒙连接工具", snapshot.HarmonySummary);
            Assert.Equal(reason, snapshot.HarmonyDiagnostic);
            Assert.True(snapshot.HarmonyHdcActionAvailable);
        }

        [Fact]
        public void HarmonyDeviceDoesNotIncreaseIosCount()
        {
            var report = new DeviceDiscoveryReport();
            report.Devices.Add(new DeviceInfo { Platform = "harmony" });
            var snapshot = DeviceDiagnosticsFormatter.FromReport(report);
            Assert.Equal("已连接 1 台设备", snapshot.HarmonySummary);
            Assert.Equal("未发现设备", snapshot.IosSummary);
        }

        [Fact]
        public void EmptyReportStaysCompactWhileKeepingFullPlatformReasons()
        {
            DeviceDiscoveryReport report = new DeviceDiscoveryReport
            {
                AndroidDiagnostic = "设备未授权，请在手机上允许 USB 调试。",
                IosDiagnostic = "未检测到 iOS 设备，请解锁并信任此电脑。"
            };

            DeviceDiagnosticsSnapshot snapshot = DeviceDiagnosticsFormatter.FromReport(report);

            Assert.Equal("未检测到设备", snapshot.OverallStatus);
            Assert.Equal("需要开启 USB 调试并授权", snapshot.AndroidSummary);
            Assert.Equal("需要信任设备并解锁", snapshot.IosSummary);
            Assert.Contains("USB 调试", snapshot.AndroidDiagnostic);
            Assert.Contains("信任此电脑", snapshot.IosDiagnostic);
        }

        [Fact]
        public void ConnectedDevicesUseCountsAndMissingAppleDriverRemainsActionable()
        {
            DeviceDiscoveryReport report = new DeviceDiscoveryReport
            {
                AppleDriverMissing = true,
                AppleDriverActionAvailable = true,
                IosDiagnostic = "Apple 移动设备驱动未安装。"
            };
            report.Devices.Add(new DeviceInfo { Platform = "android" });

            DeviceDiagnosticsSnapshot snapshot = DeviceDiagnosticsFormatter.FromReport(report);

            Assert.Equal("已连接 1 台设备", snapshot.OverallStatus);
            Assert.Equal("已连接 1 台设备", snapshot.AndroidSummary);
            Assert.Equal("缺少 Apple 设备驱动", snapshot.IosSummary);
            Assert.True(snapshot.AppleDriverMissing);
            Assert.True(snapshot.AppleDriverActionAvailable);
        }

        [Fact]
        public void HarmonyPermissionFailureDoesNotOfferHdcInstall()
        {
            var snapshot = DeviceDiagnosticsFormatter.FromReport(new DeviceDiscoveryReport
            {
                HarmonyDiagnostic = "设备未授权，请在设备上允许 HDC 调试。"
            });

            Assert.Equal("需要开启 HDC 调试并授权", snapshot.HarmonySummary);
            Assert.False(snapshot.HarmonyHdcActionAvailable);
        }

        [Fact]
        public void HarmonyHdcSetupKeepsOfficialDownloadAndGuideUrls()
        {
            Assert.StartsWith("https://", HarmonyHdcSetupWindow.HdcDownloadUrl);
            Assert.Contains("developer.huawei.com", HarmonyHdcSetupWindow.HdcDownloadUrl);
            Assert.Contains("developtools_hdc", HarmonyHdcSetupWindow.HdcGuideUrl);
        }
    }
}
