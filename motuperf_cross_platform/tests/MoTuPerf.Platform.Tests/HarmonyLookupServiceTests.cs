using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyLookupServiceTests
    {
        private static string Format(string json, bool indented)
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement,
                new JsonSerializerOptions { WriteIndented = indented });
        }

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
        public void TreatsAuthorizedHdcStatesAsConnectedDevices()
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(0,
                "AUTHORIZED1 USB Authorized localhost hdc\n"
                + "AUTHORIZED2 USB Authenticated localhost hdc\n", ""));

            Assert.Equal(new[] { "AUTHORIZED1", "AUTHORIZED2" }, report.Devices.Select(device => device.Udid));
            Assert.DoesNotContain("不可采集", report.Diagnostic);
        }

        [Fact]
        public void KeepsValidTargetsWhenHdcReturnsAValidPayloadWithNonZeroExitCode()
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(
                1,
                "PARTIAL-1 USB Connected warning\n",
                "warning: target list was partially refreshed"));

            var device = Assert.Single(report.Devices);
            Assert.Equal("PARTIAL-1", device.Udid);
        }

        [Fact]
        public void IgnoresHdcCommandDiagnosticsInsteadOfCreatingFakeTargets()
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(
                1,
                "PARTIAL-1 USB Connected warning\n",
                "hdc: command not found\nerror: unable to enumerate targets"));

            var device = Assert.Single(report.Devices);
            Assert.Equal("PARTIAL-1", device.Udid);
            Assert.DoesNotContain(report.Devices, candidate => candidate.Udid == "hdc:");
        }

        [Fact]
        public void IgnoresLocalHdcUartControlPortWhenNoHarmonyDeviceIsConnected()
        {
            var report = HarmonyLookupService.ParseDiscovery(new ProcessResult(
                0,
                "COM1\tUART\tReady\tunknown...\thdc\n",
                ""));

            Assert.Empty(report.Devices);
            Assert.Contains("未发现鸿蒙设备", report.Diagnostic);
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
        public void ParsesSingleSegmentHarmonyBundleOnlyFromApplicationMetadata()
        {
            var apps = HarmonyLookupService.ParseApps(
                "bundleName: foundation\n"
                + "versionName: 1.0\n"
                + "{\"bundleName\":\"system_service\",\"versionCode\":2}\n");

            Assert.Contains(apps, app => app.BundleId == "foundation" && app.Version == "1.0");
            Assert.Contains(apps, app => app.BundleId == "system_service" && app.Version == "2");
            Assert.DoesNotContain(apps, app => app.BundleId == "EntryAbility");
            Assert.Empty(HarmonyLookupService.ParseApps("{\"name\":\"foundation\"}"));

            var indentedApps = HarmonyLookupService.ParseApps(
                "bundleInfos:\n"
                + "  - name: system_service\n"
                + "    versionName: 3.0\n");
            var indented = Assert.Single(indentedApps);
            Assert.Equal("system_service", indented.BundleId);
            Assert.Equal("3.0", indented.Version);
        }

        [Fact]
        public void ExplicitTextAndKeyedProcessFieldsAcceptSingleSegmentHarmonyBundles()
        {
            var apps = HarmonyLookupService.ParseApps(
                "bundleName: x\n"
                + "versionName: 1.0\n");

            var app = Assert.Single(apps);
            Assert.Equal("x", app.BundleId);
            Assert.Equal("1.0", app.Version);

            var processes = HarmonyLookupService.ParseProcesses(
                "PID=42 BUNDLE_NAME=x COMM=launcher\n", "HARMONY-1");

            var process = Assert.Single(processes);
            Assert.Equal("x", process.BundleId);
            Assert.Equal("launcher", process.Name);
        }

        [Fact]
        public void ParsesSingleSegmentHarmonyBundlesFromJsonInventoryValues()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleInfos\":[\"launcher\",\"systemui\",\"com.example.game\","
                + "{\"launcher\":{\"versionName\":\"1.0\"}}]}\n"
                + "{\"metadata\":{\"label\":\"systemui\",\"versionName\":\"9.9\"}}\n"
                + "{\"bundleInfos\":[{\"systemui\":{\"versionName\":\"2.0\"}}]}\n");

            Assert.Equal(
                new[] { "launcher", "systemui", "com.example.game" },
                apps.Select(app => app.BundleId));
            Assert.Equal("1.0", Assert.Single(apps, app => app.BundleId == "launcher").Version);
            Assert.DoesNotContain(apps, app => app.BundleId == "metadata");
            Assert.Equal("2.0", Assert.Single(apps, app => app.BundleId == "systemui").Version);
        }

        [Fact]
        public void KeepsRealAbilityEntriesWithTheApplicationRecord()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.native\",\"userId\":0,"
                + "\"hapModuleInfos\":[{\"moduleName\":\"entry\","
                + "\"mainElementName\":\"EntryAbility\"}]}\n");

            var app = Assert.Single(apps);
            var entry = Assert.Single(app.HarmonyLaunchEntries);
            Assert.Equal("entry", entry.Module);
            Assert.Equal("EntryAbility", entry.Ability);
            Assert.Equal(new[] { 0 }, app.HarmonyUserIds);
        }

        [Fact]
        public void NestedApplicationInfoKeepsSiblingModulesAndAbilityEntries()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleInfo\":{\"applicationInfo\":{"
                + "\"bundleName\":\"com.example.nested\",\"userId\":100,"
                + "\"label\":\"Nested App\",\"versionName\":\"1.2.3\"},"
                + "\"hapModuleInfos\":["
                + "{\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"},"
                + "{\"moduleName\":\"feature\",\"abilityInfos\":[{\"abilityName\":\"FeatureAbility\"}],"
                + "\"serviceAbilityInfos\":[{\"abilityName\":\"SyncService\"}]}"
                + "]}}\n");

            var app = Assert.Single(apps);
            Assert.Equal("Nested App", app.Name);
            Assert.Equal("1.2.3", app.Version);
            Assert.Equal(new[] { 100 }, app.HarmonyUserIds);
            Assert.Equal(new[] { "entry/EntryAbility", "feature/FeatureAbility", "feature/SyncService" },
                app.HarmonyLaunchEntries
                    .Select(entry => entry.Module + "/" + entry.Ability)
                    .OrderBy(value => value));
            Assert.True(app.HasLaunchEntry);
            Assert.Contains(app.HarmonyLaunchEntries, entry => entry.Ability == "SyncService" && !entry.IsUiEntry);
        }

        [Fact]
        public void NestedBundleRecordsKeepSameBundleUsersAndEntriesSeparate()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleInfo\":{\"applicationInfo\":{\"bundleName\":\"com.example.multi\",\"userId\":0},"
                + "\"moduleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"OwnerAbility\"}]}}\n"
                + "{\"bundleInfo\":{\"applicationInfo\":{\"bundleName\":\"com.example.multi\",\"userId\":100},"
                + "\"moduleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"WorkAbility\"}],"
                + "\"extensionAbilityInfos\":[{\"extensionAbilityName\":\"SyncService\"}]}}\n");

            var app = Assert.Single(apps);
            Assert.Equal(new[] { 0, 100 }, app.HarmonyUserIds);
            Assert.Equal(new[] { "OwnerAbility", "SyncService", "WorkAbility" },
                app.HarmonyLaunchEntries.Select(entry => entry.Ability).OrderBy(value => value));

            var instances = HarmonyLookupService.ExpandHarmonyUserInstances(apps);
            var owner = Assert.Single(instances, item => item.HarmonyUserId == 0);
            var work = Assert.Single(instances, item => item.HarmonyUserId == 100);
            Assert.Contains(owner.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
            Assert.DoesNotContain(owner.HarmonyLaunchEntries, entry => entry.Ability == "WorkAbility");
            Assert.Contains(work.HarmonyLaunchEntries, entry => entry.Ability == "WorkAbility");
            Assert.Contains(work.HarmonyLaunchEntries, entry => entry.Ability == "SyncService");
            Assert.DoesNotContain(work.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
        }

        [Fact]
        public void IndentedNestedApplicationInfoKeepsBundleAndModuleContext()
        {
            var apps = HarmonyLookupService.ParseApps(
                "bundleInfo:\n"
                + "  applicationInfo:\n"
                + "    bundleName: com.example.indented\n"
                + "    userId: 100\n"
                + "    appName: Indented App\n"
                + "  moduleInfos:\n"
                + "    - moduleName: entry\n"
                + "      abilityInfos:\n"
                + "        - abilityName: EntryAbility\n");

            var app = Assert.Single(apps);
            Assert.Equal("com.example.indented", app.BundleId);
            Assert.Equal(new[] { 100 }, app.HarmonyUserIds);
            var entry = Assert.Single(app.HarmonyLaunchEntries);
            Assert.Equal("entry", entry.Module);
            Assert.Equal("EntryAbility", entry.Ability);
        }

        [Fact]
        public async Task LaunchReusesAggregateInventoryAbilityWhenPerBundleDumpIsUnavailable()
        {
            var app = Assert.Single(HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.native\",\"userId\":0,\"hapModuleInfos\":[{"
                + "\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"}]}"));
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "aa start -u 0 -b com.example.native -m entry -a EntryAbility")
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", app, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(new[] { "aa start -u 0 -b com.example.native -m entry -a EntryAbility" }, commands);
        }

        [Fact]
        public async Task LaunchesTheMatchingAppFromACombinedIndentedInventory()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "pm list users")
                    return Task.FromResult(new ProcessResult(0, "UserInfo{0:Owner:13} running\n", ""));
                if (key == "bm dump -a")
                    return Task.FromResult(new ProcessResult(0,
                        "bundleName: com.example.first\n"
                        + "userId: 0\n"
                        + "module name: entry\n"
                        + "ability name: FirstAbility\n"
                        + "bundleName: com.example.second\n"
                        + "userId: 0\n"
                        + "module name: feature\n"
                        + "ability name: SecondAbility\n", ""));
                if (key.StartsWith("bm dump -a ", StringComparison.Ordinal)
                    || key.StartsWith("bm dump -n ", StringComparison.Ordinal)
                    || key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal)
                    || key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
                if (key.StartsWith("sh -c ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key == "aa start -u 0 -b com.example.second -m feature -a SecondAbility")
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<AppInfo> apps = await service.ListAppsAsync("HARMONY-1", CancellationToken.None);
            AppInfo selected = Assert.Single(apps, app => app.BundleId == "com.example.second");

            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", selected, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("aa start -u 0 -b com.example.second -m feature -a SecondAbility", commands);
            Assert.DoesNotContain("aa start -u 0 -b com.example.first -m entry -a FirstAbility", commands);
        }

        [Fact]
        public void ParsesCompatibilityPackagesAndMergesRunningApps()
        {
            Assert.Equal(new[] { "com.example.compat", "com.example.system", "com.example.other" }, HarmonyLookupService.ParseAndroidPackages(
                "package:/data/app/com.example.compat/base.apk=com.example.compat\n"
                + "package:/system/app/com.example.system/base.apk\n"
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
        public async Task MergesEveryHarmonyInventorySourceIntoSelectableTargets()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users" || key == "cmd user list")
                    return Task.FromResult(new ProcessResult(0,
                        "UserInfo{0:Owner:13} running\nUserInfo{100:Work:13} running\n", ""));
                if (key == "bm dump -a")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.native\",\"userId\":0,"
                        + "\"versionName\":\"1.0\",\"hapModuleInfos\":[{\"moduleName\":\"entry\","
                        + "\"mainElementName\":\"EntryAbility\"}]}\n", ""));
                if (key.StartsWith("bm dump -a ", StringComparison.Ordinal))
                {
                    if (key.Contains(" 100", StringComparison.Ordinal))
                        return Task.FromResult(new ProcessResult(0,
                            "{\"bundleName\":\"com.example.stopped\",\"userId\":100,\"versionName\":\"2.0\"}\n", ""));
                    return Task.FromResult(new ProcessResult(0, "", ""));
                }
                if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "package:/system/app/com.example.system/base.apk\npackage:com.example.compat\n", ""));
                if (key == "ps -A -o UID,PID,PPID,ARGS")
                    return Task.FromResult(new ProcessResult(0,
                        "UID PID PPID ARGS\n"
                        + "u0 501 1 com.example.native\n"
                        + "u100_a1 502 1 com.example.compat:render\n"
                        + "u100_a2 503 1 foundation\n", ""));
                if (key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "PID ARGS\n501 com.example.native\n502 com.example.compat:render\n503 foundation\n", ""));
                if (key.StartsWith("sh -c ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<AppInfo> apps = await service.ListAppsAsync("HARMONY-ALL", CancellationToken.None);

            Assert.Contains(apps, app => app.BundleId == "com.example.native" && app.HarmonyUserId == 0);
            Assert.Contains(apps, app => app.BundleId == "com.example.system");
            Assert.Contains(apps, app => app.BundleId == "com.example.compat" && app.HarmonyUserId == 100);
            Assert.Contains(apps, app => app.BundleId == "com.example.stopped" && app.HarmonyUserId == 100 && !app.IsRunning);
            Assert.Contains(apps, app => string.IsNullOrWhiteSpace(app.BundleId) && app.ProcessPid == 503 && app.IsProcessOnly);
            Assert.Equal("有启动入口", apps.Single(app => app.BundleId == "com.example.native").LaunchAvailability);
            Assert.Equal("可尝试启动", apps.Single(app => app.BundleId == "com.example.stopped").LaunchAvailability);
            Assert.Equal("仅运行中可采集", apps.Single(app => string.IsNullOrWhiteSpace(app.BundleId) && app.ProcessPid == 503).LaunchAvailability);
        }

        [Fact]
        public void ParsesBareCompatibilityPackagesAndDiagnosticsFromEitherStream()
        {
            Assert.Equal(new[] { "com.example.bare", "com.example.uid" }, HarmonyLookupService.ParseAndroidPackages(
                "com.example.bare\ncom.example.uid uid:10234\npermission denied\n"));
            Assert.Equal(new[] { "com.example.stderr" }, HarmonyLookupService.ParseAndroidPackages(
                "warning: fallback\ncom.example.stderr\n"));
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
        public void EnumeratesAbilityInfoNamesForModulesWithoutMainElementName()
        {
            string output = "{\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"abilityInfos\":["
                + "{\"name\":\"MainAbility\",\"exported\":true},"
                + "{\"name\":\"SettingsAbility\",\"exported\":false}]}]}";

            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "MainAbility");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "SettingsAbility");
        }

        [Fact]
        public void ClassNameUsesTheOwningAbilityCollectionToDetermineUiStatus()
        {
            string output = "{\"bundleName\":\"com.example.classname\",\"hapModuleInfos\":[{"
                + "\"moduleName\":\"entry\",\"abilityInfos\":[{\"className\":\"MainAbility\"}],"
                + "\"serviceAbilityInfos\":[{\"className\":\"SyncService\"}]}]}";

            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);
            var ui = Assert.Single(entries, entry => entry.Ability == "MainAbility");
            var service = Assert.Single(entries, entry => entry.Ability == "SyncService");
            Assert.True(ui.IsUiEntry);
            Assert.False(service.IsUiEntry);

            AppInfo app = Assert.Single(HarmonyLookupService.ParseApps(output));
            Assert.True(app.HasLaunchEntry);
            Assert.False(app.IsProcessOnly);
        }

        [Fact]
        public void DoesNotTreatBundleAndModuleMetadataAsLaunchEntries()
        {
            string output = "{\"bundleName\":\"com.example.app\",\"hapModuleInfos\":[{"
                + "\"moduleName\":\"entry\",\"abilityInfos\":[{"
                + "\"bundleName\":\"com.example.app\",\"moduleName\":\"entry\","
                + "\"name\":\"MainAbility\"}]}]}";

            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            var entry = Assert.Single(entries);
            Assert.Equal("entry", entry.Module);
            Assert.Equal("MainAbility", entry.Ability);
        }

        [Fact]
        public void EnumeratesExtensionAbilityEntriesFromJsonAndIndentedBundleOutput()
        {
            string json = "{\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"extensionAbilityInfos\":["
                + "{\"name\":\"SyncService\"},{\"extensionAbilityName\":\"FormService\"}]}]}";
            var jsonEntries = HarmonyLookupService.ParseLaunchEntryPoints(json);
            Assert.Contains(jsonEntries, entry => entry.Module == "entry" && entry.Ability == "SyncService");
            Assert.Contains(jsonEntries, entry => entry.Module == "entry" && entry.Ability == "FormService");

            string text = "moduleName: service\n"
                + "extensionAbilityInfos:\n"
                + "  - extensionAbilityName: DataShareService\n";
            var textEntries = HarmonyLookupService.ParseLaunchEntryPoints(text);
            Assert.Contains(textEntries, entry => entry.Module == "service" && entry.Ability == "DataShareService");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RealNestedExtensionInfosKeepModuleAndNonUiOwnership(bool indented)
        {
            // This is the shape returned by the connected ALN-AL80 for
            // com.huawei.hmos.aidataservice: ExtensionAbilityInfo records are
            // nested below each hapModuleInfo, and appId/appIdentifier are
            // adjacent identity fields rather than the selected Bundle.
            string json = "{\"appId\":\"com.huawei.hmos.aidataservice_signature\","
                + "\"appIdentifier\":\"5765880207853016403\","
                + "\"bundleName\":\"com.huawei.hmos.aidataservice\",\"userId\":100,"
                + "\"hapModuleInfos\":["
                + "{\"moduleName\":\"dataaiprocess\",\"extensionInfos\":["
                + "{\"bundleName\":\"com.huawei.hmos.aidataservice\","
                + "\"moduleName\":\"dataaiprocess\",\"extensionTypeName\":\"service\","
                + "\"name\":\"AiProcessServiceAbility\",\"type\":3}]},"
                + "{\"moduleName\":\"entry\",\"extensionInfos\":["
                + "{\"moduleName\":\"entry\",\"name\":\"AiDataServiceAbility\","
                + "\"extensionTypeName\":\"service\",\"type\":3}]}]}";

            List<HarmonyLaunchEntryPoint> entries = HarmonyLookupService.ParseLaunchEntryPoints(
                Format(json, indented), "com.huawei.hmos.aidataservice", 100);

            Assert.Equal(new[] { "dataaiprocess/AiProcessServiceAbility", "entry/AiDataServiceAbility" },
                entries.Select(entry => entry.Module + "/" + entry.Ability).OrderBy(value => value));
            Assert.All(entries, entry => Assert.False(entry.IsUiEntry));

            AppInfo app = Assert.Single(HarmonyLookupService.ParseApps(Format(json, indented)));
            Assert.False(app.HasLaunchEntry);
            Assert.True(app.HasNonUiLaunchEntry);
            Assert.Equal(2, app.HarmonyLaunchEntries.Count);
        }

        [Fact]
        public void EnumeratesVendorSpecificExtensionAbilityCollectionsWithoutPromotingThemToBundles()
        {
            string json = "{\"name\":\"com.example.vendor.extensions\",\"hapModuleInfos\":["
                + "{\"moduleName\":\"entry\","
                + "\"inputMethodExtensionAbilityInfos\":[{\"inputMethodExtensionAbilityName\":\"KeyboardService\"}],"
                + "\"accessibilityExtensionAbilityList\":[{\"name\":\"AssistService\"}],"
                + "\"shareExtensionAbilities\":[\"ShareService\"],"
                + "\"fileShareExtensionAbilityInfoList\":[{\"className\":\"FileShareService\"}],"
                + "\"workSchedulerExtensionAbilityInfos\":[{\"workSchedulerExtensionAbilityName\":\"WorkerService\"}]}]}";

            List<AppInfo> apps = HarmonyLookupService.ParseApps(json);
            List<HarmonyLaunchEntryPoint> entries = HarmonyLookupService.ParseLaunchEntryPoints(json);

            Assert.Single(apps);
            Assert.Equal("com.example.vendor.extensions", apps[0].BundleId);
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "KeyboardService");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "AssistService");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "ShareService");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "FileShareService");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "WorkerService");

            List<HarmonyLaunchEntryPoint> textEntries = HarmonyLookupService.ParseLaunchEntryPoints(
                "bundleName: com.example.vendor.text\n"
                + "moduleName: entry\n"
                + "inputMethodExtensionAbilityInfos:\n"
                + "  - inputMethodExtensionAbilityName: KeyboardService\n"
                + "accessibilityExtensionAbilityList:\n"
                + "  - name: AssistService\n");
            Assert.Contains(textEntries, entry => entry.Module == "entry" && entry.Ability == "KeyboardService");
            Assert.Contains(textEntries, entry => entry.Module == "entry" && entry.Ability == "AssistService");
        }

        [Fact]
        public void ParsesAndroidCompatibilityLauncherComponent()
        {
            Assert.Equal("com.example.compat/com.example.compat.MainActivity",
                HarmonyLookupService.ParseAndroidLaunchComponent("com.example.compat", "priority=0\ncom.example.compat/.MainActivity\n"));
            Assert.Equal("", HarmonyLookupService.ParseAndroidLaunchComponent("com.example.compat", "No activity found"));
        }

        [Fact]
        public void BuildsCompleteCompatibilityPackageInventoryForAllUsers()
        {
            var commands = HarmonyLookupService.BuildPackageInventoryCommands(new[] { -1, 100, 0, 100 });

            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "-f" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "-f", "--user", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "-f", "--user", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "--user", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "--user", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "-f" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "-f", "--user", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "-f", "--user", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "--user", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "--user", "100" }));
            Assert.DoesNotContain(commands, command => command.Contains("-u", StringComparer.Ordinal));
            Assert.DoesNotContain(commands, command => command.Contains("-U", StringComparer.Ordinal));
            Assert.DoesNotContain(commands, command => command.Contains("--user-id", StringComparer.Ordinal));
            Assert.DoesNotContain(commands, command => command.Contains("-1", StringComparer.Ordinal));
            Assert.Equal(12, commands.Count);
        }

        [Fact]
        public void OnlyExplicitPackageUserSelectorEstablishesCompatibilityProfile()
        {
            Assert.Equal(100, HarmonyLookupService.CommandUserId(
                new[] { "pm", "list", "packages", "--user", "100" }));
            Assert.Equal(-1, HarmonyLookupService.CommandUserId(
                new[] { "pm", "list", "packages", "-u", "100" }));
            Assert.Equal(-1, HarmonyLookupService.CommandUserId(
                new[] { "pm", "list", "packages", "-U", "100" }));
            Assert.Equal(-1, HarmonyLookupService.CommandUserId(
                new[] { "cmd", "package", "list", "packages", "--user-id", "100" }));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task CompatibilityInventoryKeepsRealProfilesWithoutShortFlagAttribution(
            bool unifiedInventory, bool scopedPackagesDenied)
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                string key = string.Join(" ", command);
                if (key == "pm list users")
                    return Task.FromResult(new ProcessResult(0,
                        "UserInfo{0:Owner:13}\nUserInfo{100:Work:13}\n", ""));
                if (key == "bm dump -a")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.native\",\"userId\":100}", ""));
                if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal))
                {
                    // Android short flags treat a trailing number as a name
                    // filter; they do not limit the result to that profile.
                    if (command.Contains("-u") || command.Contains("-U"))
                        return Task.FromResult(new ProcessResult(0,
                            "package:com.example.100.retired uid:10123\n", ""));
                    if (command.Contains("--user-id"))
                        return Task.FromResult(new ProcessResult(1, "", "Unknown option: --user-id"));
                    int userIndex = Array.IndexOf(command, "--user");
                    if (userIndex < 0)
                        return Task.FromResult(new ProcessResult(0, "package:com.example.global\n", ""));
                    if (scopedPackagesDenied)
                        return Task.FromResult(new ProcessResult(1, "", "permission denied"));
                    bool owner = command[userIndex + 1] == "0";
                    return Task.FromResult(new ProcessResult(0,
                        "package:com.example.shared\npackage:com.example."
                        + (owner ? "owner" : "work") + "\n", ""));
                }
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "PID ARGS\n503 foundation\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "unavailable"));
            });

            HarmonyTargetInventory inventory = unifiedInventory
                ? await service.ListTargetsAsync("TEST-HDC", CancellationToken.None) : null;
            List<AppInfo> apps = inventory?.Apps
                ?? await service.ListAppsAsync("TEST-HDC", CancellationToken.None);
            Assert.DoesNotContain(apps, app => app.BundleId == "com.example.100.retired");
            Assert.Equal(100, Assert.Single(apps, app => app.BundleId == "com.example.native").HarmonyUserId);
            Assert.Contains(apps, app => app.BundleId == "" && app.ProcessPid == 503 && app.IsProcessOnly);
            if (unifiedInventory)
                Assert.Equal("foundation", Assert.Single(inventory.Processes).Name);
            AppInfo unscoped = Assert.Single(apps, app => app.BundleId == "com.example.global");
            Assert.Equal(-1, unscoped.HarmonyUserId);
            Assert.False(unscoped.CanAttemptLaunch);
            if (scopedPackagesDenied)
            {
                Assert.DoesNotContain(apps, app => app.BundleId == "com.example.shared");
            }
            else
            {
                Assert.Equal(new[] { 0, 100 }, apps.Where(app => app.BundleId == "com.example.shared")
                    .Select(app => app.HarmonyUserId).OrderBy(user => user).ToArray());
                Assert.Equal(0, Assert.Single(apps, app => app.BundleId == "com.example.owner").HarmonyUserId);
                Assert.Equal(100, Assert.Single(apps, app => app.BundleId == "com.example.work").HarmonyUserId);
            }
            int beforeLaunch = commands.Count;
            Assert.NotEqual(0, (await service.LaunchAppAsync("TEST-HDC", unscoped, CancellationToken.None)).ExitCode);
            Assert.Equal(beforeLaunch, commands.Count);
        }

        [Fact]
        public void SplitsLargeProcessStartQueriesIntoBoundedHdcCommands()
        {
            var commands = HarmonyLookupService.BuildProcessStatCommands(Enumerable.Range(1, 129));

            Assert.Equal(3, commands.Count);
            Assert.All(commands, command => Assert.Equal("sh", command[0]));
            string text = string.Join("\n", commands.Select(command => command[2]));
            Assert.Contains("for p in 1 2 3", text);
            Assert.Contains("64; do cat /proc/$p/stat", text);
            Assert.Contains("for p in 65 66 67", text);
            Assert.Contains("for p in 129", text);
            Assert.Contains("cat /proc/$p/stat", text);
            Assert.DoesNotContain(" 0 ", text);
        }

        [Fact]
        public void BuildsScopedBundleManagerInventoryForShortAndLongUserOptions()
        {
            var commands = HarmonyLookupService.BuildBundleManagerInventoryCommands(new[] { 100, -1, 0, 100 });

            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "-u", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "-u", "100" }));
            Assert.DoesNotContain(commands, command => command.Contains("--user") || command.Contains("-U"));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "--user-id", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "--user-id", "100" }));
            Assert.Equal(5, commands.Count);
        }

        [Fact]
        public void BuildsUserInventoryFallbackForHarmonyCommandVariants()
        {
            var commands = HarmonyLookupService.BuildUserInventoryCommands();

            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "users" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "user", "list" }));
        }

        [Fact]
        public void ParsesHarmonyUserInfoIdAndUserIdVariants()
        {
            var ids = HarmonyLookupService.ParseUserIds(
                "UserInfo{id=100,name=Work} running\n"
                + "UserInfo{10:Guest:13} stopped\n"
                + "userId=12\n"
                + "user id: 14\n");

            Assert.Equal(new[] { 10, 12, 14, 100 }, ids);
        }

        [Fact]
        public void ReadsHarmonyUserIdFromShortAndLongCommandOptions()
        {
            Assert.Equal(100, HarmonyLookupService.CommandUserId(new[] { "bm", "dump", "-a", "-u", "100" }));
            Assert.Equal(101, HarmonyLookupService.CommandUserId(new[] { "aa", "dump", "-r", "--userId", "101" }));
            Assert.Equal(102, HarmonyLookupService.CommandUserId(new[] { "aa", "start", "-u", "102", "-b", "com.example.app" }));
            Assert.Equal(103, HarmonyLookupService.CommandUserId(new[] { "bm", "dump", "-a", "--user-id", "103" }));
        }

        [Fact]
        public void ParsesKeyValueBundleProcessRows()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "pid=701,bundleName=com.example.native,uid=u100_a1\n"
                + "PID BUNDLE_NAME USER\n"
                + "702 com.example.compat u0_a2\n", "device");

            Assert.Equal(new[] { 701, 702 }, rows.Select(row => row.Pid).OrderBy(pid => pid));
            Assert.Equal("com.example.native", rows.Single(row => row.Pid == 701).BundleId);
            Assert.Equal(100, rows.Single(row => row.Pid == 701).HarmonyUserId);
            Assert.Equal("com.example.compat", rows.Single(row => row.Pid == 702).BundleId);
        }

        [Fact]
        public void KeepsKeyValueBundleOnlyRowsWithoutInventingAProcessName()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "pid=701,bundleName=com.example.game,uid=u100_a1\n", "device");

            var process = Assert.Single(rows);
            Assert.Empty(process.Name);
            Assert.Equal("com.example.game", process.BundleId);
            Assert.Equal("com.example.game", process.OwnerBundleId);
            Assert.Equal("com.example.game", process.OwnerName);
            Assert.Equal("process-inventory", process.OwnershipSource);
            Assert.True(process.OwnershipVerified);
            Assert.Equal(100, process.HarmonyUserId);
        }

        [Fact]
        public void KeepsBundleOnlyTableRowsSeparateFromTheProcessNameColumn()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "PID BUNDLE_NAME USER\n"
                + "702 com.example.compat u0_a2\n", "device");

            var process = Assert.Single(rows);
            Assert.Empty(process.Name);
            Assert.Equal("com.example.compat", process.BundleId);
            Assert.Equal(0, process.HarmonyUserId);
            Assert.True(process.OwnershipVerified);
        }

        [Fact]
        public void KeepsRealProcessNameWhenBundleColumnIsAlsoPresent()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "PID NAME BUNDLE_NAME USER\n"
                + "703 com.example.game:worker com.example.game u100_a1\n", "device");

            var process = Assert.Single(rows);
            Assert.Equal("com.example.game:worker", process.Name);
            Assert.Equal("com.example.game", process.BundleId);
            Assert.Equal(100, process.HarmonyUserId);
        }

        [Fact]
        public void KeepsSingleSegmentBundleOnlyWhenProcessOutputLabelsTheBundleField()
        {
            var explicitRows = HarmonyLookupService.ParseProcesses(
                "PID BUNDLE_NAME USER\n"
                + "701 foundation u0_a1\n", "device");
            var ordinaryRows = HarmonyLookupService.ParseProcesses(
                "PID NAME USER\n"
                + "702 foundation u0_a1\n", "device");

            Assert.Equal("foundation", Assert.Single(explicitRows).BundleId);
            Assert.Empty(Assert.Single(ordinaryRows).BundleId);
        }

        [Fact]
        public void NormalizesHarmonyProcessIdentityBeforePidValidation()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "PID ARGS\n"
                + "701 /system/bin/servicemanager\n"
                + "702 /system/bin/com.example.compat:worker --render\n"
                + "703 [kworker/0:1]\n", "device");

            Assert.Equal("servicemanager", rows.Single(row => row.Pid == 701).Name);
            Assert.Equal("com.example.compat:worker", rows.Single(row => row.Pid == 702).Name);
            Assert.Equal("com.example.compat", rows.Single(row => row.Pid == 702).BundleId);
            Assert.Equal("kworker/0:1", rows.Single(row => row.Pid == 703).Name);
        }

        [Fact]
        public void ParsesEntryModuleAndSingularAbilityInfoAliases()
        {
            string output = "{\"bundleName\":\"com.example.alias\",\"entryModule\":\"entry\",\"abilityInfo\":{\"abilityName\":\"EntryAbility\"}}";
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "EntryAbility");
            Assert.Equal("entry", HarmonyLookupService.ParseMainModule("entryModule: entry"));
        }

        [Fact]
        public void ParsesStringAndObjectKeyAbilityCollections()
        {
            string output = "{\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"abilityInfos\":[\"StringAbility\",{\"MapAbility\":{\"exported\":true}}]}]}";
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "StringAbility");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "MapAbility");
        }

        [Fact]
        public void ParsesVendorAbilityCollectionListAliasesWithoutPromotingAbilityNames()
        {
            string json = "{\"bundleName\":\"com.example.aliases\",\"hapModuleInfos\":["
                + "{\"moduleName\":\"entry\",\"abilityInfoList\":[{\"name\":\"EntryAbility\"}],"
                + "\"serviceExtensionAbilityInfoList\":[{\"serviceExtensionAbilityName\":\"SyncService\"}],"
                + "\"serviceExtensionAbilityInfo\":[{\"serviceExtensionAbilityName\":\"SyncServiceInfo\"}],"
                + "\"serviceExtensionAbilityList\":[{\"serviceExtensionAbilityName\":\"SyncServiceList\"}],"
                + "\"formExtensionAbilityInfo\":[{\"formExtensionAbilityName\":\"FormInfo\"}],"
                + "\"formExtensionAbilityInfos\":[{\"formExtensionAbilityName\":\"FormInfos\"}],"
                + "\"formExtensionAbilityList\":[{\"formExtensionAbilityName\":\"FormList\"}],"
                + "\"dataShareExtensionAbilityInfo\":[{\"dataShareExtensionAbilityName\":\"ShareInfo\"}],"
                + "\"dataShareExtensionAbilityInfos\":[{\"dataShareExtensionAbilityName\":\"ShareInfos\"}],"
                + "\"dataShareExtensionAbilityInfoList\":[{\"dataShareExtensionAbilityName\":\"ShareService\"}],"
                + "\"dataShareExtensionAbilityList\":[{\"dataShareExtensionAbilityName\":\"ShareList\"}]}]}";
            List<AppInfo> apps = HarmonyLookupService.ParseApps(json);
            List<HarmonyLaunchEntryPoint> entries = HarmonyLookupService.ParseLaunchEntryPoints(json);

            AppInfo app = Assert.Single(apps);
            Assert.Equal("com.example.aliases", app.BundleId);
            Assert.DoesNotContain(apps, candidate => candidate.BundleId == "com.example.aliases.EntryAbility");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "EntryAbility");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "SyncService");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "SyncServiceInfo");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "SyncServiceList");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "FormInfo");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "FormInfos");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "FormList");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "ShareInfo");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "ShareInfos");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "ShareService");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "ShareList");

            List<HarmonyLaunchEntryPoint> textEntries = HarmonyLookupService.ParseLaunchEntryPoints(
                "bundleName: com.example.aliases\n"
                + "moduleName: entry\n"
                + "abilityInfoList:\n"
                + "  - name: EntryAbility\n"
                + "serviceExtensionAbilityInfoList:\n"
                + "  - serviceExtensionAbilityName: SyncService\n"
                + "dataShareExtensionAbilityInfoList:\n"
                + "  - dataShareExtensionAbilityName: ShareService\n");
            Assert.Contains(textEntries, entry => entry.Module == "entry" && entry.Ability == "EntryAbility");
            Assert.Contains(textEntries, entry => entry.Module == "entry" && entry.Ability == "SyncService");
            Assert.Contains(textEntries, entry => entry.Module == "entry" && entry.Ability == "ShareService");
        }

        [Fact]
        public void ParsesNonExtensionServiceFormDataShareAndWorkerAbilityAliases()
        {
            string json = "{\"bundleName\":\"com.example.nonextension\",\"hapModuleInfos\":["
                + "{\"moduleName\":\"entry\","
                + "\"serviceAbilityInfos\":[{\"className\":\"SyncService\"}],"
                + "\"formAbilityInfoList\":[{\"className\":\"HomeForm\"}],"
                + "\"dataShareAbilityInfos\":[{\"className\":\"ShareService\"}],"
                + "\"workSchedulerAbilities\":[{\"className\":\"WorkerService\"}]}]}";

            List<HarmonyLaunchEntryPoint> jsonEntries = HarmonyLookupService.ParseLaunchEntryPoints(json);

            Assert.Contains(jsonEntries, entry => entry.Module == "entry" && entry.Ability == "SyncService");
            Assert.Contains(jsonEntries, entry => entry.Module == "entry" && entry.Ability == "HomeForm");
            Assert.Contains(jsonEntries, entry => entry.Module == "entry" && entry.Ability == "ShareService");
            Assert.Contains(jsonEntries, entry => entry.Module == "entry" && entry.Ability == "WorkerService");

            List<HarmonyLaunchEntryPoint> textEntries = HarmonyLookupService.ParseLaunchEntryPoints(
                "bundleName: com.example.nonextension\n"
                + "moduleName: entry\n"
                + "serviceAbilityInfos:\n"
                + "  - className: SyncService\n"
                + "formAbilityInfoList:\n"
                + "  - className: HomeForm\n"
                + "dataShareAbilityInfos:\n"
                + "  - className: ShareService\n");

            Assert.Contains(textEntries, entry => entry.Module == "entry" && entry.Ability == "SyncService");
            Assert.Contains(textEntries, entry => entry.Module == "entry" && entry.Ability == "HomeForm");
            Assert.Contains(textEntries, entry => entry.Module == "entry" && entry.Ability == "ShareService");
        }

        [Fact]
        public void KeepsServiceOnlyBundlesAsRealLaunchCandidates()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.service\",\"userId\":0,\"hapModuleInfos\":[{"
                + "\"moduleName\":\"entry\",\"serviceAbilityInfos\":[{\"className\":\"SyncService\"}],"
                + "\"dataShareAbilityInfos\":[{\"className\":\"ShareService\"}]}]}\n");

            AppInfo app = Assert.Single(apps);
            Assert.True(app.IsProcessOnly);
            Assert.False(app.HasLaunchEntry);
            Assert.True(app.HasNonUiLaunchEntry);
            Assert.True(app.CanAttemptLaunch);
            Assert.Equal("可尝试启动", app.LaunchAvailability);
            Assert.Contains(app.HarmonyLaunchEntries, entry => !entry.IsUiEntry && entry.Ability == "SyncService");
            Assert.Contains(app.HarmonyLaunchEntries, entry => !entry.IsUiEntry && entry.Ability == "ShareService");
        }

        [Theory]
        [InlineData("SyncService")]
        [InlineData("HomeForm")]
        [InlineData("ShareService")]
        [InlineData("ShareExtension")]
        public async Task LaunchesKnownNonUiEntryWhenDeviceAcceptsTheRealAbility(string ability)
        {
            List<string> commands = new List<string>();
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? new string[0]);
                commands.Add(key);
                if (key == "aa start -u 0 -b com.example.service -m entry -a " + ability)
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", new AppInfo
            {
                BundleId = "com.example.service",
                Platform = "harmony",
                HarmonyUserId = 0,
                IsProcessOnly = true,
                    HasNonUiLaunchEntry = true,
                    HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                    {
                        new HarmonyLaunchEntryInfo { Module = "entry", Ability = ability, IsUiEntry = false }
                    }
                }, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("aa start -u 0 -b com.example.service -m entry -a " + ability, commands);
            Assert.DoesNotContain(commands, command => command.Contains("am start", StringComparison.Ordinal));
        }

        [Fact]
        public async Task KeepsNonUiLaunchFailureWhenEveryRealEntryIsRejected()
        {
            List<string> commands = new List<string>();
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? new string[0]);
                commands.Add(key);
                if (key.StartsWith("aa start ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "permission denied for real ability"));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", new AppInfo
            {
                BundleId = "com.example.service",
                Platform = "harmony",
                HarmonyUserId = 0,
                IsProcessOnly = true,
                HasNonUiLaunchEntry = true,
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo { Module = "entry", Ability = "SyncService", IsUiEntry = false }
                }
            }, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("permission denied for real ability", result.Stderr);
            Assert.DoesNotContain(commands, command => command == "aa start -u 0 -b com.example.service");
            Assert.DoesNotContain(commands, command => command.StartsWith("am start ", StringComparison.Ordinal));
            Assert.DoesNotContain(commands, command => command.StartsWith("monkey ", StringComparison.Ordinal));
        }

        [Fact]
        public async Task TriesNonUiEntryFromBundleDetailAfterTheDeviceRejectsUiEntry()
        {
            List<string> commands = new List<string>();
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? new string[0]);
                commands.Add(key);
                if (key == "bm dump -n com.example.mixed -u 0")
                    return Task.FromResult(new ProcessResult(0,
                        "bundleName: com.example.mixed\n"
                        + "moduleName: entry\n"
                        + "abilityInfos:\n"
                        + "  - name: EntryAbility\n"
                        + "serviceAbilityInfos:\n"
                        + "  - className: SyncService\n", ""));
                if (key.StartsWith("bm dump -n com.example.mixed ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unknown option"));
                if (key == "bm dump -n com.example.mixed")
                    return Task.FromResult(new ProcessResult(1, "", "unscoped lookup must not be used here"));
                if (key == "aa start -u 0 -b com.example.mixed -m entry -a EntryAbility")
                    return Task.FromResult(new ProcessResult(1, "", "ui entry rejected"));
                if (key == "aa start -u 0 -b com.example.mixed -m entry -a SyncService")
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", "com.example.mixed", new[] { 0 }, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("aa start -u 0 -b com.example.mixed -m entry -a SyncService", commands);
        }

        [Fact]
        public void KeepsUiLaunchableWhenTheBundleAlsoHasNonUiEntries()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.mixed\",\"userId\":0,\"hapModuleInfos\":[{"
                + "\"moduleName\":\"entry\",\"abilityInfos\":[{\"name\":\"EntryAbility\"}],"
                + "\"serviceAbilityInfos\":[{\"className\":\"SyncService\"}]}]}\n");

            AppInfo app = Assert.Single(apps);
            Assert.True(app.HasLaunchEntry);
            Assert.False(app.HasNonUiLaunchEntry);
            Assert.True(app.CanAttemptLaunch);
            Assert.Equal("有启动入口", app.LaunchAvailability);
        }

        [Fact]
        public void KeepsApplicationIdentityWhenNonExtensionAbilityCollectionsContainClassNames()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.identity\",\"userId\":100,"
                + "\"hapModuleInfos\":[{\"moduleName\":\"entry\","
                + "\"serviceAbilityInfos\":[{\"className\":\"SyncService\"}],"
                + "\"formAbilityInfoList\":[{\"className\":\"HomeForm\"}],"
                + "\"dataShareAbilityInfos\":[{\"className\":\"ShareService\"}]}]}\n");

            AppInfo app = Assert.Single(apps);
            Assert.Equal("com.example.identity", app.BundleId);
            Assert.Equal(100, app.HarmonyUserId);
            Assert.Equal(new[] { 100 }, app.HarmonyUserIds);
            Assert.Contains(app.HarmonyLaunchEntries, entry => entry.Module == "entry" && entry.Ability == "SyncService");
            Assert.Contains(app.HarmonyLaunchEntries, entry => entry.Module == "entry" && entry.Ability == "HomeForm");
            Assert.Contains(app.HarmonyLaunchEntries, entry => entry.Module == "entry" && entry.Ability == "ShareService");
            Assert.DoesNotContain(apps, candidate => candidate.BundleId == "SyncService");
            Assert.DoesNotContain(apps, candidate => candidate.BundleId == "HomeForm");
            Assert.DoesNotContain(apps, candidate => candidate.BundleId == "ShareService");
        }

        [Fact]
        public void ParsesIndentedBundleManagerAbilityInfoOutput()
        {
            string output = "bundleName: com.example.text\n"
                + "hapModuleInfos:\n"
                + "  - moduleName: entry\n"
                + "    abilityInfos:\n"
                + "      - name: EntryAbility\n"
                + "        exported: true\n"
                + "      - abilityName: SettingsAbility\n";

            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "EntryAbility");
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "SettingsAbility");
        }

        [Fact]
        public void BindsIndentedLaunchEntriesToEachApplicationInACombinedInventory()
        {
            string output = "bundleName: com.example.first\n"
                + "versionName: 1.0\n"
                + "hapModuleInfos:\n"
                + "  - moduleName: entry\n"
                + "    abilityInfos:\n"
                + "      - name: FirstAbility\n"
                + "bundleName: com.example.second\n"
                + "versionName: 2.0\n"
                + "hapModuleInfos:\n"
                + "  - moduleName: feature\n"
                + "    extensionAbilityInfos:\n"
                + "      - extensionAbilityName: SecondService\n";

            var apps = HarmonyLookupService.ParseApps(output);

            var first = Assert.Single(apps, app => app.BundleId == "com.example.first");
            var second = Assert.Single(apps, app => app.BundleId == "com.example.second");
            Assert.Contains(first.HarmonyLaunchEntries, entry => entry.Module == "entry" && entry.Ability == "FirstAbility");
            Assert.Contains(second.HarmonyLaunchEntries, entry => entry.Module == "feature" && entry.Ability == "SecondService");
            Assert.Equal("1.0", first.Version);
            Assert.Equal("2.0", second.Version);
        }

        [Fact]
        public void BindsFlatAbilityAndModuleFieldsToEachApplication()
        {
            string output = "bundle name: com.example.first\n"
                + "module name: entry\n"
                + "ability name: FirstAbility\n"
                + "bundle name: com.example.second\n"
                + "module name: feature\n"
                + "ability name: SecondAbility\n";

            var apps = HarmonyLookupService.ParseApps(output);

            var first = Assert.Single(apps, app => app.BundleId == "com.example.first");
            var second = Assert.Single(apps, app => app.BundleId == "com.example.second");
            Assert.Contains(first.HarmonyLaunchEntries, entry => entry.Module == "entry" && entry.Ability == "FirstAbility");
            Assert.Contains(second.HarmonyLaunchEntries, entry => entry.Module == "feature" && entry.Ability == "SecondAbility");
        }

        [Fact]
        public void TextInventoryKeepsUsersWhenUserFieldPrecedesEachSameBundleRecord()
        {
            string output = "userId: 100\n"
                + "bundleName: com.example.shared\n"
                + "hapModuleInfos:\n"
                + "  - moduleName: entry\n"
                + "    abilityInfos:\n"
                + "      - name: OwnerAbility\n"
                + "userId: 101\n"
                + "bundleName: com.example.shared\n"
                + "hapModuleInfos:\n"
                + "  - moduleName: work\n"
                + "    abilityInfos:\n"
                + "      - name: WorkAbility\n"
                + "    extensionAbilityInfos:\n"
                + "      - extensionAbilityName: WorkService\n";

            var rows = HarmonyLookupService.ExpandHarmonyUserInstances(
                HarmonyLookupService.ParseApps(output));

            var owner = Assert.Single(rows, app => app.HarmonyUserId == 100);
            var work = Assert.Single(rows, app => app.HarmonyUserId == 101);
            Assert.Contains(owner.HarmonyLaunchEntries,
                entry => entry.Module == "entry" && entry.Ability == "OwnerAbility");
            Assert.DoesNotContain(owner.HarmonyLaunchEntries, entry => entry.Ability == "WorkAbility");
            Assert.Contains(work.HarmonyLaunchEntries,
                entry => entry.Module == "work" && entry.Ability == "WorkAbility");
            Assert.Contains(work.HarmonyLaunchEntries,
                entry => entry.Module == "work" && entry.Ability == "WorkService" && !entry.IsUiEntry);
            Assert.DoesNotContain(work.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
        }

        [Fact]
        public void TextInventoryMapsHarmonyUidProfileTokensBeforeBundleRecords()
        {
            var apps = HarmonyLookupService.ParseApps(
                "uid: u100_a123\n"
                + "bundleName: com.example.profiled\n"
                + "uid: 20010123\n"
                + "bundleName: com.example.nativeuid\n"
                + "userId: 101\n"
                + "bundleName: com.example.user\n");

            Assert.Contains(apps, app => app.BundleId == "com.example.profiled" && app.HarmonyUserIds.Contains(100));
            Assert.Contains(apps, app => app.BundleId == "com.example.nativeuid" && app.HarmonyUserIds.Contains(100));
            Assert.Contains(apps, app => app.BundleId == "com.example.user" && app.HarmonyUserIds.Contains(101));
        }

        [Fact]
        public void DoesNotPairFlatAbilityFieldsWithAnEarlierModuleWhenAliasesRepeat()
        {
            string output = "moduleName: entry\n"
                + "entryModuleName: entry\n"
                + "abilityName: EntryAbility\n"
                + "moduleName: feature\n"
                + "abilityName: FeatureAbility\n";

            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.Module == "entry" && entry.Ability == "EntryAbility");
            Assert.Contains(entries, entry => entry.Module == "feature" && entry.Ability == "FeatureAbility");
            Assert.DoesNotContain(entries, entry => entry.Module == "entry" && entry.Ability == "FeatureAbility");
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
            Assert.Equal("com.example.game:render", merged[0].Name);
            Assert.False(merged[0].OwnershipAmbiguous);
        }

        [Fact]
        public void MultipleConcreteProcessViewsRemainAmbiguousEvenWhenBundleMatches()
        {
            var merged = HarmonyLookupService.MergeProcesses(new[]
            {
                new ProcessInfo { Pid = 602, Name = "appspawn", BundleId = "com.example.game", Platform = "harmony" },
                new ProcessInfo { Pid = 602, Name = "com.example.game:worker", BundleId = "com.example.game", Platform = "harmony" },
                new ProcessInfo { Pid = 602, Name = "com.example.game:renderer", BundleId = "com.example.game", Platform = "harmony" }
            });

            ProcessInfo process = Assert.Single(merged);
            Assert.Equal("appspawn", process.Name);
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.OwnershipVerified);
            Assert.Empty(process.BundleId);
            Assert.False(process.Recommended);
        }

        [Fact]
        public void ExistingProcessAmbiguityCannotBeClearedByLaterAbilityEvidence()
        {
            var process = new ProcessInfo
            {
                Pid = 603,
                Name = "appspawn",
                Platform = "harmony",
                BundleId = "com.example.game",
                OwnershipAmbiguous = true
            };

            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, new[]
            {
                new HarmonyProcessBinding
                {
                    Pid = 603,
                    ProcessName = "com.example.game:renderer",
                    BundleId = "com.example.game",
                    HarmonyUserId = 0,
                    Foreground = true
                }
            });

            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.OwnershipVerified);
            Assert.Empty(process.BundleId);
            Assert.False(process.Recommended);
        }

        [Fact]
        public void ParsesAdditionalBundleIdentityKeys()
        {
            var apps = HarmonyLookupService.ParseApps(
                "bundle id: com.example.one\n"
                + "applicationId=com.example.two\n"
                + "appIdentifier: com.example.three\n"
                + "bundle: com.example.four\n"
                + "package: com.example.five\n"
                + "appId=com.example.six\n");

            Assert.Equal(new[] { "com.example.one", "com.example.two", "com.example.three", "com.example.four", "com.example.five", "com.example.six" }, apps.Select(app => app.BundleId));
        }

        [Fact]
        public void ParsesJsonPackageAndApplicationIdAliases()
        {
            var apps = HarmonyLookupService.ParseApps(
                "[{\"packageId\":\"com.example.package\"},{\"package\":\"com.example.package2\"},"
                + "{\"appId\":\"com.example.app\"},{\"app_id\":\"com.example.app2\"}]\n");

            Assert.Equal(new[] { "com.example.package", "com.example.package2", "com.example.app", "com.example.app2" },
                apps.Select(app => app.BundleId));
        }

        [Fact]
        public void ParsesAllHarmonyUserIdsAndKeepsOwnerUser()
        {
            var ids = HarmonyLookupService.ParseUserIds(
                "Users:\n"
                + "  UserInfo{0:Owner:13} running\n"
                + "  UserInfo{100:Work:10} running\n"
                + "  user id=999\n"
                + "  user 1000\n");

            Assert.Equal(new[] { 0, 100, 999, 1000 }, ids);
        }

        [Fact]
        public void ParsesMultipleJsonValuesFromMixedBundleManagerOutput()
        {
            var apps = HarmonyLookupService.ParseApps(
                "[Info] bundle dump follows\n"
                + "{\"bundleInfos\":[{\"bundleName\":\"com.example.first\",\"applicationInfo\":{\"label\":\"First\"}}]}\n"
                + "{\"bundleName\":\"com.example.second\",\"versionName\":\"2.0\"}\n");

            Assert.Equal(2, apps.Count);
            Assert.Equal("First", apps.Single(app => app.BundleId == "com.example.first").Name);
            Assert.Equal("2.0", apps.Single(app => app.BundleId == "com.example.second").Version);
        }

        [Fact]
        public void ParsesStructuredApplicationNamesVersionsAndArrayOutput()
        {
            var apps = HarmonyLookupService.ParseApps(
                "prefix\n[{\"bundleName\":\"com.example.native\",\"appName\":\"Native App\",\"versionName\":\"3.2.1\"},"
                + "{\"applicationInfo\":{\"bundleId\":\"com.example.system\",\"label\":\"System App\",\"versionCode\":12}},"
                + "{\"bundleName\":\"com.example.nested\",\"applicationInfo\":{\"appName\":\"Nested App\",\"versionName\":\"9.1\"}}]");

            Assert.Equal(3, apps.Count);
            Assert.Equal("Native App", apps.Single(app => app.BundleId == "com.example.native").Name);
            Assert.Equal("3.2.1", apps.Single(app => app.BundleId == "com.example.native").Version);
            Assert.Equal("System App", apps.Single(app => app.BundleId == "com.example.system").Name);
            Assert.Equal("12", apps.Single(app => app.BundleId == "com.example.system").Version);
            Assert.Equal("Nested App", apps.Single(app => app.BundleId == "com.example.nested").Name);
            Assert.Equal("9.1", apps.Single(app => app.BundleId == "com.example.nested").Version);
        }

        [Fact]
        public void ParsesBundleRecordsWithOnlyNameAndLabelAndNestedApplicationArrays()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleInfo\":{\"name\":\"com.example.labelonly\",\"label\":\"Label Only\"}}\n"
                + "{\"bundleName\":\"com.example.arrayinfo\",\"applicationInfo\":[{"
                + "\"label\":\"Array App\",\"versionName\":\"4.2\"}]}\n");

            AppInfo labelOnly = Assert.Single(apps, app => app.BundleId == "com.example.labelonly");
            Assert.Equal("Label Only", labelOnly.Name);
            AppInfo arrayInfo = Assert.Single(apps, app => app.BundleId == "com.example.arrayinfo");
            Assert.Equal("Array App", arrayInfo.Name);
            Assert.Equal("4.2", arrayInfo.Version);
        }

        [Fact]
        public void DoesNotUseNestedAbilityNameAsApplicationDisplayName()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.abilityowner\",\"applicationInfo\":{"
                + "\"abilityInfos\":[{\"name\":\"com.example.abilityowner.EntryAbility\"}]}}\n");

            AppInfo app = Assert.Single(apps);
            Assert.Equal("com.example.abilityowner", app.Name);
        }

        [Fact]
        public void TreatsEmbeddedNumericUidAsItsHarmonyProfile()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.work\",\"uid\":210123}\n"
                + "bundleName: com.example.text uid: 410234\n");

            Assert.Contains(apps, app => app.BundleId == "com.example.work" && app.HarmonyUserIds.Contains(1));
            Assert.Contains(apps, app => app.BundleId == "com.example.text" && app.HarmonyUserIds.Contains(2));
        }

        [Fact]
        public void ParsesOfficialHarmonyBundleNameRecordsFromJsonAndIndentedText()
        {
            var jsonApps = HarmonyLookupService.ParseApps(
                "{\"bundleInfos\":[{\"name\":\"com.example.official\",\"versionCode\":1000000,\"versionName\":\"1.0\","
                + "\"hapModuleInfos\":[{\"moduleName\":\"entry\"}]}]}\n");
            Assert.Contains(jsonApps, app => app.BundleId == "com.example.official" && app.Version == "1.0");

            var textApps = HarmonyLookupService.ParseApps(
                "bundleInfos:\n"
                + "  - name: com.example.text\n"
                + "    versionName: 2.0\n"
                + "    hapModuleInfos:\n"
                + "      - moduleName: entry\n"
                + "        abilityInfos:\n"
                + "          - name: com.example.EntryAbility\n");
            Assert.Contains(textApps, app => app.BundleId == "com.example.text" && app.Version == "2.0");
            Assert.DoesNotContain(textApps, app => app.BundleId == "com.example.EntryAbility");
        }

        [Fact]
        public void ParsesPackageStringArraysObjectKeysAndLaunchCapability()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"com.example.keyed\":{\"version_code\":7}}\n"
                + "[\"com.example.string.one\",\"com.example.string.two\"]\n"
                + "{\"bundle_name\":\"com.example.native\",\"userId\":0,\"name\":\"Native\",\"abilityInfos\":[{\"name\":\"EntryAbility\"}]}\n");

            Assert.Equal(new[] { "com.example.keyed", "com.example.string.one", "com.example.string.two", "com.example.native" },
                apps.Select(app => app.BundleId));
            Assert.Equal("7", apps.Single(app => app.BundleId == "com.example.keyed").Version);
            Assert.Equal("Native", apps.Single(app => app.BundleId == "com.example.native").Name);
            Assert.True(apps.Single(app => app.BundleId == "com.example.native").HasLaunchEntry);
            Assert.Equal("有启动入口", apps.Single(app => app.BundleId == "com.example.native").LaunchAvailability);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DoesNotCreateApplicationsFromDottedMetadata(bool indented)
        {
            string output = System.Text.Json.JsonSerializer.Serialize(new
            {
                bundleInfos = new[]
                {
                    new
                    {
                        bundleName = "com.example.real",
                        label = "Studio.Game",
                        vendor = "com.example.vendor",
                        versionName = "Release.Beta",
                        permissions = new[] { "ohos.permission.CAMERA" },
                        defPermissions = new[] { new { name = "ohos.permission.CUSTOM", label = "Camera access" } },
                        metadata = new { channel = "com.example.channel", name = "com.example.meta", label = "Metadata" },
                        abilityInfos = new[] { new { name = "com.example.real.EntryAbility" } }
                    }
                }
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = indented });

            AppInfo app = Assert.Single(HarmonyLookupService.ParseApps(output));

            Assert.Equal("com.example.real", app.BundleId);
            Assert.Equal("Studio.Game", app.Name);
            Assert.Equal("Release.Beta", app.Version);
            Assert.Contains(app.HarmonyLaunchEntries, entry => entry.Ability == "com.example.real.EntryAbility");
        }

        [Fact]
        public void KeepsRealInventoryShapesWhileIgnoringMetadataArrays()
        {
            string output = "[\"com.example.root\"]\n"
                + "{\"installedBundles\":[\"com.example.installed\"],"
                + "\"permissions\":[\"ohos.permission.LOCATION\"],"
                + "\"wrapper\":{\"bundleInfos\":[{\"bundleName\":\"com.example.nested\"}]}}\n"
                + "{\"com.example.keyed\":{\"versionName\":\"Preview.Stable\"}}\n"
                + "bundleName: com.example.text appName: Text App\n";

            Assert.Equal(new[] { "com.example.root", "com.example.installed", "com.example.nested",
                "com.example.keyed", "com.example.text" },
                HarmonyLookupService.ParseApps(output).Select(app => app.BundleId));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MetadataDoesNotBecomeLaunchTargetsInUnifiedInventory(bool indented)
        {
            string output = System.Text.Json.JsonSerializer.Serialize(new
            {
                bundleName = "com.example.game",
                userId = 100,
                label = "Studio.Game",
                reqPermissions = new[] { "ohos.permission.CAMERA" },
                hapModuleInfos = new[] { new { moduleName = "entry", mainElementName = "EntryAbility" } }
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = indented });
            List<string> starts = new List<string>();
            Task<ProcessResult> Execute(string serial, string[] command, int timeoutMs, CancellationToken token)
            {
                string key = string.Join(" ", command);
                if (key == "bm dump -a") return Task.FromResult(new ProcessResult(0, output, ""));
                if (key.StartsWith("aa start ", StringComparison.Ordinal))
                {
                    starts.Add(key);
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                }
                return Task.FromResult(new ProcessResult(1, "", "not available"));
            }
            var service = new HarmonyLookupService(Execute);

            HarmonyTargetInventory inventory = await service.ListTargetsAsync("device", CancellationToken.None);
            AppInfo app = Assert.Single(inventory.Apps);
            Assert.Equal("com.example.game", app.BundleId);
            Assert.Equal(100, app.HarmonyUserId);
            ProcessResult launch = await service.LaunchAppAsync("device", app, CancellationToken.None);

            Assert.Equal(0, launch.ExitCode);
            Assert.Equal(new[] { "aa start -u 100 -b com.example.game -m entry -a EntryAbility" }, starts);
        }

        [Fact]
        public void DoesNotPromoteDottedAbilityStringsToApplications()
        {
            var jsonApps = HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.owner\",\"hapModuleInfos\":[{\"moduleName\":\"entry\","
                + "\"abilityInfos\":[\"com.example.owner.EntryAbility\",{\"name\":\"com.example.owner.OtherAbility\"}]}]}\n");
            Assert.Single(jsonApps);
            Assert.Equal("com.example.owner", jsonApps[0].BundleId);

            var textApps = HarmonyLookupService.ParseApps(
                "bundleInfos:\n"
                + "  - name: com.example.text.owner\n"
                + "    hapModuleInfos:\n"
                + "      - moduleName: entry\n"
                + "        abilityInfos:\n"
                + "          com.example.text.owner.EntryAbility\n"
                + "          com.example.text.owner.OtherAbility\n");
            Assert.Single(textApps);
            Assert.Equal("com.example.text.owner", textApps[0].BundleId);
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
        public void SupportsVendorProcessNameColumnsWhenArgsIsUnavailable()
        {
            IReadOnlyList<string[]> commands = HarmonyLookupService.BuildProcessInventoryCommands();

            Assert.Equal(28, commands.Count);
            Assert.Contains(commands, command => string.Join(" ", command) == "ps -A -o PID,NAME");
            Assert.Contains(commands, command => string.Join(" ", command) == "ps -A -o UID,PID,PPID,NAME");
            Assert.Contains(commands, command => string.Join(" ", command) == "ps -A -o USER,PID,PPID,COMM");
            Assert.Contains(commands, command => string.Join(" ", command) == "ps -A -o PID,COMM");
            Assert.Contains(commands, command => string.Join(" ", command) == "ps -A -o UID,PID,PPID,COMMAND");
            Assert.Contains(commands, command => string.Join(" ", command) == "ps -A -o USER,PID,PPID,COMMAND");
            Assert.Contains(commands, command => string.Join(" ", command) == "ps -A -o PID,CMDLINE");
            Assert.Contains(commands, command => string.Join(" ", command) == "ps -A -o PID,USER,PPID,CMDLINE");

            List<ProcessInfo> rows = HarmonyLookupService.ParseProcesses(
                "USER PID PPID NAME\n"
                + "u100_a1 801 1 com.example.native\n"
                + "u0_a2 802 1 com.example.native:worker\n",
                "harmony");

            Assert.Equal(new[] { "com.example.native", "com.example.native:worker" },
                rows.OrderBy(row => row.Pid).Select(row => row.Name));
            Assert.Equal(new[] { 100, 0 }, rows.OrderBy(row => row.Pid).Select(row => row.HarmonyUserId));
        }

        [Fact]
        public void ProcInventoryFallbackUsesReadOnlyPidUidAndCommandEvidence()
        {
            string command = Assert.Single(HarmonyLookupService.BuildProcProcessInventoryCommands())[2];
            Assert.Contains("__MOTUPERF_PROC_LIST__", command);
            Assert.Contains("/proc/[0-9]*", command);
            Assert.DoesNotContain("kill", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("start", command, StringComparison.OrdinalIgnoreCase);

            List<ProcessInfo> rows = HarmonyLookupService.ParseProcesses(
                "__MOTUPERF_PROC_LIST__\n"
                + "PID=901 UID=20000123 CMDLINE=/system/bin/com.example.game --render\n"
                + "PID=902 UID=0 COMM=foundation\n", "harmony",
                inferBundleFromProcessName: false);

            Assert.Equal(new[] { 901, 902 }, rows.Select(row => row.Pid).OrderBy(pid => pid));
            Assert.Equal("com.example.game", rows.Single(row => row.Pid == 901).Name);
            Assert.Empty(rows.Single(row => row.Pid == 901).BundleId);
            Assert.Equal(100, rows.Single(row => row.Pid == 901).HarmonyUserId);
            Assert.Equal("foundation", rows.Single(row => row.Pid == 902).Name);
            Assert.Empty(rows.Single(row => row.Pid == 902).BundleId);
            Assert.True(rows.Single(row => row.Pid == 902).HarmonyNameIsComm);
            Assert.Equal(-1, rows.Single(row => row.Pid == 902).HarmonyUserId);
        }

        [Fact]
        public async Task ProcInventoryFallbackRecoversWhenEveryPsViewIsUnavailable()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "ps: unsupported"));
                if (command.Length >= 3 && command[0] == "sh" && command[2].Contains("__MOTUPERF_PROC_LIST__", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "__MOTUPERF_PROC_LIST__\nPID=901 UID=20000123 CMDLINE=/system/bin/com.example.game\n", ""));
                if (command.Length >= 3 && command[0] == "sh")
                {
                    string stat = "901 (com.example.game) "
                        + string.Join(" ", Enumerable.Repeat("0", 19))
                        + " 12345\n";
                    return Task.FromResult(new ProcessResult(0, stat, ""));
                }
                if (key.StartsWith("aa", StringComparison.Ordinal)) return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<ProcessInfo> processes = await service.ListProcessesAsync("HARMONY-1", CancellationToken.None);

            ProcessInfo process = Assert.Single(processes);
            Assert.Equal(901, process.Pid);
            Assert.Equal("com.example.game", process.Name);
            Assert.Empty(process.BundleId);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Equal(12345, process.HarmonyStartTimeTicks);
        }

        [Fact]
        public async Task ProcInventoryFallbackRejectsUnmarkedCommandOutput()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "ps: unsupported"));
                if (command.Length >= 3 && command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0,
                        "PID=999 UID=20000123 CMDLINE=/system/bin/phantom\n", ""));
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);

            await Assert.ThrowsAsync<IOException>(
                () => service.ListProcessesAsync("HARMONY-1", CancellationToken.None));
        }

        [Fact]
        public void MapsNumericUidWhenVendorUsesUserColumn()
        {
            List<ProcessInfo> rows = HarmonyLookupService.ParseProcesses(
                "USER PID PPID NAME\n"
                + "210001 901 1 foundation\n"
                + "410001 902 1 com.example.work\n"
                + "100 903 1 com.example.owner\n",
                "harmony");

            Assert.Equal(1, rows.Single(row => row.Pid == 901).HarmonyUserId);
            Assert.Equal(2, rows.Single(row => row.Pid == 902).HarmonyUserId);
            Assert.Equal(-1, rows.Single(row => row.Pid == 903).HarmonyUserId);
        }

        [Fact]
        public void ParsesHeaderlessPidFirstUserAndPlainProcessName()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "801 u100_a1 1 worker_service --mode=render\n"
                + "u200_a2 802 1 plain_service\n", "device");

            Assert.Equal(2, rows.Count);
            Assert.Equal("worker_service", rows.Single(row => row.Pid == 801).Name);
            Assert.Equal(100, rows.Single(row => row.Pid == 801).HarmonyUserId);
            Assert.Equal("plain_service", rows.Single(row => row.Pid == 802).Name);
            Assert.Equal(200, rows.Single(row => row.Pid == 802).HarmonyUserId);
        }

        [Fact]
        public void ParsesKeyValueProcessOutputAndMarksProcessOnlyApps()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "pid=601,name=com.example.running:worker\n"
                + "pid: 602, cmd: /system/bin/other_service --render\n", "device");

            Assert.Equal(new[] { 601, 602 }, rows.Select(row => row.Pid).OrderBy(pid => pid));
            Assert.Equal("com.example.running", rows.Single(row => row.Pid == 601).BundleId);
            var apps = new List<AppInfo>();
            HarmonyLookupService.MergeProcessApps(apps, rows);
            AppInfo bundledProcess = Assert.Single(apps, app => app.BundleId == "com.example.running");
            Assert.True(bundledProcess.IsProcessOnly);
            Assert.False(bundledProcess.CanAttemptLaunch);
            Assert.Equal("仅运行中可采集", bundledProcess.LaunchAvailability);
            AppInfo unbundledProcess = Assert.Single(apps, app => string.IsNullOrWhiteSpace(app.BundleId));
            Assert.True(unbundledProcess.IsProcessOnly);
            Assert.False(unbundledProcess.CanAttemptLaunch);
            Assert.Equal("仅运行中可采集", unbundledProcess.LaunchAvailability);
        }

        [Fact]
        public async Task UnknownUserProcessOnlyTargetCannotIssueAnUnscopedLaunch()
        {
            List<string> commands = new List<string>();
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                commands.Add(string.Join(" ", command ?? Array.Empty<string>()));
                return Task.FromResult(new ProcessResult(0, "unexpected launch", ""));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", new AppInfo
            {
                BundleId = "com.example.running",
                Platform = "harmony",
                IsProcessOnly = true,
                HarmonyUserId = -1
            }, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("只能选择运行中的真实进程", result.Stderr);
            Assert.Empty(commands);
        }

        [Fact]
        public async Task UnknownUserBundleDoesNotInventUserZeroOrIssueAnUnscopedLaunch()
        {
            List<string> commands = new List<string>();
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "bm dump -a")
                    return Task.FromResult(new ProcessResult(0,
                        "bundleName: com.example.unknown\n"
                        + "module name: entry\n"
                        + "ability name: EntryAbility\n", ""));
                throw new IOException("user inventory unavailable");
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            HarmonyTargetInventory inventory = await service.ListTargetsAsync("hdc-1", CancellationToken.None);
            AppInfo app = Assert.Single(inventory.Apps, candidate => candidate.BundleId == "com.example.unknown");

            Assert.Equal(-1, app.HarmonyUserId);
            Assert.Empty(app.HarmonyUserIds);
            Assert.False(app.CanAttemptLaunch);
            Assert.Equal("用户范围未知", app.LaunchAvailability);
            Assert.Contains("不会默认使用 user 0", inventory.UserInventoryError);

            ProcessResult launch = await service.LaunchAppAsync("hdc-1", app, CancellationToken.None);
            Assert.NotEqual(0, launch.ExitCode);
            Assert.Contains("不会执行无范围启动", launch.Stderr);
            Assert.DoesNotContain(commands, command => command.StartsWith("aa start", StringComparison.Ordinal));
        }

        [Fact]
        public void DuplicateKnownUserEvidenceDoesNotBecomeAnAmbiguousScope()
        {
            var app = new AppInfo
            {
                BundleId = "com.example.running",
                Platform = "harmony",
                IsProcessOnly = true,
                HarmonyUserId = -1,
                HarmonyUserIds = new List<int> { 100, 100 }
            };

            Assert.True(app.CanAttemptLaunch);
            Assert.Equal("可尝试启动", app.LaunchAvailability);
        }

        [Fact]
        public void KeepsUnbundledHarmonyServicesAsPidOnlyProcessApps()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "701 1 0 00:00:01 foundation\n"
                + "702 1 0 appspawn\n", "device");

            Assert.Equal(new[] { "appspawn", "foundation" }, rows.Select(row => row.Name).OrderBy(name => name));
            Assert.All(rows, row => Assert.Empty(row.BundleId));

            var apps = new List<AppInfo>();
            HarmonyLookupService.MergeProcessApps(apps, rows);

            Assert.Equal(new[] { 701, 702 }, apps.Select(app => app.ProcessPid).OrderBy(pid => pid));
            Assert.All(apps, app => Assert.Empty(app.BundleId));
            Assert.All(apps, app => Assert.Equal("仅运行中可采集", app.LaunchAvailability));
            Assert.Equal("PID 701", apps.Single(app => app.ProcessPid == 701).TargetIdentifier);
        }

        [Fact]
        public void KeepsHarmonyUserProfileOnProcessesAndProcessOnlyApps()
        {
            Assert.Equal(100, HarmonyLookupService.ParseHarmonyUserId("u100_a123"));
            Assert.Equal(12, HarmonyLookupService.ParseHarmonyUserId("uid=12"));
            var processes = HarmonyLookupService.ParseProcesses(
                "UID PID PPID CMD\n"
                + "u100_a1 701 1 com.example.work\n", "device");

            Assert.Single(processes);
            Assert.Equal(100, processes[0].HarmonyUserId);
            var apps = new List<AppInfo>();
            HarmonyLookupService.MergeProcessApps(apps, processes);
            Assert.Equal(new[] { 100 }, apps[0].HarmonyUserIds);
        }

        [Fact]
        public void BindsSameBundleProcessesToTheMatchingHarmonyUserRows()
        {
            var apps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.shared",
                    Name = "Shared App",
                    Platform = "harmony",
                    HarmonyUserIds = new List<int> { 0, 100 }
                }
            };
            var processes = HarmonyLookupService.ParseProcesses(
                "UID PID PPID CMD\n"
                + "u0 701 1 com.example.shared\n"
                + "u100_a123 702 1 com.example.shared:worker\n", "device");

            HarmonyLookupService.MergeProcessApps(apps, processes);

            Assert.Equal(new[] { 0, 100 }, apps.Select(app => app.HarmonyUserId).OrderBy(id => id));
            Assert.All(apps, app => Assert.True(app.IsRunning));
            Assert.All(apps, app => Assert.False(app.IsProcessOnly));
        }

        [Fact]
        public void ParsesNumericHarmonyUidAndKeepsUserFromTheRicherPsView()
        {
            Assert.Equal(1, HarmonyLookupService.ParseProcesses(
                "UID PID PPID C STIME TTY TIME CMD\n210123 701 1 0 10:00 ? 00:00:01 com.example.game\n",
                "device").Single().HarmonyUserId);

            var withoutUser = HarmonyLookupService.ParseProcesses(
                "PID ARGS\n701 com.example.game\n", "device");
            var withUser = HarmonyLookupService.ParseProcesses(
                "UID PID PPID C STIME TTY TIME CMD\n" +
                "u100_a123 701 1 0 10:00 ? 00:00:01 com.example.game\n", "device");

            var merged = HarmonyLookupService.MergeProcesses(withoutUser.Concat(withUser));

            Assert.Single(merged);
            Assert.Equal(100, merged[0].HarmonyUserId);
        }

        [Fact]
        public void ParsesUidUserAndPidFirstPsColumnVariants()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "UID PID PPID ARGS\n"
                + "u100_a1 801 1 com.example.uid\n"
                + "USER PID PPID ARGS\n"
                + "u200_a2 802 1 com.example.user\n"
                + "PID UID PPID ARGS\n"
                + "803 610123 1 com.example.uidfirst\n"
                + "PID USER PPID ARGS\n"
                + "804 u400_a4 1 com.example.userfirst\n", "device");

            Assert.Equal(4, rows.Count);
            Assert.Equal(100, rows.Single(row => row.Pid == 801).HarmonyUserId);
            Assert.Equal(200, rows.Single(row => row.Pid == 802).HarmonyUserId);
            Assert.Equal(3, rows.Single(row => row.Pid == 803).HarmonyUserId);
            Assert.Equal(400, rows.Single(row => row.Pid == 804).HarmonyUserId);
        }

        [Fact]
        public void DoesNotTreatSystemUidAsHarmonyProfileButAcceptsExplicitUserId()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "UID PID PPID ARGS\n"
                + "2000 805 1 com.example.systemuid\n"
                + "USERID PID PPID ARGS\n"
                + "100 806 1 com.example.work\n", "device");

            Assert.Equal(-1, rows.Single(row => row.Pid == 805).HarmonyUserId);
            Assert.Equal(100, rows.Single(row => row.Pid == 806).HarmonyUserId);
        }

        [Fact]
        public void MergesProcessViewsFieldByFieldWithoutLosingIdentity()
        {
            var first = new ProcessInfo
            {
                Pid = 901,
                Name = "com.example.rich",
                BundleId = "com.example.rich",
                DisplayName = "Rich App",
                DeviceUdid = "device",
                HarmonyUserId = 100,
                HarmonyStartTimeTicks = 12345,
                ForegroundApplication = true,
                Recommended = true
            };
            var second = new ProcessInfo
            {
                Pid = 901,
                Name = "com.example.rich",
                OwnerName = "appspawn",
                OwnerBundleId = "com.example.owner",
                StartedAt = "10:12:13",
                ApplicationState = "foreground",
                ApplicationExecutablePath = "/system/bin/com.example.rich",
                OwnershipVerified = true,
                OwnerPid = 12
            };

            List<ProcessInfo> merged = HarmonyLookupService.MergeProcesses(new[] { first, second });

            ProcessInfo result = Assert.Single(merged);
            Assert.Equal("com.example.rich", result.BundleId);
            Assert.Equal("Rich App", result.DisplayName);
            Assert.Equal(100, result.HarmonyUserId);
            Assert.Equal(12345, result.HarmonyStartTimeTicks);
            Assert.Equal("com.example.owner", result.OwnerBundleId);
            Assert.Equal("10:12:13", result.StartedAt);
            Assert.Equal("foreground", result.ApplicationState);
            Assert.Equal("/system/bin/com.example.rich", result.ApplicationExecutablePath);
            Assert.Equal(12, result.OwnerPid);
            Assert.True(result.OwnershipVerified);
            Assert.True(result.ForegroundApplication);
            Assert.True(result.Recommended);
        }

        [Fact]
        public void KeepsPidAndNameWhenKnownBundleOnlyAppearsForAnotherHarmonyUser()
        {
            var apps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.shared",
                    Name = "Shared App",
                    Platform = "harmony",
                    HarmonyUserId = 0,
                    HarmonyUserIds = new List<int> { 0 }
                }
            };
            var processes = HarmonyLookupService.ParseProcesses(
                "UID PID PPID ARGS\n"
                + "u100_a123 1702 1 com.example.shared:worker\n", "device");

            HarmonyLookupService.MergeProcessApps(apps, processes);

            AppInfo workProcess = Assert.Single(apps, app => app.HarmonyUserId == 100);
            Assert.Equal("com.example.shared", workProcess.BundleId);
            Assert.Equal(1702, workProcess.ProcessPid);
            Assert.Equal("com.example.shared:worker", workProcess.ProcessName);
            Assert.True(workProcess.IsRunning);
            Assert.True(workProcess.IsProcessOnly);
            Assert.Equal(new[] { 100 }, workProcess.HarmonyUserIds);
        }

        [Fact]
        public void CarriesUniqueRunningProcessEvidenceToKnownAppRowsAndClearsAmbiguousBinding()
        {
            var uniqueApps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.unique",
                    Name = "Unique App",
                    Platform = "harmony",
                    HarmonyUserId = 100,
                    HarmonyUserIds = new List<int> { 100 }
                }
            };
            var uniqueProcess = new ProcessInfo
            {
                Pid = 1801,
                Name = "com.example.unique",
                BundleId = "com.example.unique",
                Platform = "harmony",
                HarmonyUserId = 100,
                HarmonyStartTimeTicks = 9911
            };

            HarmonyLookupService.MergeProcessApps(uniqueApps, new[] { uniqueProcess });

            AppInfo uniqueApp = Assert.Single(uniqueApps);
            Assert.Equal(1801, uniqueApp.ProcessPid);
            Assert.Equal("com.example.unique", uniqueApp.ProcessName);

            var ambiguousApps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.shared",
                    Name = "Shared App",
                    Platform = "harmony",
                    HarmonyUserId = 100,
                    HarmonyUserIds = new List<int> { 100 }
                }
            };
            var ambiguousProcesses = new[]
            {
                new ProcessInfo
                {
                    Pid = 1802,
                    Name = "com.example.shared:render",
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = 100
                },
                new ProcessInfo
                {
                    Pid = 1803,
                    Name = "com.example.shared:worker",
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = -1
                }
            };

            HarmonyLookupService.MergeProcessApps(ambiguousApps, ambiguousProcesses);

            AppInfo ambiguousApp = Assert.Single(ambiguousApps, app => app.HarmonyUserId == 100);
            Assert.Equal(0, ambiguousApp.ProcessPid);
            Assert.Empty(ambiguousApp.ProcessName);
            Assert.True(ambiguousApp.IsRunning);
            AppInfo unknownProcess = Assert.Single(ambiguousApps, app => app.ProcessPid == 1803);
            Assert.True(unknownProcess.IsProcessOnly);
            Assert.Equal(-1, unknownProcess.HarmonyUserId);
        }

        [Fact]
        public void ClearsProcessOnlyPidWhenAnotherProcessAppearsForTheSameUser()
        {
            var apps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = 0,
                    HarmonyUserIds = new List<int> { 0 }
                }
            };
            ProcessInfo[] processes =
            {
                new ProcessInfo
                {
                    Pid = 1703,
                    Name = "com.example.shared:render",
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = 100
                },
                new ProcessInfo
                {
                    Pid = 1704,
                    Name = "com.example.shared:worker",
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = 100
                }
            };

            HarmonyLookupService.MergeProcessApps(apps, processes);

            AppInfo workProcess = Assert.Single(apps, app => app.HarmonyUserId == 100);
            Assert.Equal(0, workProcess.ProcessPid);
            Assert.Empty(workProcess.ProcessName);
            Assert.True(workProcess.IsProcessOnly);
        }

        [Theory]
        [InlineData("/system/bin/appspawn --bundle-name com.example.launcher --user 0", "appspawn", "")]
        [InlineData("/system/bin/service --log /data/log/service.log", "service", "")]
        [InlineData("com.example.game:worker --parent com.example.host", "com.example.game:worker", "com.example.game")]
        public void ProcessArgumentsDoNotReplaceExecutableIdentity(string command, string name, string bundle)
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "PID ARGS\n601 " + command + "\n", "device");

            Assert.Single(rows);
            Assert.Equal(bundle, rows[0].BundleId);
            Assert.Equal(name, rows[0].Name);
        }

        [Fact]
        public void HeaderlessServiceArgumentsDoNotBecomeProcessIdentity()
        {
            var process = Assert.Single(HarmonyLookupService.ParseProcesses(
                "42 u100_a123 1 0 10:25:33 ? 00:00:01 service --bundle com.example.game\n", "device"));
            Assert.Equal("service", process.Name);
            Assert.Empty(process.BundleId);
            Assert.Equal(100, process.HarmonyUserId);
        }

        [Fact]
        public void RemoteArgumentsStayQuotedAndDisplayInfoExcludesVirtualScreens()
        {
            var args = HarmonyLookupService.TargetArgs("serial", "aa", "start", "-a", "test';echo injected");
            Assert.Equal(new[] { "-t", "serial", "shell", "'aa' 'start' '-a' 'test'\"'\"';echo injected'" }, args);
            Assert.Equal(
                new[] { "surface", "screen" },
                HarmonyLookupService.BuildRenderServiceInfoCommands().Select(command => command[4]));
            Assert.Equal("1080x2400", HarmonyLookupService.ParseResolution(
                "screen[0]: id=0, render resolution=1080x2400, physical resolution=1080x2400, isVirtual=false\n"
                + "screen[1]: render resolution=400x600, isVirtual=true\n"));
            Assert.Equal("1440x2560", HarmonyLookupService.ParseResolution("physicalResolution: 1440 x 2560\n"));
            Assert.Equal("1080x2400", HarmonyLookupService.ParseResolution("width=1080 height=2400\n"));
            Assert.Equal("", HarmonyLookupService.ParseResolution("error code 1080x2400"));
        }

        [Fact]
        public void ParsesHarmonyVendorCpuSummaryWhenProcCpuInfoIsBlocked()
        {
            Assert.Equal("ARM64 / 2核 / 2.62 GHz", HarmonyLookupService.ParseCpuInfo(
                "aarch64\n"
                + "cmd is: cat /sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq\n\n1530000\n"
                + "cmd is: cat /sys/devices/system/cpu/cpu11/cpufreq/cpuinfo_max_freq\n\n2620000\n"));
        }

        [Fact]
        public void ParsesHarmonyRenderServiceGlesRenderer()
        {
            Assert.Equal("Maleoon 910", HarmonyLookupService.ParseGpuInfo(
                "GL_VENDOR: HUAWEI\nGL_RENDERER: Maleoon 910\nGL_VERSION: OpenGL ES 3.2\n"));
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
        public void ParsesVerboseTargetStatesWithoutAssumingUsbColumnPosition()
        {
            PlatformDeviceDiscovery report = HarmonyLookupService.ParseDiscovery(new ProcessResult(
                0,
                "ABC123 tcp:127.0.0.1:8710 CONNECTED extra\nDEF456 usb AUTHORIZED extra\n",
                ""));

            Assert.Equal(new[] { "ABC123", "DEF456" }, report.Devices.Select(device => device.Udid));
            Assert.DoesNotContain("不可采集", report.Diagnostic);
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

        [Fact]
        public async Task FakeHdcCoversMultiUserInventoryProcessBindingAndAbilityLaunch()
        {
            List<string> commands = new List<string>();
            int activeShells = 0;
            int maxActiveShells = 0;

            async Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                int current = Interlocked.Increment(ref activeShells);
                int observed;
                do
                {
                    observed = maxActiveShells;
                    if (current <= observed) break;
                }
                while (Interlocked.CompareExchange(ref maxActiveShells, current, observed) != observed);

                try
                {
                    await Task.Delay(1, token);
                    if (key == "pm list users")
                        return new ProcessResult(0, "Users:\n UserInfo{0:Owner:13} running\n UserInfo{100:Work:13} running\n", "");
                    if (key == "bm dump -a" || key == "bm dump -a -u 0" || key == "bm dump -a --user-id 0")
                        return new ProcessResult(0,
                            "{\"bundleName\":\"com.example.native\",\"versionName\":\"1.0\",\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"}]}\n",
                            "");
                    if (key == "bm dump -a -u 100" || key == "bm dump -a --user-id 100")
                        return new ProcessResult(0,
                            "{\"bundleName\":\"com.example.work\",\"versionName\":\"2.0\",\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"WorkAbility\"}]}\n",
                            "");
                    if (key.StartsWith("bm dump -a ", StringComparison.Ordinal))
                        return new ProcessResult(0, "", "");
                    if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                        || key.StartsWith("cmd package list packages", StringComparison.Ordinal))
                        return new ProcessResult(0, "package:com.example.compat\n", "");
                    if (key.StartsWith("ps", StringComparison.Ordinal))
                        return new ProcessResult(0,
                            "UID PID PPID C STIME TTY TIME CMD\n"
                            + "u100_a1 501 1 0 10:00 ? 00:00:01 com.example.work\n"
                            + "u0_a1 502 1 0 10:00 ? 00:00:01 com.example.native\n",
                            "");
                    if (key.StartsWith("sh -c ", StringComparison.Ordinal))
                        return new ProcessResult(0, "", "");
                    if (key == "bm dump -n com.example.work -u 100")
                        return new ProcessResult(1, "", "unknown option: -u");
                    if (key == "bm dump -n com.example.work --user-id 100")
                        return new ProcessResult(0,
                            "{\"bundleName\":\"com.example.work\",\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"WorkAbility\"}]}\n",
                            "");
                    if (key == "aa start -u 100 -b com.example.work -m entry -a WorkAbility")
                        return new ProcessResult(0, "Ability started", "");
                    return new ProcessResult(1, "", "unsupported fake HDC command");
                }
                finally
                {
                    Interlocked.Decrement(ref activeShells);
                }
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            Task<List<AppInfo>> appsTask = service.ListAppsAsync("HARMONY-1", CancellationToken.None);
            Task<List<ProcessInfo>> processesTask = service.ListProcessesAsync("HARMONY-1", CancellationToken.None);
            await Task.WhenAll(appsTask, processesTask);

            List<AppInfo> apps = await appsTask;
            List<ProcessInfo> processes = await processesTask;
            AppInfo workApp = Assert.Single(apps, app => app.BundleId == "com.example.work");
            Assert.Equal(new[] { 100 }, workApp.HarmonyUserIds);
            Assert.Equal("2.0", workApp.Version);
            AppInfo nativeApp = Assert.Single(apps, app => app.BundleId == "com.example.native");
            Assert.Equal(new[] { 0 }, nativeApp.HarmonyUserIds);
            Assert.Contains(apps, app => app.BundleId == "com.example.compat");

            ProcessInfo workProcess = Assert.Single(processes, process => process.BundleId == "com.example.work");
            Assert.Equal(501, workProcess.Pid);
            Assert.Equal(100, workProcess.HarmonyUserId);
            Assert.Equal("harmony", workProcess.Platform);

            ProcessResult launch = await service.LaunchAppAsync(
                "HARMONY-1",
                workApp.BundleId,
                workApp.HarmonyUserIds.Where(userId => userId == 100),
                CancellationToken.None);

            Assert.Equal(0, launch.ExitCode);
            Assert.Contains("bm dump -n com.example.work -u 100", commands);
            Assert.Contains("bm dump -n com.example.work --user-id 100", commands);
            Assert.Contains("aa start -u 100 -b com.example.work -m entry -a WorkAbility", commands);
            Assert.Equal(1, maxActiveShells);
        }

        [Fact]
        public async Task UnifiedTargetInventoryUsesOneProcessAndStartTimePass()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "pm list users")
                    return Task.FromResult(new ProcessResult(0, "UserInfo{0:Owner:13} running\n", ""));
                if (key == "cmd user list")
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key.StartsWith("bm dump -a", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.snapshot\",\"versionName\":\"1.0\","
                        + "\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"}]}\n", ""));
                if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key == "ps -A -o PID,ARGS")
                    return Task.FromResult(new ProcessResult(0,
                        "PID ARGS\n801 com.example.snapshot\n", ""));
                if (key.StartsWith("ps ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key.StartsWith("sh -c ", StringComparison.Ordinal))
                {
                    string stat = "801 (com.example.snapshot) "
                        + string.Join(" ", Enumerable.Repeat("0", 19))
                        + " 12345\n";
                    return Task.FromResult(new ProcessResult(0, stat, ""));
                }
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            HarmonyTargetInventory snapshot = await service.ListTargetsAsync("HARMONY-1", CancellationToken.None);

            AppInfo app = Assert.Single(snapshot.Apps, candidate => candidate.BundleId == "com.example.snapshot" && candidate.HarmonyUserId == 0);
            AppInfo unknownProcess = Assert.Single(snapshot.Apps, candidate => candidate.ProcessPid == 801);
            ProcessInfo process = Assert.Single(snapshot.Processes);
            Assert.False(app.IsRunning);
            Assert.True(unknownProcess.IsProcessOnly);
            Assert.Equal(-1, unknownProcess.HarmonyUserId);
            Assert.Equal(801, process.Pid);
            Assert.Equal(12345, process.HarmonyStartTimeTicks);
            Assert.Equal(HarmonyLookupService.BuildProcessInventoryCommands().Count,
                commands.Count(command => string.Equals(command, "ps", StringComparison.Ordinal)
                    || command.StartsWith("ps ", StringComparison.Ordinal)));
            int expectedShellCommands = HarmonyLookupService.BuildProcessStatCommands(new[] { 801 }).Count
                + HarmonyLookupService.BuildProcProcessInventoryCommands().Count;
            Assert.Equal(expectedShellCommands,
                commands.Count(command => command.StartsWith("sh -c ", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task FakeHdcKeepsSameBundleRowsAndPidsSeparatedByHarmonyUser()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users")
                    return Task.FromResult(new ProcessResult(0,
                        "UserInfo{0:Owner:13} running\nUserInfo{100:Work:13} running\n", ""));
                if (key == "cmd user list")
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key == "bm dump -a")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.shared\",\"userId\":0,"
                        + "\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"}]}\n"
                        + "{\"bundleName\":\"com.example.shared\",\"userId\":100,"
                        + "\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"}]}\n", ""));
                if (key.StartsWith("bm dump -a ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "UID PID PPID C STIME TTY TIME CMD\n"
                        + "u0_a1 701 1 0 10:00 ? 00:00:01 com.example.shared\n"
                        + "u100_a1 702 1 0 10:00 ? 00:00:01 com.example.shared:worker\n", ""));
                if (key.StartsWith("sh -c ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<AppInfo> apps = await service.ListAppsAsync("HARMONY-1", CancellationToken.None);

            List<AppInfo> shared = apps.Where(app => app.BundleId == "com.example.shared").ToList();
            Assert.Equal(2, shared.Count);
            Assert.Equal(new[] { 0, 100 }, shared.Select(app => app.HarmonyUserId).OrderBy(id => id));
            Assert.All(shared, app => Assert.True(app.IsRunning));
        }

        [Fact]
        public async Task FakeHdcStartsCompatibilityAppThroughResolvedLauncherComponent()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key.StartsWith("bm dump -n com.example.compat", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "bundleName: com.example.compat\n", ""));
                if (key.StartsWith("aa start ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unknown ability"));
                if (key == "cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER --user 0 com.example.compat")
                    return Task.FromResult(new ProcessResult(0, "com.example.compat/.MainActivity\n", ""));
                if (key.StartsWith("cmd package resolve-activity ", StringComparison.Ordinal)
                    || key.StartsWith("pm resolve-activity ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "not found"));
                if (key == "am start --user 0 -n com.example.compat/com.example.compat.MainActivity")
                    return Task.FromResult(new ProcessResult(0, "Starting: Intent { ... }", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync(
                "HARMONY-1",
                "com.example.compat",
                new[] { 0 },
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER --user 0 com.example.compat", commands);
            Assert.Contains("am start --user 0 -n com.example.compat/com.example.compat.MainActivity", commands);
        }

        [Fact]
        public async Task FakeHdcAllowsBundleProcessOnlyTargetToTryPackageLaunch()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key.StartsWith("bm dump -n com.example.processonly", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "bundleName: com.example.processonly\n", ""));
                if (key.StartsWith("aa start ", StringComparison.Ordinal)
                    || key.StartsWith("cmd package resolve-activity ", StringComparison.Ordinal)
                    || key.StartsWith("pm resolve-activity ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "not supported"));
                if (key == "am start --user 0 -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p com.example.processonly")
                    return Task.FromResult(new ProcessResult(0, "Starting: Intent { ... }", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", new AppInfo
            {
                BundleId = "com.example.processonly",
                Platform = "harmony",
                IsProcessOnly = true,
                HarmonyUserId = 0
            }, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("am start --user 0 -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p com.example.processonly", commands);
        }

        [Fact]
        public async Task FakeHdcNeverStartsCompatibilityAppAcrossUsersAfterScopedLaunchFails()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER --user 100 com.example.compat")
                    return Task.FromResult(new ProcessResult(0, "com.example.compat/.MainActivity\n", ""));
                if (key == "am start -n com.example.compat/com.example.compat.MainActivity")
                    return Task.FromResult(new ProcessResult(0, "Starting: Intent { ... }", ""));
                if (key.StartsWith("am start --user 100 -n ", StringComparison.Ordinal)
                    || key.StartsWith("am start --user 100 ", StringComparison.Ordinal)
                    || key.StartsWith("monkey --user 100 ", StringComparison.Ordinal)
                    || key.StartsWith("aa start ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "scoped launch rejected"));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync(
                "HARMONY-1",
                "com.example.compat",
                new[] { 100 },
                CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("am start --user 100 -n com.example.compat/com.example.compat.MainActivity", commands);
            Assert.DoesNotContain("am start -n com.example.compat/com.example.compat.MainActivity", commands);
            Assert.DoesNotContain(commands, command => command.Contains("resolve-activity", StringComparison.OrdinalIgnoreCase)
                && !command.Contains("--user", StringComparison.OrdinalIgnoreCase)
                && !command.Contains("-u", StringComparison.OrdinalIgnoreCase)
                && !command.Contains("-U", StringComparison.OrdinalIgnoreCase)
                && !command.Contains("--user-id", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task FakeHdcUnifiedInventoryMergesNativeSystemStoppedAndRunningOnlyApplications()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users")
                    return Task.FromResult(new ProcessResult(0, "UserInfo{0:Owner:13} running\n", ""));
                if (key == "bm dump -a")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.native\",\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"}]}\n",
                        ""));
                if (key.StartsWith("bm dump -a ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported user option"));
                if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessResult(0,
                        "package:com.example.compat\n"
                        + "package:com.example.system.preinstalled\n"
                        + "package:com.example.stopped\n", ""));
                }
                if (key.StartsWith("ps", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessResult(0,
                        "UID PID PPID C STIME TTY TIME CMD\n"
                        + "u0_a1 501 1 0 10:00 ? 00:00:01 com.example.native\n"
                        + "u0_a2 502 1 0 10:00 ? 00:00:01 com.example.running:worker\n", ""));
                }
                if (key.StartsWith("sh -c ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            HarmonyTargetInventory snapshot = await service.ListTargetsAsync("HARMONY-1", CancellationToken.None);
            List<AppInfo> apps = snapshot.Apps;

            Assert.Contains(apps, app => app.BundleId == "com.example.native" && app.HasLaunchEntry);
            Assert.Contains(apps, app => app.BundleId == "com.example.compat");
            Assert.Contains(apps, app => app.BundleId == "com.example.system.preinstalled");
            Assert.Contains(apps, app => app.BundleId == "com.example.stopped");
            Assert.Contains(apps, app => app.BundleId == "com.example.running" && app.IsProcessOnly);
            Assert.DoesNotContain(apps, app => app.BundleId == "com.example.unlisted");
            Assert.Contains(snapshot.Processes, process => process.BundleId == "com.example.native");
            Assert.Contains(snapshot.Processes, process => process.BundleId == "com.example.running");
        }

        [Fact]
        public async Task FakeHdcKeepsApplicationsWhenEveryProcessInventoryCommandFails()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users" || key == "cmd user list")
                    return Task.FromResult(new ProcessResult(0, "UserInfo{0:Owner:13} running\n", ""));
                if (key.StartsWith("bm dump -a", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.available\",\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"}]}\n",
                        ""));
                if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key.StartsWith("ps", StringComparison.Ordinal))
                    throw new IOException("permission denied while reading process table");
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            HarmonyTargetInventory snapshot = await new HarmonyLookupService(ExecuteFakeHdcAsync)
                .ListTargetsAsync("HARMONY-1", CancellationToken.None);

            AppInfo app = Assert.Single(snapshot.Apps, candidate => candidate.BundleId == "com.example.available");
            Assert.True(app.HasLaunchEntry);
            Assert.Empty(snapshot.Processes);
            Assert.Contains("无法读取鸿蒙进程", snapshot.ProcessInventoryError);
        }

        [Fact]
        public async Task FakeHdcReportsCombinedFailureWhenApplicationsAndProcessesAreUnavailable()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users" || key == "cmd user list")
                    throw new IOException("HDC authorization unavailable");
                if (key.StartsWith("bm dump -a", StringComparison.Ordinal)
                    || key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal)
                    || key.StartsWith("ps", StringComparison.Ordinal))
                    throw new IOException("HDC authorization unavailable");
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            Exception error = await Assert.ThrowsAsync<IOException>(delegate
            {
                return new HarmonyLookupService(ExecuteFakeHdcAsync)
                    .ListTargetsAsync("HARMONY-1", CancellationToken.None);
            });

            Assert.Contains("无法读取鸿蒙应用和进程列表", error.Message);
            Assert.Contains("无法读取鸿蒙进程", error.Message);
        }

        [Fact]
        public async Task FakeHdcUsesStructuredSecondaryUserOutputForPackageInventory()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users")
                    return Task.FromResult(new ProcessResult(1, "", "pm wrapper unavailable"));
                if (key == "cmd user list")
                    return Task.FromResult(new ProcessResult(0, "UserInfo{id=100,name=Work} running\n", ""));
                if (key == "bm dump -a")
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key.StartsWith("bm dump -a ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported scoped Bundle Manager output"));
                if (key == "cmd package list packages --user 100")
                    return Task.FromResult(new ProcessResult(0, "package:com.example.work.profile\n", ""));
                if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "package:com.example.owner\n", ""));
                if (key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<AppInfo> apps = await service.ListAppsAsync("HARMONY-1", CancellationToken.None);

            AppInfo work = Assert.Single(apps, app => app.BundleId == "com.example.work.profile");
            Assert.Equal(new[] { 100 }, work.HarmonyUserIds);
            Assert.Contains(apps, app => app.BundleId == "com.example.owner");
        }

        [Fact]
        public async Task NativeAbilityDetailSupportsDocumentedLongUserOption()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "bm dump -n com.example.work --user-id 100")
                    return Task.FromResult(new ProcessResult(0,
                        "bundleName: com.example.work\n"
                        + "hapModuleInfos:\n"
                        + "  - moduleName: entry\n"
                        + "    abilityInfos:\n"
                        + "      - name: WorkAbility\n", ""));
                if (key.StartsWith("bm dump -n com.example.work ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unknown option"));
                if (key == "bm dump -n com.example.work")
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (key == "aa start -u 100 -b com.example.work -m entry -a WorkAbility")
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync(
                "HARMONY-1",
                "com.example.work",
                new[] { 100 },
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("aa start -u 100 -b com.example.work -m entry -a WorkAbility", commands);
            Assert.DoesNotContain(commands, command => command.StartsWith("aa start --user", StringComparison.Ordinal)
                || command.StartsWith("aa start -U", StringComparison.Ordinal));
            Assert.DoesNotContain("aa start -b com.example.work -m entry -a WorkAbility", commands);
        }

        [Fact]
        public async Task NeverReadsUnscopedNativeAbilityForSecondaryUserWhenScopedQueriesFail()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key.StartsWith("bm dump -n com.example.work ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "scoped Bundle Manager lookup rejected"));
                if (key == "bm dump -n com.example.work")
                    return Task.FromResult(new ProcessResult(0,
                        "bundleName: com.example.work\n"
                        + "hapModuleInfos:\n"
                        + "  - moduleName: entry\n"
                        + "    abilityInfos:\n"
                        + "      - name: OwnerOnlyAbility\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "launch unavailable"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync(
                "HARMONY-1",
                "com.example.work",
                new[] { 100 },
                CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(commands, command => command == "bm dump -n com.example.work -u 100");
            Assert.Contains(commands, command => command == "bm dump -n com.example.work --user-id 100");
            Assert.DoesNotContain(commands, command => command == "bm dump -n com.example.work --user 100"
                || command == "bm dump -n com.example.work -U 100");
            Assert.DoesNotContain(commands, command => command == "bm dump -n com.example.work");
            Assert.DoesNotContain(commands, command => command.Contains("OwnerOnlyAbility", StringComparison.Ordinal));
        }

        [Fact]
        public async Task FallsBackToKnownAbilityWhenDeviceRejectsModuleArgument()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "aa start -u 0 -b com.example.moduleless -m entry -a EntryAbility")
                    return Task.FromResult(new ProcessResult(1, "", "module option unsupported"));
                if (key == "aa start -u 0 -b com.example.moduleless -a EntryAbility")
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync(
                "HARMONY-1",
                new AppInfo
                {
                    BundleId = "com.example.moduleless",
                    Platform = "harmony",
                    HarmonyUserId = 0,
                    HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                    {
                        new HarmonyLaunchEntryInfo { Module = "entry", Ability = "EntryAbility" }
                    }
                },
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("aa start -u 0 -b com.example.moduleless -m entry -a EntryAbility", commands);
            Assert.Contains("aa start -u 0 -b com.example.moduleless -a EntryAbility", commands);
        }

        [Fact]
        public async Task KeepsBundleManagerAppsWhenCommandReturnsPartialPayloadWithNonZeroExitCode()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users")
                    return Task.FromResult(new ProcessResult(0, "UserInfo{0:Owner:13} running\n", ""));
                if (key == "bm dump -a")
                    return Task.FromResult(new ProcessResult(1,
                        "{\"bundleName\":\"com.example.partial.native\",\"versionName\":\"1.0\",\"userId\":100}\n",
                        "warning: one bundle could not be read"));
                if (key.StartsWith("bm dump -a ", StringComparison.Ordinal)
                    || key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal)
                    || key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported or unavailable"));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<AppInfo> apps = await service.ListAppsAsync("HARMONY-1", CancellationToken.None);

            AppInfo partial = Assert.Single(apps, app => app.BundleId == "com.example.partial.native");
            Assert.Equal(new[] { 100 }, partial.HarmonyUserIds);
        }

        [Fact]
        public async Task KeepsCompatibilityPackagesWhenCommandReturnsPartialPayloadWithNonZeroExitCode()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users")
                    return Task.FromResult(new ProcessResult(0, "UserInfo{0:Owner:13} running\n", ""));
                if (key.StartsWith("pm list packages", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "package:com.example.partial.compat\n", "warning: package service returned partial data"));
                if (key.StartsWith("bm dump -a ", StringComparison.Ordinal)
                    || key == "bm dump -a"
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal)
                    || key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported or unavailable"));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<AppInfo> apps = await service.ListAppsAsync("HARMONY-1", CancellationToken.None);

            Assert.Contains(apps, app => app.BundleId == "com.example.partial.compat");
        }

        [Fact]
        public async Task KeepsProcessRowsWhenProcessCommandReturnsPartialPayloadWithNonZeroExitCode()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "ps -A -o PID,ARGS")
                    return Task.FromResult(new ProcessResult(1,
                        "PID ARGS\n801 com.example.partial.process\n",
                        "warning: process table was truncated"));
                if (key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported or unavailable"));
                if (key.StartsWith("sh -c ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<ProcessInfo> processes = await service.ListProcessesAsync("HARMONY-1", CancellationToken.None);

            Assert.Contains(processes, process => process.Pid == 801 && process.BundleId == "com.example.partial.process");
        }

        [Fact]
        public async Task FallsBackToUidPidPpidArgsProcessCommandVariant()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "ps -A -o UID,PID,PPID,ARGS")
                    return Task.FromResult(new ProcessResult(0,
                        "UID PID PPID ARGS\n"
                        + "u100_a1 902 1 com.example.rich\n", ""));
                if (key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported ps columns"));
                if (key.StartsWith("sh -c ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<ProcessInfo> processes = await service.ListProcessesAsync("HARMONY-1", CancellationToken.None);

            ProcessInfo process = Assert.Single(processes);
            Assert.Equal(902, process.Pid);
            Assert.Equal("com.example.rich", process.BundleId);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Contains("ps -A -o UID,PID,PPID,ARGS", commands);
        }

        [Fact]
        public async Task KeepsReadableProcessStartTimesWhenAStatBatchReturnsPartialPayload()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "ps -A -o PID,ARGS")
                    return Task.FromResult(new ProcessResult(1,
                        "PID ARGS\n801 com.example.partial.process\n",
                        "warning: process table was truncated"));
                if (key.StartsWith("ps", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported or unavailable"));
                if (key.StartsWith("sh -c ", StringComparison.Ordinal))
                {
                    string stat = "801 (com.example.partial.process) "
                        + string.Join(" ", Enumerable.Repeat("0", 19))
                        + " 12345\n";
                    return Task.FromResult(new ProcessResult(1, stat, "warning: one PID disappeared"));
                }
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<ProcessInfo> processes = await service.ListProcessesAsync("HARMONY-1", CancellationToken.None);

            ProcessInfo process = Assert.Single(processes);
            Assert.Equal(801, process.Pid);
            Assert.Equal(12345, process.HarmonyStartTimeTicks);
        }

        [Fact]
        public void SplitsOneBundleIntoSelectableHarmonyUserInstances()
        {
            var source = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.shared",
                    Name = "Shared App",
                    Platform = "harmony",
                    HarmonyUserIds = new List<int> { 100, 0 },
                    HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                    {
                        new HarmonyLaunchEntryInfo { Module = "entry", Ability = "EntryAbility" }
                    },
                    HasLaunchEntry = true
                }
            };

            List<AppInfo> rows = HarmonyLookupService.ExpandHarmonyUserInstances(source);

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { 0, 100 }, rows.Select(row => row.HarmonyUserId).OrderBy(id => id));
            Assert.All(rows, row => Assert.Equal(new[] { row.HarmonyUserId }, row.HarmonyUserIds));
            Assert.Contains(rows, row => row.TargetIdentifier == "com.example.shared · 用户 0");
            Assert.Contains(rows, row => row.TargetIdentifier == "com.example.shared · 用户 100");
            Assert.All(rows, row => Assert.Contains(row.HarmonyLaunchEntries, entry => entry.Ability == "EntryAbility"));
        }

        [Fact]
        public void KeepsUserSpecificLaunchEntriesOnTheirOwnHarmonyProfile()
        {
            List<AppInfo> source = HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.profiled\",\"userId\":0,"
                + "\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"OwnerAbility\"}]}\n"
                + "{\"bundleName\":\"com.example.profiled\",\"userId\":100,"
                + "\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"WorkAbility\"}]}\n");

            List<AppInfo> rows = HarmonyLookupService.ExpandHarmonyUserInstances(source);
            AppInfo owner = Assert.Single(rows, row => row.HarmonyUserId == 0);
            AppInfo work = Assert.Single(rows, row => row.HarmonyUserId == 100);

            Assert.Contains(owner.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
            Assert.DoesNotContain(owner.HarmonyLaunchEntries, entry => entry.Ability == "WorkAbility");
            Assert.Contains(work.HarmonyLaunchEntries, entry => entry.Ability == "WorkAbility");
            Assert.DoesNotContain(work.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
        }

        [Fact]
        public void UsesExplicitLaunchEntryUsersWhenProfileListIsMissing()
        {
            var source = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.entryscoped",
                    Name = "Entry Scoped",
                    Platform = "harmony",
                    HarmonyUserId = -1,
                    HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                    {
                        new HarmonyLaunchEntryInfo
                        {
                            Module = "entry",
                            Ability = "OwnerAbility",
                            IsUiEntry = true,
                            HarmonyUserId = 0
                        },
                        new HarmonyLaunchEntryInfo
                        {
                            Module = "entry",
                            Ability = "WorkAbility",
                            IsUiEntry = true,
                            HarmonyUserId = 100
                        }
                    }
                }
            };

            List<AppInfo> rows = HarmonyLookupService.ExpandHarmonyUserInstances(source);

            Assert.Equal(new[] { 0, 100 }, rows.Select(row => row.HarmonyUserId).OrderBy(id => id));
            AppInfo owner = Assert.Single(rows, row => row.HarmonyUserId == 0);
            AppInfo work = Assert.Single(rows, row => row.HarmonyUserId == 100);
            Assert.Contains(owner.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
            Assert.DoesNotContain(owner.HarmonyLaunchEntries, entry => entry.Ability == "WorkAbility");
            Assert.Contains(work.HarmonyLaunchEntries, entry => entry.Ability == "WorkAbility");
            Assert.DoesNotContain(work.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
        }

        [Fact]
        public async Task LaunchUsesTheOnlyExplicitLaunchEntryUserWhenProfileListIsMissing()
        {
            var starts = new List<string[]>();
            var app = new AppInfo
            {
                BundleId = "com.example.entryscoped",
                Platform = "harmony",
                HarmonyUserId = -1,
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo
                    {
                        Module = "entry",
                        Ability = "EntryAbility",
                        IsUiEntry = true,
                        HarmonyUserId = 100
                    }
                }
            };
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                starts.Add(command);
                return Task.FromResult(command[0] == "aa"
                    && command.Contains("-u")
                    && command.Contains("100")
                    ? new ProcessResult(0, "Ability started", "")
                    : new ProcessResult(1, "", "wrong or unscoped user"));
            });

            Assert.True(app.CanAttemptLaunch);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", app, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(starts, command => command[0] == "aa"
                && HarmonyLookupService.CommandUserId(command) == 100
                && command.Contains("EntryAbility"));
        }

        [Fact]
        public async Task LaunchesOnlyTheSelectedProfileEntryFromAggregatedInventory()
        {
            List<string> commands = new List<string>();
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "aa start -u 100 -b com.example.profiled -m entry -a WorkAbility")
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                if (key.StartsWith("aa start ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "wrong profile Ability"));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            AppInfo work = Assert.Single(
                HarmonyLookupService.ExpandHarmonyUserInstances(HarmonyLookupService.ParseApps(
                    "{\"bundleName\":\"com.example.profiled\",\"userId\":0,"
                    + "\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"OwnerAbility\"}]}\n"
                    + "{\"bundleName\":\"com.example.profiled\",\"userId\":100,"
                    + "\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"WorkAbility\"}]}\n")),
                app => app.HarmonyUserId == 100);

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", work, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("aa start -u 100 -b com.example.profiled -m entry -a WorkAbility", commands);
            Assert.DoesNotContain(commands, command => command.Contains("OwnerAbility", StringComparison.Ordinal));
        }

        [Fact]
        public async Task KeepsGlobalAndScopedEntriesSeparatedForSameBundleProfiles()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users" || key == "cmd user list")
                    return Task.FromResult(new ProcessResult(0,
                        "UserInfo{0:Owner:13} running\nUserInfo{100:Work:13} running\n", ""));
                if (key == "bm dump -a")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.profiled\","
                        + "\"hapModuleInfos\":[{\"moduleName\":\"entry\","
                        + "\"mainElementName\":\"GlobalAbility\"}]}\n", ""));
                if (key == "bm dump -a -u 0")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.profiled\","
                        + "\"hapModuleInfos\":[{\"moduleName\":\"entry\","
                        + "\"mainElementName\":\"OwnerAbility\"}]}\n", ""));
                if (key == "bm dump -a -u 100")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.profiled\","
                        + "\"hapModuleInfos\":[{\"moduleName\":\"entry\","
                        + "\"mainElementName\":\"WorkAbility\"}]}\n", ""));
                if (key.StartsWith("bm dump -a --user-id ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported user option"));
                if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal)
                    || key.StartsWith("ps", StringComparison.Ordinal)
                    || key.StartsWith("sh -c ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            List<AppInfo> apps = await new HarmonyLookupService(ExecuteFakeHdcAsync)
                .ListAppsAsync("HARMONY-1", CancellationToken.None);

            AppInfo owner = Assert.Single(apps, app => app.BundleId == "com.example.profiled" && app.HarmonyUserId == 0);
            AppInfo work = Assert.Single(apps, app => app.BundleId == "com.example.profiled" && app.HarmonyUserId == 100);

            Assert.Contains(owner.HarmonyLaunchEntries, entry => entry.Ability == "GlobalAbility");
            Assert.Contains(owner.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
            Assert.DoesNotContain(owner.HarmonyLaunchEntries, entry => entry.Ability == "WorkAbility");
            Assert.Contains(work.HarmonyLaunchEntries, entry => entry.Ability == "GlobalAbility");
            Assert.Contains(work.HarmonyLaunchEntries, entry => entry.Ability == "WorkAbility");
            Assert.DoesNotContain(work.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
            Assert.All(apps
                .SelectMany(app => app.HarmonyLaunchEntries)
                .Where(entry => entry.Ability == "GlobalAbility"),
                entry => Assert.Equal(-1, entry.HarmonyUserId));
            Assert.Equal(0, owner.HarmonyLaunchEntries.Single(entry => entry.Ability == "OwnerAbility").HarmonyUserId);
            Assert.Equal(100, work.HarmonyLaunchEntries.Single(entry => entry.Ability == "WorkAbility").HarmonyUserId);
        }

        [Fact]
        public void DoesNotCopyLiveProcessBindingToAnotherHarmonyUserInstance()
        {
            var source = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = 0,
                    HarmonyUserIds = new List<int> { 0, 100 },
                    ProcessPid = 701,
                    ProcessName = "com.example.shared",
                    IsRunning = true,
                    IsProcessOnly = true
                }
            };

            List<AppInfo> rows = HarmonyLookupService.ExpandHarmonyUserInstances(source);

            AppInfo owner = Assert.Single(rows, row => row.HarmonyUserId == 0);
            AppInfo work = Assert.Single(rows, row => row.HarmonyUserId == 100);
            Assert.Equal(701, owner.ProcessPid);
            Assert.Equal("com.example.shared", owner.ProcessName);
            Assert.Equal(0, work.ProcessPid);
            Assert.Empty(work.ProcessName);
            Assert.False(work.IsRunning);
            Assert.True(work.IsProcessOnly);
        }

        [Fact]
        public void BindsAnExplicitProcessUserToTheOnlyUnscopedBundleRow()
        {
            var apps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.unscoped",
                    Platform = "harmony",
                    Name = "Unscoped App"
                }
            };
            var processes = HarmonyLookupService.ParseProcesses(
                "UID PID PPID ARGS\n"
                + "u100_a1 801 1 com.example.unscoped\n", "device");

            HarmonyLookupService.MergeProcessApps(apps, processes);

            AppInfo app = Assert.Single(apps);
            Assert.Equal(100, app.HarmonyUserId);
            Assert.Equal(new[] { 100 }, app.HarmonyUserIds);
            Assert.True(app.IsRunning);
            Assert.False(app.IsProcessOnly);
        }

        [Fact]
        public async Task PrefersEmbeddedBundleUserOverTheScopedCommandUser()
        {
            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                if (key == "pm list users")
                    return Task.FromResult(new ProcessResult(0,
                        "UserInfo{0:Owner:13} running\nUserInfo{100:Work:13} running\n", ""));
                if (key.StartsWith("bm dump -a", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.embeddeduser\",\"userId\":100}\n", ""));
                if (key.StartsWith("pm list packages", StringComparison.Ordinal)
                    || key.StartsWith("cmd package list packages", StringComparison.Ordinal)
                    || key.StartsWith("ps", StringComparison.Ordinal)
                    || key.StartsWith("sh -c ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<AppInfo> apps = await service.ListAppsAsync("HARMONY-1", CancellationToken.None);

            AppInfo app = Assert.Single(apps, candidate => candidate.BundleId == "com.example.embeddeduser");
            Assert.Equal(100, app.HarmonyUserId);
            Assert.Equal(new[] { 100 }, app.HarmonyUserIds);
        }

        [Fact]
        public void InheritsUserScopeFromNestedBundleInventoryJson()
        {
            List<AppInfo> apps = HarmonyLookupService.ParseApps(
                "{\"userId\":100,\"bundleInfos\":["
                + "{\"bundleName\":\"com.example.nested\","
                + "\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"WorkAbility\"}]}"
                + "]}");

            AppInfo app = Assert.Single(HarmonyLookupService.ExpandHarmonyUserInstances(apps));
            Assert.Equal(100, app.HarmonyUserId);
            Assert.Equal(new[] { 100 }, app.HarmonyUserIds);
            HarmonyLaunchEntryInfo entry = Assert.Single(app.HarmonyLaunchEntries);
            Assert.Equal(100, entry.HarmonyUserId);
            Assert.Equal("WorkAbility", entry.Ability);
        }

        [Fact]
        public void NestedExplicitBundleUserOverridesInheritedInventoryScope()
        {
            List<AppInfo> apps = HarmonyLookupService.ParseApps(
                "{\"userId\":100,\"bundleInfos\":["
                + "{\"bundleName\":\"com.example.nested\",\"userId\":0,"
                + "\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"OwnerAbility\"}]}"
                + "]}");

            AppInfo app = Assert.Single(HarmonyLookupService.ExpandHarmonyUserInstances(apps));
            Assert.Equal(0, app.HarmonyUserId);
            Assert.Equal(new[] { 0 }, app.HarmonyUserIds);
            HarmonyLaunchEntryInfo entry = Assert.Single(app.HarmonyLaunchEntries);
            Assert.Equal(0, entry.HarmonyUserId);
            Assert.Equal("OwnerAbility", entry.Ability);
        }

        [Fact]
        public async Task LaunchesOnlyTheSelectedHarmonyUserInstance()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "aa start -u 100 -b com.example.shared -m entry -a EntryAbility")
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                if (key.StartsWith("aa start ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "wrong user must not launch"));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", new AppInfo
            {
                BundleId = "com.example.shared",
                Platform = "harmony",
                HarmonyUserId = 100,
                HarmonyUserIds = new List<int> { 0, 100 },
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo { Module = "entry", Ability = "EntryAbility" }
                }
            }, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("aa start -u 100 -b com.example.shared -m entry -a EntryAbility", commands);
            Assert.DoesNotContain(commands, command => command.Contains("-u 0", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RefusesToLaunchAnAppWithAmbiguousHarmonyUserScope()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                commands.Add(string.Join(" ", command ?? Array.Empty<string>()));
                return Task.FromResult(new ProcessResult(1, "", "should not be called"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", new AppInfo
            {
                BundleId = "com.example.shared",
                Platform = "harmony",
                HarmonyUserIds = new List<int> { 0, 100 }
            }, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("用户作用域不明确", result.Stderr);
            Assert.Empty(commands);
        }

        [Fact]
        public async Task DoesNotReuseLaunchAbilityFromAnotherHarmonyUser()
        {
            List<string> commands = new List<string>();

            Task<ProcessResult> ExecuteFakeHdcAsync(
                string serial,
                string[] command,
                int timeoutMs,
                CancellationToken token)
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (key == "bm dump -n com.example.profiled -u 0")
                    return Task.FromResult(new ProcessResult(0, "moduleName: entry\nabilityName: OwnerAbility\n", ""));
                if (key == "bm dump -n com.example.profiled -u 100")
                    return Task.FromResult(new ProcessResult(0, "moduleName: entry\nabilityName: WorkAbility\n", ""));
                if (key.StartsWith("bm dump -n com.example.profiled ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported detail option"));
                return Task.FromResult(new ProcessResult(1, "", "launch rejected"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            await service.LaunchAppAsync(
                "HARMONY-1",
                "com.example.profiled",
                new[] { 0, 100 },
                CancellationToken.None);

            Assert.Contains("aa start -u 0 -b com.example.profiled -m entry -a OwnerAbility", commands);
            Assert.Contains("aa start -u 100 -b com.example.profiled -m entry -a WorkAbility", commands);
            Assert.DoesNotContain(commands, command => command.Contains("aa start -u 100", StringComparison.Ordinal)
                && command.Contains("OwnerAbility", StringComparison.Ordinal));
        }

        [Fact]
        public void ParsesAbilityManagerProcessBindingsFromJsonAndText()
        {
            string output = "{\"appRunningRecords\":["
                + "{\"bundleName\":\"com.example.native\",\"pid\":501,\"userId\":0,\"isForeground\":true},"
                + "{\"bundleName\":\"com.example.compat\",\"processId\":502,\"uid\":201234},"
                + "{\"process_name\":\"/system/bin/com.example.worker --bundle-name com.example.other\",\"pid\":504}]}\n"
                + "AppRunningRecord ID #3\n"
                + "  bundle name [com.example.service]\n"
                + "  pid #503  uid #401234\n"
                + "  state #FOREGROUND\n";

            List<HarmonyProcessBinding> bindings = HarmonyLookupService.ParseAbilityProcessBindings(output);

            Assert.Contains(bindings, binding => binding.Pid == 501
                && binding.BundleId == "com.example.native"
                && binding.HarmonyUserId == 0
                && binding.Foreground);
            Assert.Contains(bindings, binding => binding.Pid == 502
                && binding.BundleId == "com.example.compat"
                && binding.HarmonyUserId == 1);
            Assert.Contains(bindings, binding => binding.Pid == 503
                && binding.BundleId == "com.example.service"
                && binding.HarmonyUserId == 2
                && binding.Foreground);
            Assert.Contains(bindings, binding => binding.Pid == 504
                && binding.ProcessName == "com.example.worker");
        }

        [Fact]
        public void JsonRelatedProcessAndMetadataPidsDoNotBecomeSelectableTargets()
        {
            string output = "{\"appRunningRecords\":["
                + "{\"bundleName\":\"com.example.game\",\"pid\":501,\"userId\":100,"
                + "\"state\":\"FOREGROUND\","
                + "\"rootCaller\":{\"pid\":700,\"bundleName\":\"com.example.caller\"},"
                + "\"uiExtensionProvider\":{\"processId\":701,\"processName\":\"com.example.extension\"},"
                + "\"metadata\":{\"pid\":702,\"bundleName\":\"com.example.metadata\"}},"
                + "{\"bundleName\":\"com.example.service\",\"processId\":502,\"userId\":100}]}";

            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(output);

            Assert.Equal(new[] { 501, 502 }, bindings.Select(binding => binding.Pid).OrderBy(pid => pid));
            Assert.Equal("com.example.game", bindings.Single(binding => binding.Pid == 501).BundleId);
            Assert.Equal("com.example.service", bindings.Single(binding => binding.Pid == 502).BundleId);
            Assert.DoesNotContain(bindings, binding => new[] { 700, 701, 702 }.Contains(binding.Pid));
        }

        [Fact]
        public void AbilityManagerBindingFillsGenericProcessAndPreservesForegroundRecommendation()
        {
            List<ProcessInfo> processes = HarmonyLookupService.ParseProcesses(
                "UID PID PPID NAME\n"
                + "u0 501 1 appspawn\n"
                + "u0 502 1 render_service\n", "HARMONY-1");

            HarmonyLookupService.ApplyAbilityProcessBindings(processes, new[]
            {
                new HarmonyProcessBinding
                {
                    Pid = 501,
                    BundleId = "com.example.native",
                    HarmonyUserId = 0,
                    Foreground = true
                },
                new HarmonyProcessBinding
                {
                    Pid = 502,
                    BundleId = "com.example.compat",
                    HarmonyUserId = 0
                }
            });

            ProcessInfo native = Assert.Single(processes, process => process.Pid == 501);
            Assert.Equal("com.example.native", native.BundleId);
            Assert.Equal("com.example.native", native.OwnerBundleId);
            Assert.True(native.OwnershipVerified);
            Assert.True(native.Recommended);
            Assert.Equal(0, native.HarmonyUserId);
            Assert.Equal("aa-dump", native.OwnershipSource);
            Assert.Equal("com.example.compat", processes.Single(process => process.Pid == 502).BundleId);
        }

        [Fact]
        public void GenericAppspawnIdentityCanBeCompletedByRealAbilityProcessEvidence()
        {
            List<ProcessInfo> processes = HarmonyLookupService.ParseProcesses(
                "UID PID PPID NAME\n"
                + "u0 501 1 appspawn\n", "HARMONY-1");

            HarmonyLookupService.ApplyAbilityProcessBindings(processes, new[]
            {
                new HarmonyProcessBinding
                {
                    Pid = 501,
                    BundleId = "com.example.native",
                    ProcessName = "/system/bin/com.example.native:worker",
                    HarmonyUserId = 0,
                    Foreground = true
                }
            });

            ProcessInfo process = Assert.Single(processes);
            Assert.Equal("com.example.native:worker", process.Name);
            Assert.Equal(process.Name, process.DisplayName);
            Assert.Equal("com.example.native", process.BundleId);
            Assert.True(process.OwnershipVerified);
            Assert.True(process.Recommended);
            Assert.False(process.OwnershipAmbiguous);
        }

        [Fact]
        public void ProcInventoryOwnershipCannotBeReplacedFromProcessNameInference()
        {
            List<ProcessInfo> processes = new List<ProcessInfo>
            {
                new ProcessInfo
                {
                    Pid = 501,
                    Name = "com.example.inferred:worker",
                    DisplayName = "com.example.inferred:worker",
                    BundleId = "com.example.inferred",
                    OwnershipSource = "proc-inventory",
                    Platform = "harmony",
                    DeviceUdid = "HARMONY-1",
                    HarmonyUserId = 0
                }
            };

            HarmonyLookupService.ApplyAbilityProcessBindings(processes, new[]
            {
                new HarmonyProcessBinding
                {
                    Pid = 501,
                    BundleId = "com.example.real",
                    HarmonyUserId = 0
                }
            });

            ProcessInfo process = Assert.Single(processes);
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.OwnershipVerified);
            Assert.Empty(process.BundleId);
            Assert.Empty(process.OwnerBundleId);
        }

        [Fact]
        public void AbilityManagerCommandAliasUsesExecutableTokenOnly()
        {
            List<ProcessInfo> processes = HarmonyLookupService.ParseProcesses(
                "UID PID PPID NAME\n"
                + "u0 501 1 appspawn\n", "HARMONY-1");

            HarmonyLookupService.ApplyAbilityProcessBindings(processes, new[]
            {
                new HarmonyProcessBinding
                {
                    Pid = 501,
                    ProcessName = "/system/bin/com.example.native --bundle-name com.example.other",
                    HarmonyUserId = 0
                }
            });

            ProcessInfo process = Assert.Single(processes);
            Assert.Equal("com.example.native", process.Name);
            Assert.Empty(process.BundleId);
            Assert.False(process.OwnershipVerified);
        }

        [Fact]
        public void ConflictingAbilityManagerBindingsRemainUnowned()
        {
            List<ProcessInfo> processes = HarmonyLookupService.ParseProcesses(
                "UID PID PPID NAME\n"
                + "u0 501 1 appspawn\n", "HARMONY-1");

            HarmonyLookupService.ApplyAbilityProcessBindings(processes, new[]
            {
                new HarmonyProcessBinding { Pid = 501, BundleId = "com.example.first", HarmonyUserId = 0 },
                new HarmonyProcessBinding { Pid = 501, BundleId = "com.example.second", HarmonyUserId = 0 }
            });

            ProcessInfo process = Assert.Single(processes);
            Assert.False(process.OwnershipVerified);
            Assert.True(process.OwnershipAmbiguous);
            Assert.DoesNotContain("com.example.first", process.BundleId);
            Assert.DoesNotContain("com.example.second", process.BundleId);
        }

        [Fact]
        public void AbilityInventoryCommandsKeepEachKnownUserScoped()
        {
            IReadOnlyList<string[]> commands = HarmonyLookupService.BuildAbilityInventoryCommands(new[] { 0, 100, 100 });

            Assert.Contains(commands, command => string.Join(" ", command) == "aa dump -a");
            Assert.Contains(commands, command => string.Join(" ", command) == "aa dump -a -u 0");
            Assert.Contains(commands, command => string.Join(" ", command) == "aa dump -a --userId 100");
            Assert.Contains(commands, command => string.Join(" ", command) == "aa dump -r -u 0");
            Assert.Contains(commands, command => string.Join(" ", command) == "aa dump -r --userId 100");
            Assert.DoesNotContain(commands, command => command.Contains("--user"));
            Assert.DoesNotContain(commands, command => string.Join(" ", command) == "aa dump -a -u 100 -u 0");
        }

        [Fact]
        public void ParsesHarmonyIconAndHapEvidenceOnlyForTheRequestedBundle()
        {
            string output = "{\"bundleName\":\"com.example.game\","
                + "\"applicationInfo\":{\"iconPath\":\"/data/app/com.example.game/icon.png\","
                + "\"hapPath\":\"/data/app/com.example.game/entry.hap\","
                + "\"icon\":\"resources/base/media/icon\"}}";

            HarmonyIconMetadata metadata = HarmonyLookupService.ParseHarmonyIconMetadata(output, "com.example.game");

            Assert.Contains("/data/app/com.example.game/icon.png", metadata.IconPaths);
            Assert.Contains("/data/app/com.example.game/entry.hap", metadata.HapPaths);
            Assert.Contains("resources/base/media/icon", metadata.IconEntries);
            Assert.Empty(HarmonyLookupService.ParseHarmonyIconMetadata(
                output.Replace("com.example.game", "com.other.game"), "com.example.game").IconPaths);
        }

        [Fact]
        public void SelectsReferencedHarmonyIconBeforeGenericResources()
        {
            string entry = HarmonyLookupService.SelectHarmonyIconEntry(
                new[]
                {
                    "resources/base/media/notification.png",
                    "resources/base/media/icon.webp",
                    "resources/base/media/entry_icon.png",
                    "resources/base/media/splash.png"
                },
                new[] { "resources/base/media/entry_icon.png" });

            Assert.Equal("resources/base/media/entry_icon.png", entry);
        }

        [Fact]
        public void HarmonyIconCommandsKeepKnownUserScopeAndDoNotInventUnknownScope()
        {
            IReadOnlyList<string[]> scoped = HarmonyLookupService.BuildIconMetadataCommands(
                "com.example.game", 100);
            IReadOnlyList<string[]> unscoped = HarmonyLookupService.BuildIconMetadataCommands(
                "com.example.game", -1);

            Assert.Contains(scoped, command => string.Join(" ", command) == "bm dump -n com.example.game -u 100");
            Assert.Contains(scoped, command => string.Join(" ", command) == "bm dump -n com.example.game --user-id 100");
            Assert.DoesNotContain(unscoped, command => string.Join(" ", command).Contains("--user-id"));
            Assert.DoesNotContain(unscoped, command => string.Join(" ", command).Contains(" -u "));
        }

        [Fact]
        public void HarmonyLaunchCapabilityIgnoresEntriesFromAnotherUser()
        {
            var app = new AppInfo
            {
                BundleId = "com.example.game",
                Platform = "harmony",
                HarmonyUserId = 100,
                HasLaunchEntry = true,
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo
                    {
                        Module = "entry",
                        Ability = "WorkOnlyAbility",
                        IsUiEntry = true,
                        HarmonyUserId = 101
                    }
                }
            };

            Assert.False(app.CanAttemptLaunch);
        }

        [Fact]
        public void PreservesHarmonyInstallEvidenceForDefaultPickerFiltering()
        {
            List<AppInfo> apps = HarmonyLookupService.ParseApps(
                "{\"bundleInfos\":["
                + "{\"bundleName\":\"com.example.user\",\"isSystemApp\":false,\"isPreInstallApp\":false,\"installSource\":\"user\"},"
                + "{\"bundleName\":\"com.example.system\",\"isSystemApp\":true,\"isPreInstallApp\":true,\"installSource\":\"pre-installed\"}]}\n");

            AppInfo user = Assert.Single(apps, app => app.BundleId == "com.example.user");
            AppInfo system = Assert.Single(apps, app => app.BundleId == "com.example.system");
            Assert.False(user.IsSystemApp);
            Assert.False(user.IsPreInstallApp);
            Assert.Equal("user", user.InstallSource);
            Assert.True(system.IsSystemApp);
            Assert.True(system.IsPreInstallApp);
            Assert.Equal("pre-installed", system.InstallSource);
        }

        [Fact]
        public void HarmonyFileReceiveUsesDeviceScopeOutsideTheRemoteShell()
        {
            IReadOnlyList<string> args = HarmonyLookupService.FileReceiveArgs(
                "SERIAL-1", "/data/app/com.example.game/icon.png", "C:/temp/icon.png");

            Assert.Equal(new[]
            {
                "-t", "SERIAL-1", "file", "recv", "/data/app/com.example.game/icon.png", "C:/temp/icon.png"
            }, args);
            Assert.DoesNotContain("shell", args);
        }
    }
}
