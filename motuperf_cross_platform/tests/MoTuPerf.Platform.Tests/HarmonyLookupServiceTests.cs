using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        public async Task LaunchReusesAggregateInventoryAbilityWhenPerBundleDumpIsUnavailable()
        {
            var app = Assert.Single(HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.native\",\"hapModuleInfos\":[{"
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
                if (key == "aa start -b com.example.native -m entry -a EntryAbility")
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", app, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(new[] { "aa start -b com.example.native -m entry -a EntryAbility" }, commands);
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
                if (key == "aa start -U 0 -b com.example.second -m feature -a SecondAbility")
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fake HDC command"));
            }

            var service = new HarmonyLookupService(ExecuteFakeHdcAsync);
            List<AppInfo> apps = await service.ListAppsAsync("HARMONY-1", CancellationToken.None);
            AppInfo selected = Assert.Single(apps, app => app.BundleId == "com.example.second");

            ProcessResult result = await service.LaunchAppAsync("HARMONY-1", selected, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("aa start -U 0 -b com.example.second -m feature -a SecondAbility", commands);
            Assert.DoesNotContain("aa start -U 0 -b com.example.first -m entry -a FirstAbility", commands);
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
            var commands = HarmonyLookupService.BuildPackageInventoryCommands(new[] { 100, 0, 100 });

            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "-f" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "-f", "--user", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "-f", "--user", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "-f", "-u", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "-f", "-U", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "-f", "--user-id", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "--user", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "pm", "list", "packages", "--user", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "-f" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "-f", "--user", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "-f", "--user", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "--user", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "--user", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "-u", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "-U", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "cmd", "package", "list", "packages", "--user-id", "100" }));
            Assert.Equal(36, commands.Count);
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
            var commands = HarmonyLookupService.BuildBundleManagerInventoryCommands(new[] { 100, 0, 100 });

            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "-u", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "-u", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "--user", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "--user", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "-U", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "-U", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "--user-id", "0" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "bm", "dump", "-a", "--user-id", "100" }));
            Assert.Equal(9, commands.Count);
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

            Assert.Equal(new[] { 0, 10, 12, 14, 100 }, ids);
        }

        [Fact]
        public void ReadsHarmonyUserIdFromShortAndLongCommandOptions()
        {
            Assert.Equal(100, HarmonyLookupService.CommandUserId(new[] { "bm", "dump", "-a", "-u", "100" }));
            Assert.Equal(101, HarmonyLookupService.CommandUserId(new[] { "bm", "dump", "-a", "--user", "101" }));
            Assert.Equal(102, HarmonyLookupService.CommandUserId(new[] { "aa", "start", "-U", "102", "-b", "com.example.app" }));
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
                "{\"bundleName\":\"com.example.work\",\"uid\":100123}\n"
                + "bundleName: com.example.text uid: 200234\n");

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
                + "{\"bundle_name\":\"com.example.native\",\"name\":\"Native\",\"abilityInfos\":[{\"name\":\"EntryAbility\"}]}\n");

            Assert.Equal(new[] { "com.example.keyed", "com.example.string.one", "com.example.string.two", "com.example.native" },
                apps.Select(app => app.BundleId));
            Assert.Equal("7", apps.Single(app => app.BundleId == "com.example.keyed").Version);
            Assert.Equal("Native", apps.Single(app => app.BundleId == "com.example.native").Name);
            Assert.True(apps.Single(app => app.BundleId == "com.example.native").HasLaunchEntry);
            Assert.Equal("有启动入口", apps.Single(app => app.BundleId == "com.example.native").LaunchAvailability);
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
        public void MapsNumericUidWhenVendorUsesUserColumn()
        {
            List<ProcessInfo> rows = HarmonyLookupService.ParseProcesses(
                "USER PID PPID NAME\n"
                + "100000 901 1 foundation\n"
                + "200000 902 1 com.example.work\n"
                + "100 903 1 com.example.owner\n",
                "harmony");

            Assert.Equal(1, rows.Single(row => row.Pid == 901).HarmonyUserId);
            Assert.Equal(2, rows.Single(row => row.Pid == 902).HarmonyUserId);
            Assert.Equal(100, rows.Single(row => row.Pid == 903).HarmonyUserId);
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
                + "pid: 602, cmd: /system/bin/com.example.other --render\n", "device");

            Assert.Equal(new[] { 601, 602 }, rows.Select(row => row.Pid).OrderBy(pid => pid));
            Assert.Equal("com.example.running", rows.Single(row => row.Pid == 601).BundleId);
            var apps = new List<AppInfo>();
            HarmonyLookupService.MergeProcessApps(apps, rows);
            Assert.All(apps, app => Assert.True(app.IsProcessOnly));
            Assert.All(apps, app => Assert.Equal("仅运行中可采集", app.LaunchAvailability));
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
                "UID PID PPID C STIME TTY TIME CMD\n100123 701 1 0 10:00 ? 00:00:01 com.example.game\n",
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
                + "803 300123 1 com.example.uidfirst\n"
                + "PID USER PPID ARGS\n"
                + "804 u400_a4 1 com.example.userfirst\n", "device");

            Assert.Equal(4, rows.Count);
            Assert.Equal(100, rows.Single(row => row.Pid == 801).HarmonyUserId);
            Assert.Equal(200, rows.Single(row => row.Pid == 802).HarmonyUserId);
            Assert.Equal(3, rows.Single(row => row.Pid == 803).HarmonyUserId);
            Assert.Equal(400, rows.Single(row => row.Pid == 804).HarmonyUserId);
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
                    if (key == "bm dump -a" || key == "bm dump -a -u 0" || key == "bm dump -a --user 0")
                        return new ProcessResult(0,
                            "{\"bundleName\":\"com.example.native\",\"versionName\":\"1.0\",\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"EntryAbility\"}]}\n",
                            "");
                    if (key == "bm dump -a -u 100" || key == "bm dump -a --user 100")
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
                    if (key == "bm dump -n com.example.work --user 100")
                        return new ProcessResult(0,
                            "{\"bundleName\":\"com.example.work\",\"hapModuleInfos\":[{\"moduleName\":\"entry\",\"mainElementName\":\"WorkAbility\"}]}\n",
                            "");
                    if (key == "aa start -U 100 -b com.example.work -m entry -a WorkAbility")
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
            Assert.Contains("bm dump -n com.example.work --user 100", commands);
            Assert.Contains("aa start -U 100 -b com.example.work -m entry -a WorkAbility", commands);
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

            AppInfo app = Assert.Single(snapshot.Apps, candidate => candidate.BundleId == "com.example.snapshot");
            ProcessInfo process = Assert.Single(snapshot.Processes);
            Assert.True(app.IsRunning);
            Assert.Equal(801, process.Pid);
            Assert.Equal(12345, process.HarmonyStartTimeTicks);
            Assert.Equal(HarmonyLookupService.BuildProcessInventoryCommands().Count,
                commands.Count(command => string.Equals(command, "ps", StringComparison.Ordinal)
                    || command.StartsWith("ps ", StringComparison.Ordinal)));
            Assert.Equal(1, commands.Count(command => command.StartsWith("sh -c ", StringComparison.Ordinal)));
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
        public async Task FakeHdcFallsBackAcrossNativeAbilityUserOptionSpellings()
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
                if (key == "bm dump -n com.example.work --user 100")
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
                if (key == "aa start -U 100 -b com.example.work -m entry -a WorkAbility")
                    return Task.FromResult(new ProcessResult(1, "", "unknown option"));
                if (key == "aa start --user 100 -b com.example.work -m entry -a WorkAbility")
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
            Assert.Contains("aa start -U 100 -b com.example.work -m entry -a WorkAbility", commands);
            Assert.Contains("aa start --user 100 -b com.example.work -m entry -a WorkAbility", commands);
            Assert.DoesNotContain("aa start -b com.example.work -m entry -a WorkAbility", commands);
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
                if (key == "aa start -U 0 -b com.example.moduleless -m entry -a EntryAbility"
                    || key == "aa start --user 0 -b com.example.moduleless -m entry -a EntryAbility"
                    || key == "aa start -u 0 -b com.example.moduleless -m entry -a EntryAbility"
                    || key == "aa start --user-id 0 -b com.example.moduleless -m entry -a EntryAbility")
                    return Task.FromResult(new ProcessResult(1, "", "module option unsupported"));
                if (key == "aa start -U 0 -b com.example.moduleless -a EntryAbility")
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
            Assert.Contains("aa start -U 0 -b com.example.moduleless -m entry -a EntryAbility", commands);
            Assert.Contains("aa start -U 0 -b com.example.moduleless -a EntryAbility", commands);
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
                if (key == "aa start -U 100 -b com.example.shared -m entry -a EntryAbility")
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
            Assert.Contains("aa start -U 100 -b com.example.shared -m entry -a EntryAbility", commands);
            Assert.DoesNotContain(commands, command => command.Contains("-U 0", StringComparison.Ordinal));
        }
    }
}
