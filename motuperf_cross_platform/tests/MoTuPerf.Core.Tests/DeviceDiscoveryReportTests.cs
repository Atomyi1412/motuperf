using Xunit;

namespace MoTuPerf.Core.Tests
{
    public sealed class DeviceDiscoveryReportTests
    {
        [Fact]
        public void EmptyReportUsesPlatformNeutralStatusMessage()
        {
            CSharpIosPerfMonitor.DeviceDiscoveryReport report = new CSharpIosPerfMonitor.DeviceDiscoveryReport
            {
                IosDiagnostic = "未检测到 iOS 设备，请解锁设备。",
                AndroidDiagnostic = "未检测到设备，请开启 USB 调试。"
            };

            Assert.Equal("未检测到设备，请连接设备、检查授权后刷新。", report.StatusMessage);
            Assert.DoesNotContain("iOS", report.StatusMessage);
            Assert.DoesNotContain("Android", report.StatusMessage);
        }

        [Fact]
        public void DetectedDevicesKeepCountStatusMessage()
        {
            CSharpIosPerfMonitor.DeviceDiscoveryReport report = new CSharpIosPerfMonitor.DeviceDiscoveryReport();
            report.Devices.Add(new CSharpIosPerfMonitor.DeviceInfo { Platform = "android" });

            Assert.Equal("已检测到设备：1", report.StatusMessage);
        }

        [Fact]
        public void WindowsAppleDriverRepairCanBeOfferedEvenWhenDriverIsInstalled()
        {
            CSharpIosPerfMonitor.DeviceDiscoveryReport report = new CSharpIosPerfMonitor.DeviceDiscoveryReport
            {
                AppleDriverMissing = false,
                AppleDriverActionAvailable = true,
                IosDiagnostic = "Apple 移动设备驱动已安装，但 Apple Mobile Device 服务未运行。"
            };

            Assert.True(report.AppleDriverActionAvailable);
            Assert.False(report.AppleDriverMissing);
            Assert.Contains("服务未运行", report.IosDiagnostic);
        }

        [Fact]
        public void InvalidProcessTargetsAreRejectedBeforeCapture()
        {
            Assert.False(CSharpIosPerfMonitor.ProcessTargetMatcher.IsValidTarget(null));
            Assert.False(CSharpIosPerfMonitor.ProcessTargetMatcher.IsValidTarget(new CSharpIosPerfMonitor.ProcessInfo { Name = "WeChat" }));
            Assert.False(CSharpIosPerfMonitor.ProcessTargetMatcher.IsValidTarget(new CSharpIosPerfMonitor.ProcessInfo { Pid = 6712 }));
            Assert.True(CSharpIosPerfMonitor.ProcessTargetMatcher.IsValidTarget(new CSharpIosPerfMonitor.ProcessInfo { Pid = 6712, Name = "WeChat" }));
        }

        [Fact]
        public void HarmonyBundlePidRemainsValidWhenExecutableNameIsUnreadable()
        {
            var process = new CSharpIosPerfMonitor.ProcessInfo
            {
                Pid = 6712,
                Platform = "harmony",
                BundleId = "com.example.game",
                OwnerBundleId = "com.example.game",
                OwnershipVerified = true,
                HarmonyUserId = 100
            };

            Assert.True(CSharpIosPerfMonitor.ProcessTargetMatcher.IsValidTarget(process));
            process.OwnershipAmbiguous = true;
            Assert.False(CSharpIosPerfMonitor.ProcessTargetMatcher.IsValidTarget(process));
        }

        [Fact]
        public void HarmonyProcessReuseRequiresTheSelectedIdentity()
        {
            var selected = new CSharpIosPerfMonitor.ProcessInfo
            {
                Pid = 701,
                Name = "com.example.game",
                BundleId = "com.example.game",
                HarmonyUserId = 100,
                HarmonyStartTimeTicks = 12345
            };

            Assert.True(CSharpIosPerfMonitor.ProcessTargetMatcher.SameHarmonyProcessInstance(
                new CSharpIosPerfMonitor.ProcessInfo
                {
                    Pid = 701,
                    Name = "com.example.game",
                    BundleId = "com.example.game",
                    HarmonyUserId = 100,
                    HarmonyStartTimeTicks = 12345
                }, selected));
            Assert.False(CSharpIosPerfMonitor.ProcessTargetMatcher.SameHarmonyProcessInstance(
                new CSharpIosPerfMonitor.ProcessInfo
                {
                    Pid = 701,
                    Name = "com.example.game",
                    BundleId = "com.example.game",
                    HarmonyUserId = 100,
                    HarmonyStartTimeTicks = 12346
                }, selected));
            Assert.False(CSharpIosPerfMonitor.ProcessTargetMatcher.SameHarmonyProcessInstance(
                new CSharpIosPerfMonitor.ProcessInfo
                {
                    Pid = 701,
                    Name = "com.example.game",
                    BundleId = "com.example.game",
                    HarmonyUserId = 0,
                    HarmonyStartTimeTicks = 12345
                }, selected));
            Assert.False(CSharpIosPerfMonitor.ProcessTargetMatcher.SameHarmonyProcessInstance(
                new CSharpIosPerfMonitor.ProcessInfo
                {
                    Pid = 701,
                    Name = "com.example.game:render",
                    BundleId = "com.example.game",
                    HarmonyUserId = 100,
                    HarmonyStartTimeTicks = 12345
                }, selected));
        }
    }
}
