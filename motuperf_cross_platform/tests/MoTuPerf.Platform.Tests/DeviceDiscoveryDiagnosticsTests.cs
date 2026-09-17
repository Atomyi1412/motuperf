using System;
using System.IO;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class DeviceDiscoveryDiagnosticsTests
    {
        [Fact]
        public void AndroidMixedStatesRetainOnlineDevicesAndExplainBlockedDevices()
        {
            PlatformDeviceDiscovery report = AndroidLookupService.ParseDiscovery(new ProcessResult(0,
                "List of devices attached\nonline123 device product:test model:Pixel_8\nlocked123 unauthorized\nlost123 offline\n", ""));
            Assert.Equal("online123", Assert.Single(report.Devices).Udid);
            Assert.Contains("未授权", report.Diagnostic);
            Assert.Contains("离线", report.Diagnostic);
            Assert.DoesNotContain("locked123", report.Diagnostic);
        }

        [Theory]
        [InlineData("", "未发现")]
        [InlineData("abc unauthorized", "未授权")]
        [InlineData("abc offline", "离线")]
        [InlineData("abc recovery", "不可采集")]
        [InlineData("abc no permissions", "权限")]
        public void AndroidBlockedStatesAreNotReportedAsUsableDevices(string text, string expected)
        {
            PlatformDeviceDiscovery result = AndroidLookupService.ParseDiscovery(new ProcessResult(0, "List of devices attached\n" + text, ""));
            Assert.Empty(result.Devices);
            Assert.Contains(expected, result.Diagnostic);
        }

        [Fact]
        public void DiscoveryFailuresDistinguishTimeoutRuntimeAndAdbExit()
        {
            Assert.Contains("超时", AndroidLookupService.DescribeDiscoveryException(new TimeoutException()));
            Assert.Contains("运行组件", AndroidLookupService.DescribeDiscoveryException(new FileNotFoundException()));
            Assert.Contains("超时", RuntimeTools.DescribeIosException(new TimeoutException()));
            PlatformDeviceDiscovery result = AndroidLookupService.ParseDiscovery(new ProcessResult(1, "", "server failed token=secret"));
            Assert.Contains("退出码 1", result.Diagnostic);
            Assert.DoesNotContain("secret", result.Diagnostic);
        }

        [Theory]
        [InlineData("InvalidHostID", "信任")]
        [InlineData("password protected", "锁定")]
        [InlineData("Developer mode is disabled", "开发者模式")]
        [InlineData("socket timed out", "超时")]
        [InlineData("No module named pymobiledevice3", "组件不完整")]
        public void IosFailuresProvideActionableGuidance(string error, string expected)
        {
            Assert.Contains(expected, RuntimeTools.DescribeIosFailure(new ProcessResult(1, "", error)));
        }
    }
}
