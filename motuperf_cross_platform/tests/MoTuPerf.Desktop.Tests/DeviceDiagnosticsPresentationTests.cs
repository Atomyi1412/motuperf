using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class DeviceDiagnosticsPresentationTests
    {
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
    }
}
