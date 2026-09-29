using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyInventoryStreamTests
    {
        private static readonly ProcessResult Denied = new ProcessResult(1, "", "permission denied");

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        public async Task ProcessHeadersCannotHideTargetsOnTheOtherStream(bool reverse, int exitCode)
        {
            string table = "USERID PID COMM\n100 42 foundation\n";
            string headerless = "43 /system/bin/servicemanager --device\n";
            var service = new HarmonyLookupService((serial, command, timeout, token) => Task.FromResult(
                command[0] == "ps" ? new ProcessResult(exitCode,
                    reverse ? headerless : table, reverse ? table : headerless) : Denied));

            var rows = await service.ListProcessesAsync("HDC-1", CancellationToken.None);

            Assert.Equal(new[] { 42, 43 }, rows.OrderBy(row => row.Pid).Select(row => row.Pid));
            var native = rows.Single(row => row.Pid == 42);
            Assert.Equal("foundation", native.Name);
            Assert.True(native.HarmonyNameIsComm);
            Assert.Equal(100, native.HarmonyUserId);
            var system = rows.Single(row => row.Pid == 43);
            Assert.Equal("servicemanager", system.Name);
            Assert.False(system.HarmonyNameIsComm);
            Assert.Equal(-1, system.HarmonyUserId);
            Assert.Equal("HDC-1", system.DeviceUdid);
            Assert.Empty(system.BundleId);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task OrphanFieldsCannotGrantARealAppForeignEntriesOrAccount(bool appsOnly, bool reverse)
        {
            string bundle = "bundleName: com.example.real\n";
            string orphan = "userId: 101\nappName: Other application\nversionName: 99\n"
                + "moduleName: wrong\nabilityName: ForeignAbility\n";
            var calls = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                calls.Add(command);
                if (command[0] == "acm") return Task.FromResult(new ProcessResult(0, "ID: 100", ""));
                if (string.Join(" ", command) == "bm dump -a -u 100")
                    return Task.FromResult(new ProcessResult(1, reverse ? orphan : bundle, reverse ? bundle : orphan));
                return Task.FromResult(Denied);
            });

            var apps = appsOnly ? await service.ListAppsAsync("HDC-1", CancellationToken.None)
                : (await service.ListTargetsAsync("HDC-1", CancellationToken.None)).Apps;
            var app = Assert.Single(apps);
            Assert.Equal("com.example.real", app.BundleId);
            Assert.Equal("com.example.real", app.Name);
            Assert.Empty(app.Version);
            Assert.Equal(100, app.HarmonyUserId);
            Assert.Equal(new[] { 100 }, app.HarmonyUserIds);
            Assert.Empty(app.HarmonyLaunchEntries);
            Assert.False(app.HasLaunchEntry);
            await service.LaunchAppAsync("HDC-1", app, CancellationToken.None);
            Assert.DoesNotContain(calls, command => command.Contains("ForeignAbility"));
            Assert.DoesNotContain(calls, command => command.Contains("101"));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task CompleteAppRecordsOnBothStreamsRetainDistinctUsers(bool appsOnly, bool reverse)
        {
            string owner = "{\"bundleName\":\"com.example.shared\",\"userId\":100,\"moduleName\":\"main\",\"abilityName\":\"OwnerAbility\"}";
            string work = "{\"bundleName\":\"com.example.shared\",\"userId\":101,\"moduleName\":\"work\",\"abilityName\":\"WorkAbility\"}";
            var service = new HarmonyLookupService((serial, command, timeout, token) => Task.FromResult(
                command[0] == "bm" ? new ProcessResult(1, reverse ? work : owner, reverse ? owner : work) : Denied));
            var apps = appsOnly ? await service.ListAppsAsync("HDC-1", CancellationToken.None)
                : (await service.ListTargetsAsync("HDC-1", CancellationToken.None)).Apps;
            Assert.Equal(new[] { 100, 101 }, apps.Select(app => app.HarmonyUserId));
            Assert.Equal("OwnerAbility", Assert.Single(apps[0].HarmonyLaunchEntries).Ability);
            Assert.Equal("WorkAbility", Assert.Single(apps[1].HarmonyLaunchEntries).Ability);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DuplicateCompleteRecordsStaySingleAcrossBothStreams(bool appsOnly)
        {
            string appPayload = "{\"bundleName\":\"com.example.single\",\"userId\":100,\"abilityName\":\"MainAbility\"}";
            string processPayload = "USERID PID ARGS\n100 42 com.example.single";
            var service = new HarmonyLookupService((serial, command, timeout, token) => Task.FromResult(
                command[0] == "bm" ? new ProcessResult(1, appPayload, appPayload)
                : command[0] == "ps" ? new ProcessResult(1, processPayload, processPayload) : Denied));
            var inventory = appsOnly ? null : await service.ListTargetsAsync("HDC-1", CancellationToken.None);
            var apps = appsOnly ? await service.ListAppsAsync("HDC-1", CancellationToken.None) : inventory.Apps;
            var app = Assert.Single(apps);
            Assert.Equal(42, app.ProcessPid);
            Assert.Equal("MainAbility", Assert.Single(app.HarmonyLaunchEntries).Ability);
            if (inventory != null) Assert.Single(inventory.Processes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailureMarkerRejectsBothStreamsOfTheFailedCommand(bool failInStdout)
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                string payload = command[0] == "bm" ? "bundleName: com.example.invalid\n"
                    : command[0] == "ps" ? "PID ARGS\n42 invalid-process\n" : "";
                return Task.FromResult(payload.Length > 0 ? new ProcessResult(0,
                    failInStdout ? "[Fail] disconnected" : payload,
                    failInStdout ? payload : "[Fail] disconnected") : Denied);
            });
            await Assert.ThrowsAsync<System.IO.IOException>(() => service.ListTargetsAsync("HDC-1", CancellationToken.None));
            await Assert.ThrowsAsync<System.IO.IOException>(() => service.ListAppsAsync("HDC-1", CancellationToken.None));
            await Assert.ThrowsAsync<System.IO.IOException>(() => service.ListProcessesAsync("HDC-1", CancellationToken.None));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task UserCancellationDoesNotReturnPartialInventory(bool cancelBundleQuery)
        {
            using var cancellation = new CancellationTokenSource();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == (cancelBundleQuery ? "bm" : "ps"))
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(token);
                }
                return Task.FromResult(Denied);
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ListTargetsAsync("HDC-1", cancellation.Token));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" \n\t")]
        [InlineData("USERID PID ARGS\n")]
        public async Task SuccessfulEmptyProcessCommandDoesNotHideInventoryFailure(string processOutput)
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "acm")
                    return Task.FromResult(new ProcessResult(0, "ID: 100", ""));
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, processOutput, ""));
                return Task.FromResult(Denied);
            });

            var error = await Assert.ThrowsAsync<System.IO.IOException>(() =>
                service.ListProcessesAsync("HDC-1", CancellationToken.None));
            Assert.Contains("未返回可解析的进程记录", error.Message);
        }

        [Fact]
        public async Task ProcInventoryComplementsPartiallySuccessfulPsView()
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "PID ARGS\n42 com.example.visible\n", ""));
                if (command.Length >= 3
                    && command[0] == "sh"
                    && command[2].IndexOf("__MOTUPERF_PROC_LIST__", StringComparison.Ordinal) >= 0)
                {
                    return Task.FromResult(new ProcessResult(0,
                        "__MOTUPERF_PROC_LIST__\nPID=43 UID=0 CMDLINE=/system/bin/com.example.hidden\n", ""));
                }
                return Task.FromResult(Denied);
            });

            var processes = await service.ListProcessesAsync("HDC-1", CancellationToken.None);

            Assert.Equal(new[] { 42, 43 }, processes.OrderBy(process => process.Pid).Select(process => process.Pid));
            Assert.Equal("com.example.visible", processes.Single(process => process.Pid == 42).Name);
            var procOnly = processes.Single(process => process.Pid == 43);
            Assert.Equal("com.example.hidden", procOnly.Name);
            Assert.Empty(procOnly.BundleId);
            Assert.Equal("proc-inventory", procOnly.OwnershipSource);
        }

        [Theory]
        [InlineData("")]
        [InlineData("PID ARGS\n")]
        [InlineData("userid pid name\n")]
        public async Task LaterEmptyProcessViewsDoNotEraseEarlierDiagnostic(string emptyOutput)
        {
            int processCommands = 0;
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                {
                    processCommands++;
                    return Task.FromResult(processCommands == 1
                        ? new ProcessResult(1, "", "permission denied")
                        : new ProcessResult(0, emptyOutput, ""));
                }
                return Task.FromResult(Denied);
            });

            var error = await Assert.ThrowsAsync<System.IO.IOException>(() =>
                service.ListProcessesAsync("HDC-1", CancellationToken.None));
            Assert.Contains("permission denied", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EmptyProcessViewsPreserveInstalledAppsAndReportWhetherAbilityRecovered(bool recover)
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "acm") return Task.FromResult(new ProcessResult(0, "ID: 100", ""));
                if (command[0] == "bm") return Task.FromResult(new ProcessResult(0,
                    "{\"bundleName\":\"com.example.game\",\"userId\":100}", ""));
                if (command[0] == "ps") return Task.FromResult(new ProcessResult(0, "PID ARGS\n", ""));
                if (recover && command[0] == "aa") return Task.FromResult(new ProcessResult(0,
                    "{\"pid\":501,\"bundleName\":\"com.example.game\",\"userId\":100}", ""));
                return Task.FromResult(Denied);
            });

            var inventory = await service.ListTargetsAsync("HDC-1", CancellationToken.None);
            var app = Assert.Single(inventory.Apps);
            Assert.Equal("com.example.game", app.BundleId);
            Assert.Equal(100, app.HarmonyUserId);
            Assert.Equal(recover, app.IsRunning);
            if (recover)
            {
                Assert.Equal(501, Assert.Single(inventory.Processes).Pid);
                Assert.Empty(inventory.ProcessInventoryError);
            }
            else
            {
                Assert.Empty(inventory.Processes);
                Assert.Contains("未返回可解析的进程记录", inventory.ProcessInventoryError);
            }
        }

        [Fact]
        public void ProcessNameConflictsAcrossStreamsRemainMissing()
        {
            var names = HarmonyLookupService.ParseProcessNamesFromIndependentStreams(
                "PID=42 CMDLINE=/system/bin/first\n",
                "PID=42 CMDLINE=/system/bin/second\n");

            Assert.Empty(names);
        }

        [Fact]
        public void CompleteProcessNamesOnEitherStreamAreMergedOnce()
        {
            var names = HarmonyLookupService.ParseProcessNamesFromIndependentStreams(
                "PID=42 CMDLINE=/system/bin/foundation\n",
                "PID=43 CMDLINE=/system/bin/render\nPID=42 CMDLINE=/system/bin/foundation\n");

            Assert.Equal("foundation", names[42]);
            Assert.Equal("render", names[43]);
            Assert.Equal(2, names.Count);
        }

        [Fact]
        public void LaunchDetailsDoNotPairFieldsAcrossStreams()
        {
            string stdout = "bundleName: com.example.selected\nmoduleName: entry\n";
            string stderr = "abilityInfos:\n  - name: SelectedAbility\n";

            var independent = Assert.Single(HarmonyLookupService.ParseLaunchEntryPointsFromIndependentStreams(
                stdout, stderr, "com.example.selected", 100));
            Assert.Equal("", independent.Module);
            Assert.Equal("SelectedAbility", Assert.Single(
                HarmonyLookupService.ParseLaunchEntryPoints(
                    stdout + "\n" + stderr, "com.example.selected", 100)).Ability);
            Assert.Equal("entry", Assert.Single(
                HarmonyLookupService.ParseLaunchEntryPoints(
                    stdout + "\n" + stderr, "com.example.selected", 100)).Module);
        }

        [Fact]
        public void PackageRecordsOnEitherStreamAreMergedWithoutDiagnosticText()
        {
            var packages = HarmonyLookupService.ParseAndroidPackagesFromIndependentStreams(
                "package:/system/app/com.example.system/base.apk\n",
                "permission denied while reading com.example.denied\npackage:com.example.stderr\n");

            Assert.Equal(new[] { "com.example.system", "com.example.stderr" }, packages);
        }

        [Fact]
        public void ExplicitHarmonyPackageNamesWithoutDotsRemainSelectable()
        {
            var packages = HarmonyLookupService.ParseAndroidPackages(
                "package:launcher\n"
                + "package:/system/app/system_server/base.apk\n"
                + "package:systemui uid:1000\n");

            Assert.Equal(new[] { "launcher", "system_server", "systemui" }, packages);
        }

        [Theory]
        [InlineData("package:/system/app/launcher/base.hap", "launcher")]
        [InlineData("package:/system/app/systemui/entry.hsp", "systemui")]
        [InlineData("pkg:/data/app/com.example.game/module.hap", "com.example.game")]
        public void HarmonyPackageArchivesRecoverSingleSegmentAndDottedBundles(string output, string expected)
        {
            Assert.Equal(new[] { expected }, HarmonyLookupService.ParseAndroidPackages(output));
        }

        [Fact]
        public void ProcessStartTimesOnEitherStreamAreMergedOnlyWhenConsistent()
        {
            string first = "42 (target) " + string.Join(" ", Enumerable.Repeat("0", 19)) + " 100\n";
            string second = "43 (worker) " + string.Join(" ", Enumerable.Repeat("0", 19)) + " 200\n";

            var values = HarmonyLookupService.ParseProcessStartTimeTicksFromIndependentStreams(first, second);

            Assert.Equal(100, values[42]);
            Assert.Equal(200, values[43]);
        }

        [Fact]
        public void ConflictingProcessStartTimesAcrossStreamsRemainMissing()
        {
            string stdout = "42 (target) " + string.Join(" ", Enumerable.Repeat("0", 19)) + " 100\n";
            string stderr = "42 (target) " + string.Join(" ", Enumerable.Repeat("0", 19)) + " 101\n";

            Assert.Empty(HarmonyLookupService.ParseProcessStartTimeTicksFromIndependentStreams(stdout, stderr));
        }

        [Fact]
        public async Task ProcessInventoryDoesNotBindConflictingStreamStartTime()
        {
            string stdout = "42 (target) " + string.Join(" ", Enumerable.Repeat("0", 19)) + " 100\n";
            string stderr = "42 (target) " + string.Join(" ", Enumerable.Repeat("0", 19)) + " 101\n";
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command.Length > 0 && command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "PID ARGS\n42 com.example.target\n", ""));
                if (command.Length > 0 && command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0, stdout, stderr));
                return Task.FromResult(Denied);
            });

            var process = Assert.Single(await service.ListProcessesAsync("HDC-1", CancellationToken.None));

            Assert.Equal(0, process.HarmonyStartTimeTicks);
        }
    }
}
