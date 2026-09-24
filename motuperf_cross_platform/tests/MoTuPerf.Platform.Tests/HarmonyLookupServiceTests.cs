using System.Collections.Generic;
using System.Linq;
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
        public void ParsesBundleManagerJsonAndKeyValueVariantsWithoutFixedPackageNames()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.native\",\"modulePackage\":\"com.example.native\"}\n"
                + "bundle_name: com.example.compat\n"
                + "package name: com.example.legacy\n");

            Assert.Equal(new[] { "com.example.native", "com.example.compat", "com.example.legacy" }, apps.Select(app => app.BundleId));
            Assert.Equal("MainAbility", HarmonyLookupService.ParseMainAbility("mainElementName = MainAbility"));
            Assert.Equal("MainAbility", HarmonyLookupService.ParseMainAbility("entryAbilityName: 'MainAbility'"));
        }

        [Fact]
        public void ParsesCompatibilityPackagesAndMergesRunningApps()
        {
            Assert.Equal(new[] { "com.example.compat", "com.example.other" }, HarmonyLookupService.ParseAndroidPackages(
                "package:/data/app/com.example.compat/base.apk=com.example.compat\n"
                + "package:com.example.other uid:10234\nnot-a-package\n"));

            var apps = new List<AppInfo>();
            var processes = HarmonyLookupService.ParseProcesses(
                "PID NAME ARGS\n"
                + "401 com.example.native com.example.native\n"
                + "402 com.example.compat:render com.example.compat:render\n", "device");
            HarmonyLookupService.MergeProcessApps(apps, processes);

            Assert.Equal(new[] { "com.example.compat", "com.example.native" }, apps.Select(app => app.BundleId).OrderBy(value => value));
        }

        [Fact]
        public void FindsEntryAbilityAndModuleAcrossBundleManagerShapes()
        {
            string output = "prefix\n{\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"entryAbilityName\":\"EntryAbility\"}]}\n";
            Assert.Equal("EntryAbility", HarmonyLookupService.ParseMainAbility(output));
            Assert.Equal("entry", HarmonyLookupService.ParseMainModule(output));
            Assert.Equal("MainAbility", HarmonyLookupService.ParseMainAbility("abilityName: MainAbility"));
        }

        [Fact]
        public void EnumeratesEveryRealLaunchEntryWithoutGuessingNames()
        {
            string output = "{\"hapModuleInfos\":["
                + "{\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"},"
                + "{\"moduleName\":\"feature\",\"mainElementName\":\"FeatureAbility\"}]}";

            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "EntryAbility");
            Assert.Contains(entries, entry => entry.Module == "feature" && entry.Ability == "FeatureAbility");
        }

        [Fact]
        public void MergesProcessViewsByPidAndKeepsTheMostCompleteIdentity()
        {
            var first = HarmonyLookupService.ParseProcesses(
                "PID ARGS\n601 /system/bin/appspawn --bundle-name com.example.game\n", "device");
            var second = HarmonyLookupService.ParseProcesses(
                "UID PID PPID CMD\nu0 601 1 com.example.game:render\n", "device");

            var merged = HarmonyLookupService.MergeProcesses(first.Concat(second));

            Assert.Single(merged);
            Assert.Equal(601, merged[0].Pid);
            Assert.Equal("com.example.game", merged[0].BundleId);
        }

        [Fact]
        public void ParsesAdditionalBundleIdentityKeys()
        {
            var apps = HarmonyLookupService.ParseApps(
                "bundle id: com.example.one\n"
                + "applicationId=com.example.two\n"
                + "appIdentifier: com.example.three\n");

            Assert.Equal(new[] { "com.example.one", "com.example.two", "com.example.three" }, apps.Select(app => app.BundleId));
        }

        [Fact]
        public void ParsesHeaderlessAndCaseVariantHarmonyProcessOutput()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "uid pid ppid name\n"
                + "u0 501 1 com.example.native\n"
                + "u0 502 1 /system/bin/com.example.compat:worker --flag\n", "device");

            Assert.Equal(2, rows.Count);
            Assert.Equal("com.example.native", rows.Single(row => row.Pid == 501).BundleId);
            Assert.Equal("com.example.compat", rows.Single(row => row.Pid == 502).BundleId);
        }

        [Fact]
        public void FindsBundleInProcessCommandArguments()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "PID ARGS\n"
                + "601 /system/bin/appspawn --bundle-name com.example.launcher --user 0\n", "device");

            Assert.Single(rows);
            Assert.Equal("com.example.launcher", rows[0].BundleId);
            Assert.Equal("com.example.launcher", rows[0].Name);
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
