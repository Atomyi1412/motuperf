using System.Linq;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyDeviceDiscoveryTests
    {
        [Fact]
        public void HarmonyDeviceDetailParsersKeepRealCpuGpuAndPhysicalResolution()
        {
            Assert.Equal("Kirin Test 9000", HarmonyLookupService.ParseCpuInfo(
                "processor\t: 0\nHardware\t: Kirin Test 9000\n"));
            Assert.Equal("TestGPU 123", HarmonyLookupService.ParseGpuInfo(
                "GLES: TestGPU 123\nGPU memory: 512 MB\n"));
            Assert.Equal("2560x1600", HarmonyLookupService.ParseResolution(
                "physical resolution=2560x1600, isVirtual=false\n"));
        }

        [Fact]
        public void HarmonyDeviceDetailParsersRejectDiagnosticsAndVirtualOnlyDisplays()
        {
            Assert.Equal("", HarmonyLookupService.ParseCpuInfo("error: permission denied\n"));
            Assert.Equal("", HarmonyLookupService.ParseGpuInfo("screen[0]: isVirtual=true\n"));
            Assert.Equal("", HarmonyLookupService.ParseResolution(
                "screen[0]: resolution=400x600, isVirtual=true\n"));
        }

        [Theory]
        [InlineData("device not connected", "未连接")]
        [InlineData("[Fail] Device unauthorised", "未授权")]
        [InlineData("error: not authorized", "未授权")]
        [InlineData("permission denied", "权限")]
        [InlineData("no permissions", "权限")]
        [InlineData("device offline", "离线")]
        [InlineData("no devices", "未连接")]
        [InlineData("error: request timed out", "超时")]
        [InlineData("hdc: command not found", "HDC 运行组件")]
        public void DiagnosticsNeverBecomeDevicesEvenWithZeroExitCode(string text, string hint)
        {
            foreach (bool stderr in new[] { false, true })
            {
                var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(
                    0, stderr ? "" : text, stderr ? text : ""));
                Assert.Empty(report.Devices);
                Assert.Contains(hint, report.Diagnostic);
            }
        }

        [Theory]
        [InlineData("Connected")]
        [InlineData("Unauthorized")]
        [InlineData("warning")]
        [InlineData("ABC123")]
        public void BareStderrWordsCannotSupplyDeviceIdentity(string stderr)
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(0, "", stderr));
            Assert.Empty(report.Devices);
        }

        [Fact]
        public void PartialListsRetainRealTargetsAndActionableErrorsFromBothStreams()
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(1,
                "REAL1 USB Connected localhost hdc\nLEGACY2\n",
                "REAL3 TCP Connected localhost hdc\n[Fail] no permissions\n"));
            Assert.Equal(new[] { "REAL1", "LEGACY2", "REAL3" }, report.Devices.Select(d => d.Udid));
            Assert.Contains("权限", report.Diagnostic);
        }

        [Theory]
        [InlineData("REAL1 USB Unauthorised localhost hdc", "未授权")]
        [InlineData("REAL1 USB Not Connected localhost hdc", "未连接")]
        [InlineData("REAL1 USB Offline localhost hdc", "离线")]
        [InlineData("REAL1 USB No Permissions localhost hdc", "权限")]
        public void NegativeTargetStatesAreNeverConnected(string line, string hint)
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(0, line, ""));
            Assert.Empty(report.Devices);
            Assert.Contains(hint, report.Diagnostic);
        }

        [Fact]
        public void SuccessWordsInDiagnosticTailsCannotCreateDevices()
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(1, "",
                "warning: no target is connected\nwarning connected\nserver started connected\nREAL1 USB Ready connected\n"));
            Assert.Empty(report.Devices);
        }

        [Theory]
        [InlineData("offline-phone USB Connected localhost hdc", "offline-phone")]
        [InlineData("REAL1 USB Connected offline hdc", "REAL1")]
        [InlineData("REAL2 USB Connected offline-metadata", "REAL2")]
        public void CompleteRowUsesStateColumnInsteadOfWordsInSerialOrMetadata(string line, string serial)
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(0, line, ""));
            Assert.Equal(serial, Assert.Single(report.Devices).Udid);
            Assert.Empty(report.Diagnostic);
        }
    }
}
