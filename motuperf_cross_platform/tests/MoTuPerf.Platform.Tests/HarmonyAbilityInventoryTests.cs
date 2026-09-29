using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyAbilityInventoryTests
    {
        private const string OfficialWorker = "AppRunningRecords:\n  AppRunningRecord ID #0\n"
            + "    process name [com.example.game:renderer]\n    pid #502 uid #20010123\n    state #FOREGROUND\n";

        private static HarmonyLookupService InventoryService(
            ProcessResult ps, string abilityOutput, List<string[]> commands = null)
        {
            return new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands?.Add(command);
                if (command[0] == "ps") return Task.FromResult(ps);
                if (command[0] == "aa") return Task.FromResult(new ProcessResult(0, abilityOutput, ""));
                if (command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0,
                        "502 (com.example.game:renderer) S " + string.Join(" ", Enumerable.Repeat("0", 18)) + " 12345 0 0\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });
        }

        [Theory]
        [InlineData(0, "")]
        [InlineData(1, "permission denied")]
        public async Task IndependentAbilityRecordsRecoverTargetsWhenPsCannotListThem(int exit, string error)
        {
            var commands = new List<string[]>();
            var service = InventoryService(new ProcessResult(exit, "", error), OfficialWorker, commands);
            var inventory = await service.ListTargetsAsync("TEST-HDC", CancellationToken.None);
            var process = Assert.Single(inventory.Processes);
            Assert.Equal(502, process.Pid);
            Assert.Equal("com.example.game:renderer", process.Name);
            Assert.Equal("TEST-HDC", process.DeviceUdid);
            Assert.Equal("harmony", process.Platform);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Equal(12345L, process.HarmonyStartTimeTicks);
            Assert.True(process.Recommended);
            Assert.False(process.OwnershipVerified);
            Assert.True(string.IsNullOrEmpty(process.BundleId));
            Assert.True(string.IsNullOrEmpty(inventory.ProcessInventoryError));
            var app = Assert.Single(inventory.Apps);
            Assert.Equal(502, app.ProcessPid);
            Assert.True(app.IsProcessOnly);
            Assert.False(app.CanAttemptLaunch);
            Assert.True(ProcessTargetMatcher.BelongsToDevice(process, "TEST-HDC"));
            Assert.False(ProcessTargetMatcher.SameHarmonyProcessInstance(new ProcessInfo
            {
                Pid = 502, Name = process.Name, HarmonyUserId = 100, HarmonyStartTimeTicks = 12346
            }, process));
            // The inventory now performs an additional read-only /proc
            // fallback before the ability binding path. The contract is that
            // a shell query was issued, not that there is exactly one.
            Assert.Contains(commands, command => command[0] == "sh");
        }

        [Fact]
        public async Task BundlePidBindingUsesProcCmdlineToBecomeSelectable()
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (command[0] == "aa")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.game\",\"pid\":502,\"userId\":100}\n", ""));
                if (command[0] == "sh" && command.Length > 2
                    && command[2].Contains("/proc/$p/cmdline", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "PID=502 CMDLINE=/system/bin/com.example.game:renderer --ability Entry\n", ""));
                if (command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0,
                        "502 (com.example.game:renderer) S " + string.Join(" ", Enumerable.Repeat("0", 18)) + " 12345 0 0\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fixture command"));
            });

            var process = Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));

            Assert.Equal(502, process.Pid);
            Assert.Equal("com.example.game:renderer", process.Name);
            Assert.Equal("com.example.game", process.BundleId);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Equal(12345L, process.HarmonyStartTimeTicks);
            Assert.Contains(commands, command => command.Length > 2
                && command[0] == "sh"
                && command[2].Contains("/proc/$p/cmdline", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(false, "FOREGROUND")]
        [InlineData(true, "FOREGROUND")]
        [InlineData(true, "BACKGROUND")]
        public async Task PidOnlyAbilityBindingUsesProcCmdlineAsProcessOnlyTarget(bool unified, string state)
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (command[0] == "aa")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"appRunningRecords\":[{\"pid\":502,\"userId\":100,\"state\":\"" + state + "\"}]}\n", ""));
                if (command[0] == "sh" && command.Length > 2
                    && command[2].Contains("/proc/$p/cmdline", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "PID=502 CMDLINE=/system/bin/com.example.game:renderer --ability Entry\n", ""));
                if (command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0,
                        "502 (com.example.game:renderer) S " + string.Join(" ", Enumerable.Repeat("0", 18)) + " 12345 0 0\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fixture command"));
            });

            var inventory = unified ? await service.ListTargetsAsync("TEST-HDC", CancellationToken.None) : null;
            List<ProcessInfo> processes = inventory?.Processes
                ?? await service.ListProcessesAsync("TEST-HDC", CancellationToken.None);

            ProcessInfo process = Assert.Single(processes);
            Assert.Equal(502, process.Pid);
            Assert.Equal("com.example.game:renderer", process.Name);
            Assert.Empty(process.BundleId);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Equal(state == "FOREGROUND", process.ForegroundApplication);
            Assert.Equal(state == "FOREGROUND", process.Recommended);
            Assert.Equal(12345L, process.HarmonyStartTimeTicks);
            Assert.False(process.OwnershipVerified);
            if (inventory != null)
            {
                var app = Assert.Single(inventory.Apps);
                Assert.Equal(502, app.ProcessPid);
                Assert.Empty(app.BundleId);
                Assert.True(app.IsProcessOnly);
                Assert.False(app.CanAttemptLaunch);
            }
        }

        [Theory]
        [InlineData("FOREGROUND", true)]
        [InlineData("BACKGROUND", false)]
        public void DirectTextRunningRecordAllowsPidOnlyEvidence(string state, bool foreground)
        {
            var binding = Assert.Single(HarmonyLookupService.ParseAbilityProcessBindings(
                "AppRunningRecords:\n"
                + "  AppRunningRecord ID #0\n"
                + "    pid #502\n"
                + "    uid #20010123\n"
                + "    state #" + state + "\n"));

            Assert.Equal(502, binding.Pid);
            Assert.Empty(binding.BundleId);
            Assert.Empty(binding.ProcessName);
            Assert.Equal(100, binding.HarmonyUserId);
            Assert.Equal(foreground, binding.Foreground);
            Assert.True(binding.PidEvidenceVerified);
        }

        [Fact]
        public void TextApplicationStateAndCamelApplicationRecordKeepRunningRecordsDistinct()
        {
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "ApplicationRecord ID #0\n"
                + "  pid #501\n"
                + "  process name [com.example.foreground]\n"
                + "  applicationState #APP_STATE_FOREGROUND\n"
                + "ApplicationRecord ID #1\n"
                + "  pid #502\n"
                + "  process name [com.example.background]\n"
                + "  applicationState #APP_STATE_BACKGROUND\n");

            Assert.Equal(new[] { 501, 502 }, bindings.Select(binding => binding.Pid).OrderBy(pid => pid));
            Assert.True(bindings.Single(binding => binding.Pid == 501).Foreground);
            Assert.False(bindings.Single(binding => binding.Pid == 502).Foreground);
        }

        [Fact]
        public void TextApplicationRecordAllowsPidOnlyEvidenceWhenStateIsRunning()
        {
            var binding = Assert.Single(HarmonyLookupService.ParseAbilityProcessBindings(
                "ApplicationRecord ID #0\n"
                + "  pid #502\n"
                + "  uid #20010123\n"
                + "  appState #APP_STATE_BACKGROUND\n"));

            Assert.Equal(502, binding.Pid);
            Assert.Empty(binding.BundleId);
            Assert.Empty(binding.ProcessName);
            Assert.Equal(100, binding.HarmonyUserId);
            Assert.False(binding.Foreground);
            Assert.True(binding.PidEvidenceVerified);
        }

        [Theory]
        [InlineData("abilityState", "FOREGROUND", true)]
        [InlineData("abilityState", "BACKGROUND", false)]
        [InlineData("applicationState", "FOREGROUND", true)]
        [InlineData("isForeground", "false", false)]
        [InlineData("foreground", "true", true)]
        public void JsonRunningRecordAcceptsAllExplicitStateFields(string field, string value, bool foreground)
        {
            string jsonValue = field.IndexOf("Foreground", StringComparison.OrdinalIgnoreCase) >= 0
                || field.Equals("foreground", StringComparison.OrdinalIgnoreCase)
                ? value.ToLowerInvariant()
                : "\"" + value + "\"";
            var binding = Assert.Single(HarmonyLookupService.ParseAbilityProcessBindings(
                "{\"appRunningRecords\":[{\"pid\":502,\"userId\":100,\"" + field
                + "\":" + jsonValue + "}]}"));

            Assert.Equal(502, binding.Pid);
            Assert.Empty(binding.BundleId);
            Assert.Empty(binding.ProcessName);
            Assert.Equal(foreground, binding.Foreground);
            Assert.True(binding.PidEvidenceVerified);
        }

        [Fact]
        public void JsonApplicationRecordContainersRetainPidOnlyRunningEvidence()
        {
            var binding = Assert.Single(HarmonyLookupService.ParseAbilityProcessBindings(
                "{\"applicationRecords\":[{\"pid\":502,\"userId\":100,\"appState\":\"APP_STATE_FOREGROUND\"}]}"));

            Assert.Equal(502, binding.Pid);
            Assert.Empty(binding.BundleId);
            Assert.Empty(binding.ProcessName);
            Assert.Equal(100, binding.HarmonyUserId);
            Assert.True(binding.Foreground);
            Assert.True(binding.PidEvidenceVerified);
        }

        [Fact]
        public async Task PidOnlyAbilityBindingWithoutReadableNameRemainsMissing()
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (command[0] == "aa")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"appRunningRecords\":[{\"pid\":502,\"userId\":100,\"state\":\"BACKGROUND\"}]}\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });

            var error = await Assert.ThrowsAsync<IOException>(() =>
                service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
            Assert.Contains("无法读取鸿蒙进程", error.Message);
        }

        [Theory]
        [InlineData("{\"pid\":502,\"userId\":100,\"state\":\"FOREGROUND\"}")]
        [InlineData("{\"appRunningRecords\":[{\"pid\":502,\"userId\":100}]}")]
        [InlineData("{\"appRunningRecords\":[{\"pid\":502,\"state\":\"TERMINATED\",\"isForeground\":true}]}")]
        [InlineData("{\"appRunningRecords\":[{\"pid\":502,\"state\":2}]}")]
        [InlineData("{\"appRunningRecords\":[{\"details\":{\"pid\":502,\"state\":\"FOREGROUND\"}}]}")]
        [InlineData("{\"appRunningRecords\":[{\"rootCaller\":{\"pid\":502,\"state\":\"FOREGROUND\"}}]}")]
        [InlineData("{\"appRunningRecords\":[{\"metadata\":{\"appRunningRecords\":[{\"pid\":502,\"state\":\"FOREGROUND\"}]}}]}")]
        [InlineData("{\"appRunningRecords\":[{\"pid\":0,\"state\":\"FOREGROUND\"}]}")]
        [InlineData("{\"appRunningRecords\":[{\"pid\":502.5,\"state\":\"FOREGROUND\"}]}")]
        [InlineData("AppRunningRecords:\n pid #502 uid #20010123\n state #FOREGROUND\n")]
        public void PidOnlyAbilityEvidenceRejectsUnknownContextsAndStates(string output)
        {
            Assert.Empty(HarmonyLookupService.ParseAbilityProcessBindings(output));
        }

        [Fact]
        public async Task ProcCmdlinePermissionFailureKeepsVerifiedBundlePidSelectable()
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (command[0] == "aa")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"bundleName\":\"com.example.game\",\"pid\":502,\"userId\":100}\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });

            var process = Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
            Assert.Empty(process.Name);
            Assert.Equal("com.example.game", process.BundleId);
            Assert.Equal("com.example.game", process.OwnerBundleId);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.True(process.OwnershipVerified);
            Assert.True(ProcessTargetMatcher.IsValidTarget(process));
        }

        [Fact]
        public async Task PartialPsInventoryIsUnionedWithIndependentAbilityProcesses()
        {
            var service = InventoryService(new ProcessResult(0, "PID NAME\n501 foundation\n", ""), OfficialWorker);
            var rows = await service.ListProcessesAsync("TEST-HDC", CancellationToken.None);
            Assert.Equal(new[] { 501, 502 }, rows.Select(row => row.Pid).OrderBy(pid => pid));
            Assert.Equal("foundation", rows.Single(row => row.Pid == 501).Name);
            Assert.Equal(12345L, rows.Single(row => row.Pid == 502).HarmonyStartTimeTicks);
        }

        [Fact]
        public async Task ConflictingExecutableNamesDoNotBecomeSelectableTargets()
        {
            var service = InventoryService(new ProcessResult(0, "", ""),
                "[{\"pid\":502,\"processName\":\"old\"},{\"pid\":502,\"processName\":\"new\"}]");
            Assert.Empty(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
        }

        [Fact]
        public async Task BundlePidWithoutUserKeepsBundleEvidenceButDoesNotInventUser()
        {
            var service = InventoryService(new ProcessResult(0, "", ""),
                "{\"pid\":502,\"bundleName\":\"com.example.game\"}");

            var process = Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
            Assert.Empty(process.Name);
            Assert.Equal("com.example.game", process.BundleId);
            Assert.Equal(-1, process.HarmonyUserId);
            Assert.True(process.OwnershipVerified);
            Assert.True(ProcessTargetMatcher.IsValidTarget(process));
        }

        [Fact]
        public async Task ScopedAbilityInventorySuppliesUserWhenPayloadOmitsUser()
        {
            var commands = new List<string>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (command.Length > 0 && command[0] == "acm")
                    return Task.FromResult(new ProcessResult(0, "ID: 100", ""));
                if (command.Length > 0 && command[0] == "ps")
                    return Task.FromResult(new ProcessResult(1, "", "permission denied"));
                if (key == "aa dump -a -u 100")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"pid\":502,\"bundleName\":\"com.example.game\"}\n", ""));
                if (command.Length > 2 && command[0] == "sh"
                    && command[2].Contains("/proc/$p/cmdline", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "PID=502 CMDLINE=/system/bin/com.example.game:renderer --ability Entry\n", ""));
                if (command.Length > 0 && command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0,
                        "502 (com.example.game:renderer) S " + string.Join(" ", Enumerable.Repeat("0", 18)) + " 12345 0 0\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fixture command"));
            });

            var process = Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));

            Assert.Equal("com.example.game", process.BundleId);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.True(process.OwnershipVerified);
            Assert.Equal(12345L, process.HarmonyStartTimeTicks);
            Assert.Contains("aa dump -a -u 100", commands);
        }

        [Fact]
        public async Task ExplicitAbilityUserOverridesScopedQueryUser()
        {
            var commands = new List<string>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                string key = string.Join(" ", command ?? Array.Empty<string>());
                commands.Add(key);
                if (command.Length > 0 && command[0] == "acm")
                    return Task.FromResult(new ProcessResult(0, "ID: 100", ""));
                if (command.Length > 0 && command[0] == "ps")
                    return Task.FromResult(new ProcessResult(1, "", "permission denied"));
                if (key == "aa dump -a -u 100")
                    return Task.FromResult(new ProcessResult(0,
                        "{\"pid\":502,\"bundleName\":\"com.example.game\",\"userId\":101}\n", ""));
                if (command.Length > 2 && command[0] == "sh"
                    && command[2].Contains("/proc/$p/cmdline", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "PID=502 CMDLINE=/system/bin/com.example.game:renderer --ability Entry\n", ""));
                if (command.Length > 0 && command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0,
                        "502 (com.example.game:renderer) S " + string.Join(" ", Enumerable.Repeat("0", 18)) + " 12345 0 0\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fixture command"));
            });

            var process = Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));

            Assert.Equal("com.example.game", process.BundleId);
            Assert.Equal(101, process.HarmonyUserId);
            Assert.True(process.OwnershipVerified);
            Assert.Equal(12345L, process.HarmonyStartTimeTicks);
            Assert.Contains("aa dump -a -u 100", commands);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GenericAndConcreteAbilityAliasesKeepTheConcreteTarget(bool unifiedInventory)
        {
            var service = InventoryService(new ProcessResult(0, "", ""),
                "[{\"pid\":502,\"processName\":\"appspawn\"},"
                + "{\"pid\":502,\"processName\":\"/system/bin/com.example.game:renderer\",\"userId\":100}]");

            HarmonyTargetInventory inventory = unifiedInventory
                ? await service.ListTargetsAsync("TEST-HDC", CancellationToken.None) : null;
            var process = Assert.Single(inventory == null
                ? await service.ListProcessesAsync("TEST-HDC", CancellationToken.None) : inventory.Processes);
            Assert.Equal("com.example.game:renderer", process.Name);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.False(process.OwnershipAmbiguous);
            Assert.Empty(process.BundleId);
            Assert.False(process.OwnershipVerified);
            Assert.Equal(12345L, process.HarmonyStartTimeTicks);
            if (inventory != null)
            {
                var app = Assert.Single(inventory.Apps);
                Assert.Equal(process.Name, app.ProcessName);
                Assert.Equal(process.Pid, app.ProcessPid);
                Assert.Empty(app.BundleId);
                Assert.False(app.CanAttemptLaunch);
            }
        }

        [Fact]
        public async Task PsOnlyViewsCompleteOneTargetInBothInventories()
        {
            int psQueries = 0;
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, ++psQueries == 1
                        ? "USERID PID NAME\n100 501 appspawn\n"
                        : "USERID PID NAME\n100 501 com.example.game:renderer\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });
            var inventory = await service.ListTargetsAsync("TEST-HDC", CancellationToken.None);
            var process = Assert.Single(inventory.Processes);
            var app = Assert.Single(inventory.Apps);
            Assert.Equal("com.example.game:renderer", process.Name);
            Assert.Equal(process.Name, app.ProcessName);
            Assert.Equal(501, app.ProcessPid);
            Assert.Equal(100, app.HarmonyUserId);
            Assert.Equal("com.example.game", app.BundleId);
            Assert.False(process.OwnershipAmbiguous);
            Assert.True(app.CanAttemptLaunch);
        }

        [Fact]
        public async Task IndependentAliasNameNeverHidesConflictingOwnership()
        {
            var service = InventoryService(new ProcessResult(1, "", "permission denied"),
                "[{\"pid\":502,\"processName\":\"appspawn\",\"bundleName\":\"com.other\",\"userId\":100},"
                + "{\"pid\":502,\"processName\":\"worker\",\"bundleName\":\"com.example.game\",\"userId\":101}]");
            var inventory = await service.ListTargetsAsync("TEST-HDC", CancellationToken.None);
            var process = Assert.Single(inventory.Processes);
            Assert.Equal("worker", process.Name);
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.Recommended);
            Assert.Empty(process.BundleId);
            Assert.Equal(-1, process.HarmonyUserId);
            var app = Assert.Single(inventory.Apps);
            Assert.Equal(502, app.ProcessPid);
            Assert.False(app.CanAttemptLaunch);
        }

        [Fact]
        public async Task IndependentExplicitOwnershipCanBindOnlyTheMatchingInstalledApp()
        {
            var service = InventoryService(new ProcessResult(1, "", "denied"),
                "{\"pid\":502,\"processName\":\"worker\",\"bundleName\":\"com.example.game\",\"userId\":100}");
            var rows = await service.ListProcessesAsync("TEST-HDC", CancellationToken.None);
            var process = Assert.Single(rows);
            Assert.Equal("worker", process.Name);
            Assert.Equal("com.example.game", process.BundleId);
            Assert.True(process.OwnershipVerified);
            var apps = new List<AppInfo>
            {
                new AppInfo { Platform = "harmony", BundleId = "com.example.game", HarmonyUserId = 100 },
                new AppInfo { Platform = "harmony", BundleId = "com.example.game", HarmonyUserId = 101 }
            };
            HarmonyLookupService.MergeProcessApps(apps, rows);
            Assert.True(apps.Single(app => app.HarmonyUserId == 100).IsRunning);
            Assert.False(apps.Single(app => app.HarmonyUserId == 101).IsRunning);
        }

        [Fact]
        public void UnknownUserProcessDoesNotBindToKnownHarmonyProfile()
        {
            var apps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = 100
                }
            };
            var process = new ProcessInfo
            {
                Pid = 1201,
                Name = "com.example.shared",
                BundleId = "com.example.shared",
                Platform = "harmony",
                HarmonyUserId = -1
            };

            HarmonyLookupService.MergeProcessApps(apps, new[] { process });

            AppInfo selectedProfile = Assert.Single(apps, app => app.HarmonyUserId == 100);
            Assert.False(selectedProfile.IsRunning);
            Assert.Equal(0, selectedProfile.ProcessPid);
            AppInfo unknownProcess = Assert.Single(apps, app => app.ProcessPid == 1201);
            Assert.True(unknownProcess.IsProcessOnly);
            Assert.Equal(-1, unknownProcess.HarmonyUserId);
        }

        [Fact]
        public void ProcessOnlyPidCannotBeReusedAcrossHarmonyUsers()
        {
            var apps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "",
                    Platform = "harmony",
                    ProcessPid = 1201,
                    ProcessName = "worker",
                    HarmonyUserId = 100
                }
            };
            var process = new ProcessInfo
            {
                Pid = 1201,
                Name = "worker",
                BundleId = "",
                Platform = "harmony",
                HarmonyUserId = 101
            };

            HarmonyLookupService.MergeProcessApps(apps, new[] { process });

            AppInfo oldProfile = Assert.Single(apps, app => app.HarmonyUserId == 100);
            Assert.False(oldProfile.IsRunning);
            Assert.Equal(1201, oldProfile.ProcessPid);
            AppInfo currentProfile = Assert.Single(apps, app => app.HarmonyUserId == 101);
            Assert.True(currentProfile.IsRunning);
            Assert.True(currentProfile.IsProcessOnly);
            Assert.Equal(1201, currentProfile.ProcessPid);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void MixedKnownAndUnknownUsersStaySeparateRegardlessOfProcessOrder(
            bool unscopedInventory, bool knownFirst)
        {
            var apps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.shared",
                    Platform = "harmony",
                    HarmonyUserId = unscopedInventory ? -1 : 100
                }
            };
            var known = new ProcessInfo
            {
                Pid = 501,
                Name = "com.example.shared",
                BundleId = "com.example.shared",
                Platform = "harmony",
                HarmonyUserId = 100
            };
            var unknown = new ProcessInfo
            {
                Pid = 502,
                Name = "com.example.shared:worker",
                BundleId = "com.example.shared",
                Platform = "harmony",
                HarmonyUserId = -1
            };
            var anotherUnknown = new ProcessInfo
            {
                Pid = 503,
                Name = "com.example.shared:render",
                BundleId = "com.example.shared",
                Platform = "harmony",
                HarmonyUserId = -1
            };

            HarmonyLookupService.MergeProcessApps(apps, knownFirst
                ? new[] { known, unknown, anotherUnknown }
                : new[] { unknown, anotherUnknown, known });

            Assert.Equal(2, apps.Count);
            var scoped = Assert.Single(apps, app => app.HarmonyUserId == 100);
            var unscoped = Assert.Single(apps, app => app.HarmonyUserId < 0);
            Assert.True(scoped.IsRunning);
            Assert.True(unscoped.IsRunning);
            Assert.Equal(0, scoped.ProcessPid);
            Assert.Equal(0, unscoped.ProcessPid);
            Assert.Empty(unscoped.HarmonyUserIds);
            Assert.Equal(!unscopedInventory, unscoped.IsProcessOnly);
            Assert.Equal(unscopedInventory, scoped.IsProcessOnly);
        }

        [Fact]
        public void AUniqueKnownProfileCanCompleteAnUnscopedInstalledApp()
        {
            var apps = new List<AppInfo>
            {
                new AppInfo { BundleId = "com.example.shared", Name = "Installed app", Platform = "harmony" }
            };
            var process = new ProcessInfo
            {
                Pid = 501, Name = "com.example.shared", BundleId = "com.example.shared",
                Platform = "harmony", HarmonyUserId = 100
            };
            HarmonyLookupService.MergeProcessApps(apps, new[] { process });
            var app = Assert.Single(apps);
            Assert.Equal("Installed app", app.Name);
            Assert.Equal(100, app.HarmonyUserId);
            Assert.Equal(501, app.ProcessPid);
            Assert.False(app.IsProcessOnly);
        }


        [Theory]
        [InlineData("{\"pid\":502,\"processName\":\"worker\",\"bundleName\":\"com.example.a\",\"userId\":100}",
            "{\"pid\":502,\"processName\":\"worker\",\"bundleName\":\"com.example.a\",\"userId\":101}")]
        [InlineData("{\"pid\":502,\"processName\":\"worker\",\"bundleName\":\"com.example.a\"}",
            "{\"pid\":502,\"processName\":\"worker\",\"bundleName\":\"com.example.b\"}")]
        public async Task ConflictingOwnershipKeepsOnlyUniqueRealNameAndPid(string first, string second)
        {
            var service = InventoryService(new ProcessResult(0, "", ""), "[" + first + "," + second + "]");
            var process = Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
            Assert.Equal("worker", process.Name);
            Assert.Equal(-1, process.HarmonyUserId);
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.OwnershipVerified);
            Assert.False(process.Recommended);
            Assert.True(string.IsNullOrEmpty(process.BundleId));
        }

        [Fact]
        public async Task BothSourcesFailingStillReportAnInventoryError()
        {
            var service = InventoryService(new ProcessResult(1, "", "ps permission denied"), "permission denied");
            var error = await Assert.ThrowsAsync<IOException>(() => service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
            Assert.Contains("ps permission denied", error.Message);
        }

        [Fact]
        public async Task RepeatedAbilityViewsOfOneProcessDoNotDuplicateIt()
        {
            var service = InventoryService(new ProcessResult(0, "PID NAME\n502 /system/bin/worker\n", ""),
                "[{\"pid\":502,\"processName\":\"worker\",\"userId\":100},"
                + "{\"pid\":502,\"processName\":\"/system/bin/worker\",\"userId\":100}]");
            var process = Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
            Assert.Equal("worker", process.Name);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.False(process.OwnershipAmbiguous);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task PartialAbilitySuccessSurvivesLaterTimeoutAndUnreadableStartTime(bool stderr)
        {
            int probes = 0;
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "aa")
                {
                    if (++probes > 1) throw new OperationCanceledException(token);
                    return Task.FromResult(new ProcessResult(1, stderr ? "" : OfficialWorker,
                        stderr ? OfficialWorker : "partial result"));
                }
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });
            var process = Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
            Assert.Equal(502, process.Pid);
            Assert.Equal(0L, process.HarmonyStartTimeTicks);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Equal(2, probes);
        }

        [Fact]
        public async Task CallerCanCancelAbilityDiscoveryEvenWithoutPsRows()
        {
            using var cancel = new CancellationTokenSource();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "aa") cancel.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new ProcessResult(0, "", ""));
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListProcessesAsync("TEST-HDC", cancel.Token));
        }

        [Fact]
        public async Task TransportFailureNeverCreatesIndependentProcessesFromStalePayload()
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) => Task.FromResult(
                command[0] == "aa"
                    ? new ProcessResult(0, OfficialWorker, "[Fail] device offline")
                    : new ProcessResult(0, "", "")));
            await Assert.ThrowsAsync<IOException>(() =>
                service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
        }

        [Fact]
        public async Task AppsOnlyApiAlsoKeepsIndependentProcessTargets()
        {
            var service = InventoryService(new ProcessResult(1, "", "denied"), OfficialWorker);
            var app = Assert.Single(await service.ListAppsAsync("TEST-HDC", CancellationToken.None));
            Assert.Equal(502, app.ProcessPid);
            Assert.Equal("com.example.game:renderer", app.ProcessName);
            Assert.True(app.IsRunning);
            Assert.False(app.CanAttemptLaunch);
        }

        [Fact]
        public void ConflictingPsViewsCannotSilentlyChooseAProcessIdentity()
        {
            var merged = HarmonyLookupService.MergeProcesses(new[]
            {
                new ProcessInfo
                {
                    Pid = 501, Name = "com.example.game", BundleId = "com.example.game",
                    HarmonyUserId = 100, Platform = "harmony", DeviceUdid = "TEST-HDC", Recommended = true
                },
                new ProcessInfo
                {
                    Pid = 501, Name = "com.example.other", BundleId = "com.example.other",
                    HarmonyUserId = 101, Platform = "harmony", DeviceUdid = "TEST-HDC"
                }
            });
            var process = Assert.Single(merged);
            Assert.Equal(501, process.Pid);
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.OwnershipVerified);
            Assert.False(process.Recommended);
            // Keep the first concrete profile as provenance, but the
            // ambiguity flag prevents automatic binding or launch.
            Assert.Equal(100, process.HarmonyUserId);
            Assert.True(string.IsNullOrEmpty(process.BundleId));
            Assert.True(string.IsNullOrEmpty(process.OwnerBundleId));
            Assert.Equal("进程归属信息冲突，请按真实 PID 选择", process.Reason);
        }

        [Fact]
        public void SparsePsViewsWithOneIdentityStillMergeFields()
        {
            var merged = HarmonyLookupService.MergeProcesses(new[]
            {
                new ProcessInfo { Pid = 501, Name = "worker", Platform = "harmony", DeviceUdid = "TEST-HDC" },
                new ProcessInfo { Pid = 501, Name = "worker", HarmonyUserId = 100, HarmonyStartTimeTicks = 42,
                    Platform = "harmony", DeviceUdid = "TEST-HDC" }
            });
            var process = Assert.Single(merged);
            Assert.False(process.OwnershipAmbiguous);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Equal(42, process.HarmonyStartTimeTicks);
        }

        [Theory]
        [InlineData("appspawn", false)]
        [InlineData("foundation", true)]
        [InlineData("init", false)]
        [InlineData("system_server", true)]
        public void PsOnlyCompletionKeepsLabelsAndWorksInEitherOrder(string host, bool concreteFirst)
        {
            var generic = Process(host, 100);
            generic.DisplayName = "Friendly game";
            var concrete = Process("worker", 100);
            concrete.HarmonyStartTimeTicks = 44;
            var rows = concreteFirst ? new[] { concrete, generic } : new[] { generic, concrete };
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(rows));
            Assert.Equal("worker", process.Name);
            Assert.Equal(concreteFirst ? "worker" : "Friendly game", process.DisplayName);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Equal(44, process.HarmonyStartTimeTicks);
            Assert.Empty(process.BundleId);
            Assert.False(process.OwnershipAmbiguous);
        }

        [Fact]
        public void LaterPsRowCannotRestoreConflictingOwnershipOrRecommendation()
        {
            ProcessInfo Row(string bundle) => new ProcessInfo
            {
                Pid = 501, Name = "worker", Platform = "harmony", HarmonyUserId = 100,
                BundleId = bundle, OwnerBundleId = bundle, OwnerName = bundle,
                OwnershipVerified = true, Recommended = true, ForegroundApplication = true,
                HarmonyStartTimeTicks = 42
            };
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(new[]
            {
                Row("com.example.first"), Row("com.example.second"), Row("com.example.second")
            }));
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.OwnershipVerified);
            Assert.False(process.Recommended);
            Assert.False(process.ForegroundApplication);
            Assert.Empty(process.BundleId);
            Assert.Empty(process.OwnerBundleId);
            Assert.Equal(42, process.HarmonyStartTimeTicks);
            var apps = new List<AppInfo>();
            HarmonyLookupService.MergeProcessApps(apps, new[] { process });
            Assert.False(Assert.Single(apps).CanAttemptLaunch);
        }

        [Fact]
        public void AlreadyAmbiguousPsRowStaysManualWhenItIsTheOnlyView()
        {
            var row = Process("appspawn", 100);
            row.OwnershipAmbiguous = true;
            row.BundleId = "com.example.game";
            row.Recommended = true;
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(new[] { row }));
            Assert.Empty(process.BundleId);
            Assert.False(process.Recommended);
            Assert.Equal("appspawn", process.Name);
        }

        [Theory]
        [InlineData("Worker", "worker")]
        [InlineData("com.example.game:worker", "com.example.game:renderer")]
        public void ConcretePsConflictsCannotBeCompletedAsGenericAliases(string first, string second)
        {
            var rows = new[] { Process(first, 100), Process(second, 100), Process("appspawn", 100) };
            foreach (var row in rows) row.BundleId = "com.example.game";
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(rows));
            Assert.Equal(first, process.Name);
            Assert.True(process.OwnershipAmbiguous);
            Assert.Empty(process.BundleId);
        }

        private static ProcessInfo Process(string name = "appspawn", int user = -1)
        {
            return new ProcessInfo { Pid = 501, Name = name, DisplayName = name,
                Platform = "harmony", HarmonyUserId = user };
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CompletedExecutableFlowsIntoBothPickerProjections(bool explicitBundle)
        {
            string bundle = explicitBundle ? ",\"bundleName\":\"com.example.game\"" : "";
            var service = InventoryService(new ProcessResult(0, "PID NAME\n502 appspawn\n", ""),
                "{\"pid\":502,\"command\":\"/system/bin/com.example.game:renderer --bundle-name com.other\","
                + "\"userId\":100" + bundle + "}");

            var inventory = await service.ListTargetsAsync("TEST-HDC", CancellationToken.None);
            var process = Assert.Single(inventory.Processes);
            var app = Assert.Single(inventory.Apps);
            Assert.Equal("com.example.game:renderer", process.Name);
            Assert.Equal(process.Name, process.DisplayName);
            Assert.Equal(process.Name, app.ProcessName);
            Assert.Equal(502, app.ProcessPid);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Equal(12345L, process.HarmonyStartTimeTicks);
            Assert.Equal(explicitBundle ? "com.example.game" : "", process.BundleId);
            Assert.Equal(process.BundleId, app.BundleId);
            Assert.Equal(explicitBundle, process.OwnershipVerified);
        }

        [Theory]
        [InlineData("appspawn")]
        [InlineData("com.example.game:worker")]
        public void CompetingExecutablesCannotBeHiddenByGenericHostOrSharedBundle(string name)
        {
            var process = Process(name, 100);
            process.HarmonyStartTimeTicks = 12345;
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, new[]
            {
                new HarmonyProcessBinding { Pid = 501, ProcessName = "com.example.game:worker",
                    BundleId = "com.example.game", HarmonyUserId = 100, Foreground = true },
                new HarmonyProcessBinding { Pid = 501, ProcessName = "com.example.game:renderer",
                    BundleId = "com.example.game", HarmonyUserId = 100, Foreground = true }
            });
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.OwnershipVerified);
            Assert.False(process.Recommended);
            Assert.Empty(process.BundleId);
            Assert.Equal(name, process.Name);
            Assert.Equal(12345L, process.HarmonyStartTimeTicks);
        }

        [Fact]
        public void MatchingBundleCannotOverrideConflictingPsExecutable()
        {
            var process = Process("com.example.game:worker", 100);
            process.BundleId = "com.example.game";
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, new[]
            {
                new HarmonyProcessBinding { Pid = 501, ProcessName = "com.example.game:renderer",
                    BundleId = "com.example.game", HarmonyUserId = 100, Foreground = true }
            });
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.Recommended);
            Assert.False(process.OwnershipVerified);
            Assert.Empty(process.BundleId);
            Assert.Equal("com.example.game:worker", process.Name);
        }

        [Fact]
        public void UniqueExecutableCompletionPreservesFriendlyLabelAndIgnoresGenericAliases()
        {
            var process = Process("appspawn", 100);
            process.DisplayName = "Friendly game";
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, new[]
            {
                new HarmonyProcessBinding { Pid = 501, ProcessName = "appspawn", HarmonyUserId = 100 },
                new HarmonyProcessBinding { Pid = 501, ProcessName = "/system/bin/worker --flag", HarmonyUserId = 100 },
                new HarmonyProcessBinding { Pid = 501, ProcessName = "worker", HarmonyUserId = 100 }
            });
            Assert.Equal("worker", process.Name);
            Assert.Equal("Friendly game", process.DisplayName);
            Assert.False(process.OwnershipAmbiguous);
            Assert.Empty(process.BundleId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OwnershipConflictMustNotReplaceGenericExecutable(bool userConflict)
        {
            var process = Process("appspawn", 100);
            process.BundleId = "com.example.game";
            process.OwnerBundleId = process.BundleId;
            process.OwnershipVerified = true;
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, new[]
            {
                new HarmonyProcessBinding { Pid = 501, ProcessName = "worker",
                    BundleId = userConflict ? "com.example.game" : "com.example.other",
                    HarmonyUserId = userConflict ? 101 : 100, Foreground = true }
            });
            Assert.Equal("appspawn", process.Name);
            Assert.Equal("appspawn", process.DisplayName);
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.Recommended);
        }

        [Fact]
        public void OfficialProcessDumpKeepsSystemServicesAndDoesNotInventBundles()
        {
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "AppRunningRecords:\n  AppRunningRecord ID #0\n"
                + "    process name [appspawn]\n    pid #501  uid #0\n    state #FOREGROUND\n"
                + "  AppRunningRecord ID #1\n    process name [com.example.game:renderer]\n"
                + "    pid #502  uid #20010123\n    state #BACKGROUND\n");
            Assert.Equal(2, bindings.Count);
            Assert.All(bindings, binding => Assert.True(string.IsNullOrEmpty(binding.BundleId)));
            Assert.Equal(-1, bindings.Single(binding => binding.Pid == 501).HarmonyUserId);
            Assert.Equal(100, bindings.Single(binding => binding.Pid == 502).HarmonyUserId);
            var process = Process();
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, bindings);
            Assert.True(process.Recommended);
            Assert.False(process.OwnershipVerified);
            Assert.True(string.IsNullOrEmpty(process.BundleId));
        }

        [Theory]
        [InlineData("process_name", "com.example.service", "com.example.service")]
        [InlineData("process", "com.example.worker", "com.example.worker")]
        [InlineData("name", "foundation", "foundation")]
        [InlineData("command", "/system/bin/appspawn", "appspawn")]
        public void AbilityJsonProcessAliasesRequireAndPreserveThePid(string field, string name, string expectedProcessName)
        {
            var binding = Assert.Single(HarmonyLookupService.ParseAbilityProcessBindings(
                "{\"pid\":503,\"" + field + "\":\"" + name + "\",\"uid\":20010123}"));
            Assert.Equal(503, binding.Pid);
            Assert.Equal(expectedProcessName ?? name, binding.ProcessName);
            Assert.Equal(100, binding.HarmonyUserId);
            Assert.True(string.IsNullOrEmpty(binding.BundleId));
        }

        [Fact]
        public void SeparateRecordsAndNestedHostPidsNeverInheritBundleOwnership()
        {
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "AbilityRecord ID #8\n  bundle name [com.example.old]\n  state #FOREGROUND\n"
                + "AppRunningRecords:\n  AppRunningRecord ID #0\n    process name [appspawn]\n"
                + "    pid #501 uid #210001\n    state #BACKGROUND\n"
                + "    root caller #0\n      pid #700\n    uiextension provider #0\n      pid #701\n"
                + "  AppRunningRecord ID #1\n    process name [service]\n"
                + "    bundle name [com.example.service]\n    pid #502 uid #410001\n    state #FOREGROUND\n");
            Assert.Equal(2, bindings.Count);
            Assert.True(string.IsNullOrEmpty(bindings.Single(binding => binding.Pid == 501).BundleId));
            Assert.False(bindings.Single(binding => binding.Pid == 501).Foreground);
            Assert.Equal("com.example.service", bindings.Single(binding => binding.Pid == 502).BundleId);
        }

        [Theory]
        [InlineData("INACTIVE")]
        [InlineData("not_foreground")]
        [InlineData("background")]
        [InlineData("1")]
        public void UnknownOrNegativeStatesAreNotForeground(string state)
        {
            var binding = Assert.Single(HarmonyLookupService.ParseAbilityProcessBindings(
                "pid=501,bundleName=com.example.game,state=" + state));
            Assert.False(binding.Foreground);
        }

        [Fact]
        public void FieldsKeepTheirTypeWhenUidAndExplicitUserAreOnTheSameLine()
        {
            var binding = Assert.Single(HarmonyLookupService.ParseAbilityProcessBindings(
                "pid=501 bundleName=com.example.game userId=100 uid=20010123 state=FOREGROUND"));
            Assert.Equal(100, binding.HarmonyUserId);
            Assert.Equal("com.example.game", binding.BundleId);
            Assert.True(binding.Foreground);
        }

        [Fact]
        public void UserFieldsBeforeEachAbilityProcessRecordKeepPidOwnership()
        {
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "AppRunningRecords:\n"
                + "  userId: 0\n"
                + "  bundleName: com.example.owner\n"
                + "  processId: 501\n"
                + "  state: BACKGROUND\n"
                + "  userId: 100\n"
                + "  bundleName: com.example.work\n"
                + "  processId: 502\n"
                + "  state: FOREGROUND\n");

            Assert.Equal(2, bindings.Count);
            Assert.Equal(0, Assert.Single(bindings, binding => binding.Pid == 501).HarmonyUserId);
            Assert.Equal("com.example.owner", Assert.Single(bindings, binding => binding.Pid == 501).BundleId);
            Assert.Equal(100, Assert.Single(bindings, binding => binding.Pid == 502).HarmonyUserId);
            Assert.Equal("com.example.work", Assert.Single(bindings, binding => binding.Pid == 502).BundleId);
        }

        [Fact]
        public void TrailingUserFieldDoesNotSplitTheCurrentAbilityProcessRecord()
        {
            var binding = Assert.Single(HarmonyLookupService.ParseAbilityProcessBindings(
                "AppRunningRecord ID #0\n"
                + "  bundleName: com.example.game\n"
                + "  processId: 501\n"
                + "  userId: 100\n"
                + "  processName: com.example.game\n"
                + "  state: FOREGROUND\n"));

            Assert.Equal(501, binding.Pid);
            Assert.Equal(100, binding.HarmonyUserId);
            Assert.Equal("com.example.game", binding.BundleId);
            Assert.Equal("com.example.game", binding.ProcessName);
        }

        [Fact]
        public void InlineUsersRemainWithTheirOwnProcessAcrossConsecutiveRecords()
        {
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "pid=501 bundleName=com.example.owner userId=0 state=BACKGROUND\n"
                + "pid=502 bundleName=com.example.work userId=100 state=FOREGROUND\n");

            Assert.Equal(2, bindings.Count);
            Assert.Equal(0, Assert.Single(bindings, binding => binding.Pid == 501).HarmonyUserId);
            Assert.Equal(100, Assert.Single(bindings, binding => binding.Pid == 502).HarmonyUserId);
            Assert.False(Assert.Single(bindings, binding => binding.Pid == 501).Foreground);
            Assert.True(Assert.Single(bindings, binding => binding.Pid == 502).Foreground);
        }

        [Theory]
        [InlineData("state: BACKGROUND")]
        [InlineData("processName: com.example.owner")]
        public void TrailingUserCannotLookPastCurrentProcessFieldsIntoTheNextRecord(string trailingField)
        {
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "bundleName: com.example.owner\nprocessId: 501\nuserId: 0\n"
                + trailingField + "\n"
                + "bundleName: com.example.work\nprocessId: 502\nuserId: 100\nstate: FOREGROUND\n");

            Assert.Equal(2, bindings.Count);
            Assert.Equal(0, Assert.Single(bindings, binding => binding.Pid == 501).HarmonyUserId);
            Assert.Equal("com.example.owner", Assert.Single(bindings, binding => binding.Pid == 501).BundleId);
            Assert.Equal(100, Assert.Single(bindings, binding => binding.Pid == 502).HarmonyUserId);
            Assert.Equal("com.example.work", Assert.Single(bindings, binding => binding.Pid == 502).BundleId);
        }

        [Theory]
        [InlineData("bundleName=com.example.game,uid=2000,pid=501", -1)]
        [InlineData("bundleName=com.example.game,userId=0,pid=501", 0)]
        [InlineData("bundleName=com.example.game,uid=401234,pid=501", 2)]
        public void SystemUidAndExplicitUserRemainDistinct(string output, int expected)
        {
            Assert.Equal(expected, Assert.Single(HarmonyLookupService.ParseAbilityProcessBindings(output)).HarmonyUserId);
        }

        [Fact]
        public void PrettyJsonAndFollowingTextAreIsolated()
        {
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "{\n  \"bundleName\": \"com.example.game\",\n  \"pid\": 501,\n  \"userId\": 100,\n  \"isForeground\": true\n}\n"
                + "AppRunningRecord ID #1\n  process name [service]\n  pid #502 uid #0\n  state #BACKGROUND\n");
            Assert.Equal(2, bindings.Count);
            Assert.Equal("com.example.game", bindings.Single(binding => binding.Pid == 501).BundleId);
            Assert.True(string.IsNullOrEmpty(bindings.Single(binding => binding.Pid == 502).BundleId));
        }

        [Fact]
        public void JsonRunningRecordsInheritOuterUserAndKeepExplicitChildUser()
        {
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "{\"userId\":100,\"appRunningRecords\":["
                + "{\"pid\":501,\"state\":\"BACKGROUND\"},"
                + "{\"pid\":502,\"userId\":101,\"state\":\"FOREGROUND\"}]}" );

            Assert.Equal(100, Assert.Single(bindings, binding => binding.Pid == 501).HarmonyUserId);
            Assert.Equal(101, Assert.Single(bindings, binding => binding.Pid == 502).HarmonyUserId);
        }

        [Fact]
        public void SameBundleDifferentUsersCannotBindOnePid()
        {
            var process = Process("com.example.game", 100);
            process.BundleId = "com.example.game";
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, new[]
            {
                new HarmonyProcessBinding { Pid = 501, BundleId = "com.example.game", HarmonyUserId = 100 },
                new HarmonyProcessBinding { Pid = 501, BundleId = "com.example.game", HarmonyUserId = 101 }
            });
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.OwnershipVerified);
            Assert.True(string.IsNullOrEmpty(process.BundleId));
            Assert.True(string.IsNullOrEmpty(process.OwnerBundleId));
            Assert.Equal(100, process.HarmonyUserId);
            var apps = new List<AppInfo>();
            HarmonyLookupService.MergeProcessApps(apps, new[] { process });
            Assert.False(Assert.Single(apps).CanAttemptLaunch);
        }

        [Fact]
        public void UserConflictIsCheckedBeforeMutatingOwnership()
        {
            var process = Process("appspawn", 100);
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, new[]
            {
                new HarmonyProcessBinding { Pid = 501, BundleId = "com.example.other", HarmonyUserId = 101, Foreground = true }
            });
            Assert.True(process.OwnershipAmbiguous);
            Assert.False(process.OwnershipVerified);
            Assert.True(string.IsNullOrEmpty(process.BundleId));
            Assert.True(string.IsNullOrEmpty(process.OwnerBundleId));
            Assert.False(process.Recommended);
            Assert.Equal(100, process.HarmonyUserId);
        }

        [Fact]
        public void DifferentNameAtTheSamePidBlocksStateEnrichment()
        {
            var process = Process("com.example.game:renderer", 100);
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "AppRunningRecord ID #0\n  process name [com.example.other]\n"
                + "  pid #501 uid #20010123\n  state #FOREGROUND\n");
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, bindings);
            Assert.False(process.Recommended);
            Assert.True(process.OwnershipAmbiguous);
        }

        [Theory]
        [InlineData(0, "", "", false)]
        [InlineData(1, "", "permission denied", false)]
        [InlineData(1, "", "{\"pid\":501,\"bundleName\":\"com.example.game\",\"userId\":100,\"isForeground\":true}", true)]
        [InlineData(1, "bundleName=com.example.game", "pid=501,userId=100", false)]
        [InlineData(1, "{\"pid\":501,\"bundleName\":\"com.example.game\",\"userId\":100,\"isForeground\":true}", "partial output", true)]
        [InlineData(0, "{\"pid\":501,\"bundleName\":\"com.example.game\",\"userId\":100}", "[Fail] device offline", false)]
        public async Task OptionalDumpFailureOrPartialSuccessDoesNotLoseRealProcesses(
            int exitCode, string stdout, string stderr, bool expectedOwner)
        {
            var commands = new List<string>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                string key = string.Join(" ", command);
                commands.Add(key);
                if (key.StartsWith("ps ", StringComparison.Ordinal) || key == "ps")
                    return Task.FromResult(new ProcessResult(0, "USERID PID PPID NAME\n100 501 1 appspawn\n", ""));
                if (key.StartsWith("aa dump ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(exitCode, stdout, stderr));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fixture command"));
            });
            var process = Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None));
            Assert.Equal(501, process.Pid);
            Assert.Equal(expectedOwner, process.OwnershipVerified);
            Assert.Contains("aa dump -r", commands);
            Assert.Contains("aa dump -a", commands);
            if (expectedOwner) Assert.Equal("com.example.game", process.BundleId);
        }

        [Fact]
        public async Task CallerCancellationDuringOptionalDumpStillCancelsDiscovery()
        {
            using var cancel = new CancellationTokenSource();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "PID NAME\n501 appspawn\n", ""));
                cancel.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new ProcessResult(0, "", ""));
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListProcessesAsync("TEST-HDC", cancel.Token));
        }

        [Fact]
        public async Task OptionalReadTimeoutRetainsProcessesWithoutContinuingTheProbeMatrix()
        {
            int probes = 0;
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "PID NAME\n501 appspawn\n", ""));
                if (command[0] == "aa")
                {
                    Assert.True(token.CanBeCanceled);
                    Assert.InRange(timeout, 1, 2000);
                    probes++;
                    throw new OperationCanceledException(token);
                }
                return Task.FromResult(new ProcessResult(1, "", "unsupported fixture command"));
            });
            Assert.Equal(501, Assert.Single(await service.ListProcessesAsync("TEST-HDC", CancellationToken.None)).Pid);
            Assert.Equal(1, probes);
        }
    }
}
