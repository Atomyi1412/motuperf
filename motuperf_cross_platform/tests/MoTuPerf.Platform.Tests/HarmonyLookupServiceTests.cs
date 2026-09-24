using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyLookupServiceTests
    {
        [Fact]
        public void ParsesOfficialVerboseAndDefaultListsWithoutDuplicates()
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(0,
                "ABC123 USB Connected localhost hdc\nABC123\nDEF456 USB Ready localhost hdc\n", ""));
            Assert.Single(report.Devices);
            Assert.Equal("ABC123", report.Devices[0].Udid);
            Assert.Contains("ready", report.Diagnostic);
            Assert.Empty(HarmonyLookupService.ParseDiscovery(new ProcessResult(0, "[Empty]", "")).Devices);
            Assert.Empty(HarmonyLookupService.ParseDiscovery(new ProcessResult(0, "[Fail]Device not founded or connected.", "")).Devices);
        }

        [Fact]
        public void NumericUidAndCommandArgumentsAreNotMistakenForPidOrName()
        {
            var rows = HarmonyLookupService.ParseProcesses("UID PID PPID C STIME TTY TIME CMD\n200100 42 1 0 10:00 ? 00:00:00 com.example.game --worker com.other.app\n", "device");
            Assert.Single(rows);
            Assert.Equal(42, rows[0].Pid);
            Assert.Equal("com.example.game", rows[0].Name);
            Assert.Empty(HarmonyLookupService.ParseProcesses("permission denied", "device"));
        }

        [Fact]
        public void NativeAppsAndMainAbilityUseRealBundleOutput()
        {
            var apps = HarmonyLookupService.ParseApps("ID: 100\ncom.example.game\ncom.ohos.settings\ncom.example.game\nerror: denied\n");
            Assert.Equal(2, apps.Count);
            Assert.Equal("EntryAbility", HarmonyLookupService.ParseMainAbility("bundle:\n{\"hapModuleInfos\":[{\"mainElementName\":\"EntryAbility\"}]}"));
            Assert.Equal("", HarmonyLookupService.ParseMainAbility("failed"));
        }

        [Fact]
        public void RemoteArgumentsStayQuotedAndDisplayInfoExcludesVirtualScreens()
        {
            var args = HarmonyLookupService.TargetArgs("serial", "aa", "start", "-a", "test';echo injected");
            Assert.Equal(new[] { "-t", "serial", "shell", "'aa' 'start' '-a' 'test'\"'\"';echo injected'" }, args);
            Assert.Equal("1080x2400", HarmonyLookupService.ParseResolution(
                "screen[0]: id=0, render resolution=1080x2400, physical resolution=1080x2400, isVirtual=false\n"
                + "screen[1]: render resolution=400x600, isVirtual=true\n"));
            Assert.Equal("", HarmonyLookupService.ParseResolution("error code 1080x2400"));
        }

        [Fact]
        public void ParsesConnectedAndUnauthorizedHdcTargets()
        {
            PlatformDeviceDiscovery report = HarmonyLookupService.ParseDiscovery(new ProcessResult(
                0,
                "ABC123\tConnected\nDEF456\tUnauthorized\nGHI789\tOffline\n",
                ""));

            Assert.Single(report.Devices);
            Assert.Equal("ABC123", report.Devices[0].Udid);
            Assert.Equal("harmony", report.Devices[0].Platform);
            Assert.Contains("未授权", report.Diagnostic);
            Assert.Contains("离线", report.Diagnostic);
        }

        [Fact]
        public void ParsesHarmonyProcessRowsAndBundleIds()
        {
            var processes = HarmonyLookupService.ParseProcesses(
                "UID PID PPID C STIME TTY TIME CMD\n"
                + "u0_a1 412 1 0 10:00 ? 00:00:01 com.example.game\n"
                + "u0_a2 413 1 0 10:00 ? 00:00:01 com.example.game:worker\n",
                "ABC123");

            Assert.Equal(2, processes.Count);
            Assert.Equal("com.example.game", processes[0].BundleId);
            Assert.All(processes, process => Assert.Equal("harmony", process.Platform));
            Assert.All(processes, process => Assert.Equal("ABC123", process.DeviceUdid));
        }

        [Fact]
        public void DoesNotTurnHdcFailureIntoAnAndroidOrIosDevice()
        {
            PlatformDeviceDiscovery report = HarmonyLookupService.ParseDiscovery(new ProcessResult(
                1,
                "",
                "hdc: command not found"));

            Assert.Empty(report.Devices);
            Assert.Contains("HDC", report.Diagnostic);
        }
    }
}
