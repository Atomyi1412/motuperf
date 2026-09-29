using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyMixedInventoryEndToEndTests
    {
        [Fact]
        public async Task MixedHdcSnapshotProjectsAllTargetKindsWithoutCrossUserBinding()
        {
            var launchCommands = new List<string>();

            Task<ProcessResult> Execute(string serial, string[] command, int timeout, CancellationToken token)
            {
                string key = string.Join(" ", command);
                if (key == "acm dump -a")
                    return Task.FromResult(new ProcessResult(0, "ID: 0\nID: 100\n", ""));

                if (command[0] == "bm")
                {
                    if (key == "bm dump -a")
                    {
                        return Task.FromResult(new ProcessResult(0,
                            "{\"applications\":["
                                + "{\"bundleName\":\"com.native.app\",\"userId\":0,\"moduleName\":\"entry\",\"abilityName\":\"MainAbility\"},"
                                + "{\"bundleName\":\"com.stopped.app\",\"userId\":0}]}\n",
                            "{\"bundleName\":\"com.service.app\",\"userId\":0,"
                                + "\"serviceAbilityInfos\":[{\"name\":\"SyncService\"}]}\n"));
                    }
                    if (key == "bm dump -a -u 0")
                    {
                        return Task.FromResult(new ProcessResult(0,
                            "{\"bundleName\":\"launcher\",\"userId\":0}\n", ""));
                    }
                    if (key == "bm dump -a -u 100")
                    {
                        return Task.FromResult(new ProcessResult(0,
                            "{\"bundleName\":\"com.shared.app\",\"userId\":100,"
                                + "\"moduleName\":\"work\",\"abilityName\":\"WorkAbility\"}\n", ""));
                    }
                    return Task.FromResult(new ProcessResult(1, "", "unsupported bm scope"));
                }

                if (command[0] == "pm" || (command[0] == "cmd" && command.Length > 1 && command[1] == "package"))
                {
                    int userId = HarmonyLookupService.CommandUserId(command);
                    if (userId >= 0 && userId != 100)
                        return Task.FromResult(new ProcessResult(0, "", ""));
                    return Task.FromResult(new ProcessResult(0,
                        "package:/system/app/launcher/base.hap\npackage:com.compat.game\n",
                        userId < 0 ? "package:com.compat.stderr\n" : ""));
                }

                if (command[0] == "ps")
                {
                    return Task.FromResult(new ProcessResult(0,
                        "USERID PID BUNDLE_NAME NAME\n"
                            + "0 401 com.native.app native_process\n"
                            + "100 402 com.shared.app shared_process\n"
                            + "0 403 com.service.app service_process\n"
                            + "100 404 com.compat.game compat_process\n",
                        ""));
                }

                if (command[0] == "sh" && command.Length >= 3
                    && command[2].IndexOf("__MOTUPERF_PROC_LIST__", StringComparison.Ordinal) >= 0)
                {
                    return Task.FromResult(new ProcessResult(0,
                        "__MOTUPERF_PROC_LIST__\nPID=405 UID=0 CMDLINE=/system/bin/system_worker\n",
                        ""));
                }

                if (command[0] == "aa" && key == "aa dump -a")
                {
                    return Task.FromResult(new ProcessResult(0,
                        "{\"applicationRecords\":["
                            + "{\"pid\":401,\"bundleName\":\"com.native.app\",\"userId\":0,\"state\":\"foreground\"},"
                            + "{\"pid\":402,\"bundleName\":\"com.shared.app\",\"userId\":100,\"applicationState\":\"background\"},"
                            + "{\"pid\":403,\"bundleName\":\"com.service.app\",\"userId\":0,\"state\":\"background\"}]}\n",
                        ""));
                }

                if (command[0] == "aa" && command.Length > 1 && command[1] == "start")
                {
                    launchCommands.Add(key);
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                }

                return Task.FromResult(new ProcessResult(1, "", "unsupported test HDC command"));
            }

            HarmonyTargetInventory inventory = await new HarmonyLookupService(Execute)
                .ListTargetsAsync("hdc-mixed", CancellationToken.None);

            Assert.Equal(new[] { 401, 402, 403, 404, 405 },
                inventory.Processes.OrderBy(process => process.Pid).Select(process => process.Pid));

            AppInfo native = Assert.Single(inventory.Apps,
                app => app.BundleId == "com.native.app" && app.HarmonyUserId == 0);
            Assert.Equal(401, native.ProcessPid);
            Assert.True(native.IsRunning);
            Assert.True(native.HasLaunchEntry);
            Assert.True(native.CanAttemptLaunch);

            AppInfo shared = Assert.Single(inventory.Apps,
                app => app.BundleId == "com.shared.app" && app.HarmonyUserId == 100);
            Assert.Equal(402, shared.ProcessPid);
            Assert.Equal("WorkAbility", Assert.Single(shared.HarmonyLaunchEntries).Ability);
            Assert.DoesNotContain(inventory.Apps,
                app => app.BundleId == "com.shared.app" && app.HarmonyUserId == 0);

            AppInfo service = Assert.Single(inventory.Apps,
                app => app.BundleId == "com.service.app" && app.HarmonyUserId == 0);
            Assert.Equal(403, service.ProcessPid);
            Assert.False(service.HasLaunchEntry);
            Assert.True(service.HasNonUiLaunchEntry);
            Assert.True(service.CanAttemptLaunch);

            AppInfo compat = Assert.Single(inventory.Apps,
                app => app.BundleId == "com.compat.game" && app.HarmonyUserId == 100);
            Assert.Equal(404, compat.ProcessPid);
            Assert.True(compat.IsRunning);

            AppInfo stopped = Assert.Single(inventory.Apps,
                app => app.BundleId == "com.stopped.app" && app.HarmonyUserId == 0);
            Assert.False(stopped.IsRunning);
            Assert.Equal(0, stopped.ProcessPid);

            AppInfo system = Assert.Single(inventory.Apps,
                app => app.BundleId == "launcher" && app.HarmonyUserId == 0);
            Assert.False(system.IsRunning);
            Assert.True(system.CanAttemptLaunch);

            AppInfo processOnly = Assert.Single(inventory.Apps,
                app => string.IsNullOrWhiteSpace(app.BundleId) && app.ProcessPid == 405);
            Assert.True(processOnly.IsProcessOnly);
            Assert.False(processOnly.CanAttemptLaunch);
            Assert.Equal("system_worker", processOnly.ProcessName);

            Assert.Equal(0, Assert.Single(inventory.Processes, process => process.Pid == 401).HarmonyUserId);
            Assert.Equal(100, Assert.Single(inventory.Processes, process => process.Pid == 402).HarmonyUserId);
            Assert.Equal(100, Assert.Single(inventory.Processes, process => process.Pid == 404).HarmonyUserId);
            Assert.Empty(inventory.ProcessInventoryError);
            Assert.Empty(inventory.UserInventoryError);

            foreach (AppInfo app in new[] { native, shared, service, stopped, system })
            {
                ProcessResult launch = await new HarmonyLookupService(Execute)
                    .LaunchAppAsync("hdc-mixed", app, CancellationToken.None);
                Assert.Equal(0, launch.ExitCode);
            }
            Assert.Contains(launchCommands, command => command.Contains("-u 0", StringComparison.Ordinal)
                && command.Contains("com.native.app", StringComparison.Ordinal));
            Assert.Contains(launchCommands, command => command.Contains("-u 100", StringComparison.Ordinal)
                && command.Contains("com.shared.app", StringComparison.Ordinal));
            Assert.DoesNotContain(launchCommands, command => command.Contains("system_worker", StringComparison.Ordinal));
        }

        [Fact]
        public async Task SameBundleGlobalAndScopedRecordsKeepPerUserEntriesAndProcesses()
        {
            Task<ProcessResult> Execute(string serial, string[] command, int timeout, CancellationToken token)
            {
                string key = string.Join(" ", command);
                if (command[0] == "acm")
                    return Task.FromResult(new ProcessResult(0, "ID: 0\nID: 100\n", ""));

                if (command[0] == "bm")
                {
                    if (key == "bm dump -a")
                        return Task.FromResult(new ProcessResult(0,
                            "{\"bundleName\":\"com.example.shared\"}\n", ""));
                    if (key == "bm dump -a -u 0")
                        return Task.FromResult(new ProcessResult(0,
                            "{\"bundleName\":\"com.example.shared\",\"userId\":0,"
                            + "\"moduleName\":\"entry\",\"abilityName\":\"OwnerAbility\"}\n", ""));
                    if (key == "bm dump -a -u 100")
                        return Task.FromResult(new ProcessResult(0,
                            "{\"bundleName\":\"com.example.shared\",\"userId\":100,"
                            + "\"moduleName\":\"work\",\"serviceAbilityInfos\":["
                            + "{\"name\":\"WorkService\"}]}\n", ""));
                    return Task.FromResult(new ProcessResult(1, "", "unsupported bm scope"));
                }

                if (command[0] == "pm" || (command[0] == "cmd" && command.Length > 1 && command[1] == "package"))
                    return Task.FromResult(new ProcessResult(0, "", ""));

                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0,
                        "USERID PID BUNDLE_NAME NAME\n"
                        + "0 501 com.example.shared shared_owner\n"
                        + "100 502 com.example.shared shared_work\n", ""));

                if (command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0,
                        "501 (shared_owner) " + string.Join(" ", Enumerable.Repeat("0", 18)) + " 100\n"
                        + "502 (shared_work) " + string.Join(" ", Enumerable.Repeat("0", 18)) + " 200\n", ""));

                if (command[0] == "aa" && command.Length > 1 && command[1] == "dump")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"applicationRecords\":["
                        + "{\"pid\":501,\"bundleName\":\"com.example.shared\",\"userId\":0,\"state\":\"foreground\"},"
                        + "{\"pid\":502,\"bundleName\":\"com.example.shared\",\"userId\":100,\"state\":\"background\"}]}", ""));

                return Task.FromResult(new ProcessResult(1, "", "unsupported test HDC command"));
            }

            HarmonyTargetInventory inventory = await new HarmonyLookupService(Execute)
                .ListTargetsAsync("hdc-shared", CancellationToken.None);

            Assert.Equal(new[] { 0, 100 }, inventory.Apps
                .Where(app => app.BundleId == "com.example.shared")
                .Select(app => app.HarmonyUserId)
                .OrderBy(userId => userId));
            AppInfo owner = Assert.Single(inventory.Apps,
                app => app.BundleId == "com.example.shared" && app.HarmonyUserId == 0);
            AppInfo work = Assert.Single(inventory.Apps,
                app => app.BundleId == "com.example.shared" && app.HarmonyUserId == 100);
            Assert.Equal(501, owner.ProcessPid);
            Assert.Equal("OwnerAbility", Assert.Single(owner.HarmonyLaunchEntries).Ability);
            Assert.True(owner.HasLaunchEntry);
            Assert.Equal(502, work.ProcessPid);
            Assert.Equal("WorkService", Assert.Single(work.HarmonyLaunchEntries).Ability);
            Assert.False(work.HasLaunchEntry);
            Assert.True(work.HasNonUiLaunchEntry);
            Assert.DoesNotContain(owner.HarmonyLaunchEntries, entry => entry.Ability == "WorkService");
            Assert.DoesNotContain(work.HarmonyLaunchEntries, entry => entry.Ability == "OwnerAbility");
            Assert.Equal(0, Assert.Single(inventory.Processes, process => process.Pid == 501).HarmonyUserId);
            Assert.Equal(100, Assert.Single(inventory.Processes, process => process.Pid == 502).HarmonyUserId);
        }
    }
}
