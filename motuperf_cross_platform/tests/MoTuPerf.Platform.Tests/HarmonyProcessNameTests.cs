using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyProcessNameTests
    {
        private const string FullName = "com.example.game:renderer";
        private const string Comm = "com.example.gam";

        private static ProcessInfo Row(string field, string name, int user = 100)
        {
            return Assert.Single(HarmonyLookupService.ParseProcesses(
                $"USERID PID {field}\n{user} 502 {name}\n", "HDC-1"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FullPsNameAndCommRemainOneUnambiguousTarget(bool commFirst)
        {
            var full = Row("ARGS", "/system/bin/" + FullName + " --arg com.other.app");
            var comm = Row("COMM", Comm);
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(
                commFirst ? new[] { comm, full } : new[] { full, comm }));

            Assert.Equal(FullName, process.Name);
            Assert.Equal(FullName, process.DisplayName);
            Assert.Equal("com.example.game", process.BundleId);
            Assert.False(process.OwnershipAmbiguous);
            Assert.Equal(100, process.HarmonyUserId);
        }

        [Fact]
        public void HarmonyVendorCommWithoutComPrefixRemainsOneTarget()
        {
            var full = Row("ARGS", "com.more2.fkmj.hwhm");
            var comm = Row("COMM", "more2.fkmj.hwhm");
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(new[] { comm, full }));

            Assert.Equal("com.more2.fkmj.hwhm", process.Name);
            Assert.Equal("com.more2.fkmj.hwhm", process.BundleId);
            Assert.False(process.OwnershipAmbiguous);
        }

        [Fact]
        public void HarmonyVendorCommCanKeepForegroundRecommendation()
        {
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(new[]
            {
                Row("COMM", "more2.fkmj.hwhm"),
                Row("ARGS", "com.more2.fkmj.hwhm")
            }));

            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, new[]
            {
                new HarmonyProcessBinding
                {
                    Pid = 502,
                    ProcessName = "com.more2.fkmj.hwhm",
                    HarmonyUserId = 100,
                    Foreground = true
                }
            });

            Assert.True(process.Recommended);
            Assert.True(process.ForegroundApplication);
            Assert.False(process.OwnershipAmbiguous);
        }

        [Theory]
        [InlineData("/bin/sh")]
        [InlineData("/bin/printf")]
        [InlineData("/bin/sed")]
        [InlineData("/bin/head")]
        [InlineData("/bin/tr")]
        [InlineData("/bin/hidumper")]
        public void ProcInventoryShellHelpersAreNotSelectableTargets(string helper)
        {
            var process = Row("COMMAND", helper);

            Assert.Empty(HarmonyLookupService.MergeProcesses(new[] { process }));
        }

        [Fact]
        public void HidumperWithExplicitBundleOwnershipRemainsSelectable()
        {
            var process = Assert.Single(HarmonyLookupService.ParseProcesses(
                "USERID PID COMMAND BUNDLE_NAME\n100 502 /bin/hidumper com.example.game\n", "HDC-1"));

            var merged = Assert.Single(HarmonyLookupService.MergeProcesses(new[] { process }));

            Assert.Equal("com.example.game", merged.BundleId);
            Assert.Equal("hidumper", merged.Name);
        }

        [Fact]
        public void BracketedHarmonyCommandIsCommEvidence()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "USERID PID COMMAND\n100 502 [more2.fkmj.hwhm]\n"
                + "USERID PID ARGS\n100 502 com.more2.fkmj.hwhm\n", "HDC-1");
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(rows));

            Assert.Equal("com.more2.fkmj.hwhm", process.Name);
            Assert.Equal("com.more2.fkmj.hwhm", process.BundleId);
            Assert.False(process.OwnershipAmbiguous);
        }

        [Fact]
        public void FifteenByteHarmonyCmdIsCommEvidence()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "USERID PID CMD\n100 502 more2.fkmj.hwhm\n"
                + "USERID PID ARGS\n100 502 com.more2.fkmj.hwhm\n", "HDC-1");
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(rows));

            Assert.Equal("com.more2.fkmj.hwhm", process.Name);
            Assert.Equal("com.more2.fkmj.hwhm", process.BundleId);
            Assert.False(process.OwnershipAmbiguous);
        }

        [Theory]
        [InlineData("USERID PID COMM\n100 502 com.example.gam\n")]
        [InlineData("pid=502,userId=100,comm=com.example.gam")]
        public void CommAloneCannotInventAInstalledBundle(string output)
        {
            var rows = HarmonyLookupService.ParseProcesses(output, "HDC-1");
            var process = Assert.Single(rows);
            Assert.Equal(Comm, process.Name);
            Assert.Empty(process.BundleId);
            var apps = new List<AppInfo>();
            HarmonyLookupService.MergeProcessApps(apps, rows);
            var app = Assert.Single(apps);
            Assert.Equal(502, app.ProcessPid);
            Assert.False(app.CanAttemptLaunch);
        }

        [Theory]
        [InlineData("ARGS", "com.example.gam", 100)]
        [InlineData("COMM", "com.example.ga", 100)]
        [InlineData("COMM", "com.example.gax", 100)]
        [InlineData("COMM", "com.example.gam", 101)]
        public void OnlyExplicitMatchingCommCanBeAnAlias(string field, string name, int user)
        {
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(
                new[] { Row(field, name, user), Row("ARGS", FullName) }));
            Assert.True(process.OwnershipAmbiguous);
            Assert.Empty(process.BundleId);
        }

        [Fact]
        public void MultipleFullNamesSharingCommStayAmbiguous()
        {
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(new[]
            {
                Row("COMM", Comm), Row("ARGS", FullName), Row("CMDLINE", "com.example.game:worker")
            }));
            Assert.True(process.OwnershipAmbiguous);
            Assert.Empty(process.BundleId);
        }

        [Fact]
        public void ChangedStartTicksPreventAliasCompletion()
        {
            var comm = Row("COMM", Comm);
            comm.HarmonyStartTimeTicks = 12345;
            var full = Row("ARGS", FullName);
            full.HarmonyStartTimeTicks = 12346;
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(new[] { comm, full }));
            Assert.True(process.OwnershipAmbiguous);
            Assert.Empty(process.BundleId);
        }

        [Theory]
        [InlineData("pid=502,userId=100,comm=com.example.gam,bundleName=com.example.game", true)]
        [InlineData("pid=502,userId=100,comm=com.example.gam,args=com.example.game:renderer", false)]
        public void KeyedNameSourceTracksTheFieldActuallyUsed(string output, bool isComm)
        {
            var process = Assert.Single(HarmonyLookupService.ParseProcesses(output, "HDC-1"));
            Assert.Equal(isComm ? Comm : FullName, process.Name);
            Assert.Equal(isComm, process.HarmonyNameIsComm);
            Assert.Equal("com.example.game", process.BundleId);
        }

        [Fact]
        public void SameNamedRowsCannotDiscardDifferentUsersOrNameSources()
        {
            var rows = HarmonyLookupService.ParseProcesses(
                "USERID PID COMM\n100 502 com.example.gam\nUSERID PID ARGS\n101 502 com.example.gam", "HDC-1");
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(rows));
            Assert.True(process.OwnershipAmbiguous);
            Assert.Empty(process.BundleId);
        }

        [Fact]
        public void ProcNameConflictsNeverChooseTheLastRecord()
        {
            var names = HarmonyLookupService.ParseProcessNamesFromProc(
                "PID=502 CMDLINE=" + FullName + "\nPID=502 CMDLINE=com.example.game:worker\n"
                + "PID=502 CMDLINE=" + FullName + "\nPID=503 CMDLINE=foundation\n");
            Assert.False(names.ContainsKey(502));
            Assert.Equal("foundation", names[503]);
        }

        [Theory]
        [InlineData("abc\u754c\u9762\u6e38\u620f", true)]
        [InlineData("\u754c\u9762abcdefghijklm", false)]
        public void CommTruncationUsesBytesInsteadOfCharacterCount(string comm, bool matches)
        {
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(new[]
            {
                Row("COMM", comm), Row("ARGS", comm + "worker")
            }));
            Assert.Equal(!matches, process.OwnershipAmbiguous);
            Assert.Equal(matches ? comm + "worker" : comm, process.Name);
        }

        [Fact]
        public void GenericAndCommRowsDoNotHideCompetingExecutables()
        {
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(new[]
            {
                Row("ARGS", "appspawn"), Row("COMM", Comm), Row("ARGS", FullName),
                Row("CMDLINE", "com.example.game:worker")
            }));
            Assert.True(process.OwnershipAmbiguous);
            Assert.Empty(process.BundleId);
        }

        [Fact]
        public void CompletingCommKeepsAnIndependentFriendlyLabel()
        {
            var comm = Row("COMM", Comm);
            comm.DisplayName = "Friendly game";
            var process = Assert.Single(HarmonyLookupService.MergeProcesses(new[] { comm, Row("ARGS", FullName) }));
            Assert.Equal(FullName, process.Name);
            Assert.Equal("Friendly game", process.DisplayName);
            Assert.False(process.HarmonyNameIsComm);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AbilityConflictDoesNotCompleteComm(bool userConflict)
        {
            var process = Row("COMM", Comm);
            var bindings = new List<HarmonyProcessBinding>
            {
                new HarmonyProcessBinding { Pid = 502, ProcessName = FullName,
                    BundleId = "com.example.game", HarmonyUserId = userConflict ? 101 : 100 }
            };
            if (!userConflict) bindings.Add(new HarmonyProcessBinding
            {
                Pid = 502, ProcessName = "com.example.game:worker", HarmonyUserId = 100
            });
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, bindings);
            Assert.True(process.OwnershipAmbiguous);
            Assert.Equal(Comm, process.Name);
            Assert.Empty(process.BundleId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OptionalCommLookupTimeoutKeepsRowsButUserCancellationPropagates(bool cancelUser)
        {
            using var cancellation = new CancellationTokenSource();
            int procTimeout = 0;
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "USERID PID COMM\n100 502 " + Comm, ""));
                if (command[0] == "sh" && command[2].Contains("/proc/$p/cmdline", StringComparison.Ordinal))
                {
                    procTimeout = timeout;
                    if (cancelUser) cancellation.Cancel();
                    throw new OperationCanceledException(token);
                }
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });
            if (cancelUser)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListProcessesAsync("HDC-1", cancellation.Token));
            else
            {
                var process = Assert.Single(await service.ListProcessesAsync("HDC-1", cancellation.Token));
                Assert.Equal(Comm, process.Name);
                Assert.Empty(process.BundleId);
                Assert.False(process.OwnershipAmbiguous);
            }
            Assert.InRange(procTimeout, 1, 2000);
        }

        [Fact]
        public void AbilityFullNameCompletesCommWithoutLosingOwnership()
        {
            var process = Row("COMM", Comm);
            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, new[]
            {
                new HarmonyProcessBinding { Pid = 502, ProcessName = FullName,
                    BundleId = "com.example.game", HarmonyUserId = 100, Foreground = true }
            });
            Assert.Equal(FullName, process.Name);
            Assert.Equal("com.example.game", process.BundleId);
            Assert.True(process.OwnershipVerified);
            Assert.True(process.Recommended);
            Assert.False(process.OwnershipAmbiguous);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FullPsEvidenceCompletesCommBeforeAnyOptionalProcRead(bool appsOnly)
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (command[0] == "ps")
                {
                    bool full = command.Contains("PID,ARGS");
                    return Task.FromResult(new ProcessResult(0, full
                        ? "USERID PID ARGS\n100 502 " + FullName
                        : "USERID PID COMM\n100 502 " + Comm, ""));
                }
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });
            var apps = appsOnly ? await service.ListAppsAsync("HDC-1", CancellationToken.None)
                : (await service.ListTargetsAsync("HDC-1", CancellationToken.None)).Apps;
            var app = Assert.Single(apps);
            Assert.Equal(FullName, app.ProcessName);
            Assert.Equal("com.example.game", app.BundleId);
            Assert.Equal(502, app.ProcessPid);
            Assert.DoesNotContain(commands, command => command[0] == "sh"
                && command[2].Contains("/proc/$p/cmdline", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AbilityCanCompleteCommWhenProcIsDenied()
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "USERID PID COMM\n100 502 " + Comm, ""));
                if (command[0] == "aa")
                    return Task.FromResult(new ProcessResult(0,
                        "[{\"pid\":502,\"processName\":\"" + FullName
                        + "\",\"bundleName\":\"com.example.game\",\"userId\":100}]", ""));
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });
            var process = Assert.Single(await service.ListProcessesAsync("HDC-1", CancellationToken.None));
            Assert.Equal(FullName, process.Name);
            Assert.False(process.HarmonyNameIsComm);
            Assert.True(process.OwnershipVerified);
            Assert.False(process.OwnershipAmbiguous);
            Assert.Equal("com.example.game", process.BundleId);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task CommOnlyPsCanUseRealProcNameInBothInventories(bool appsOnly, bool denied)
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "USERID PID COMM\n100 502 " + Comm, ""));
                if (command[0] == "sh" && command[2].Contains("/proc/$p/cmdline", StringComparison.Ordinal))
                    return Task.FromResult(denied ? new ProcessResult(1, "", "permission denied")
                        : new ProcessResult(0, "PID=502 CMDLINE=/system/bin/" + FullName + " --arg\n", ""));
                if (command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0, "502 (" + Comm + ") S "
                        + string.Join(" ", Enumerable.Repeat("0", 18)) + " 12345 0 0\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "command unavailable"));
            });
            var inventory = appsOnly ? null : await service.ListTargetsAsync("HDC-1", CancellationToken.None);
            var app = Assert.Single(appsOnly ? await service.ListAppsAsync("HDC-1", CancellationToken.None) : inventory.Apps);
            Assert.Equal(denied ? Comm : FullName, app.ProcessName);
            Assert.Equal(denied ? "" : "com.example.game", app.BundleId);
            Assert.Equal(502, app.ProcessPid);
            Assert.Contains(commands, cmd => cmd[0] == "sh" && cmd[2].Contains("/proc/$p/cmdline", StringComparison.Ordinal));
            if (inventory != null)
            {
                var process = Assert.Single(inventory.Processes);
                Assert.Equal(app.ProcessName, process.Name);
                Assert.Equal(12345, process.HarmonyStartTimeTicks);
                Assert.False(process.OwnershipAmbiguous);
            }
        }
    }
}
