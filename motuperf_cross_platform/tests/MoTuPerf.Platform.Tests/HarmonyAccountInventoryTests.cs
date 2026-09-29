using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyAccountInventoryTests
    {
        [Theory]
        [InlineData("permission denied for userId=100")]
        [InlineData("error: account id: 100 not found")]
        [InlineData("user 100 does not exist")]
        [InlineData("Name: test user 101")]
        [InlineData("usage: userId=100")]
        [InlineData("userId=100oops")]
        [InlineData("userId=100.5")]
        [InlineData("userId100")]
        [InlineData("ID: 100oops")]
        [InlineData("UserInfo{100:Owner:13")]
        [InlineData("UserInfo{100oops:Owner:13}")]
        [InlineData("error: UserInfo{100:Owner:13} unavailable")]
        [InlineData("{\"message\":\"user 100 unavailable\"}")]
        [InlineData("ID: -1")]
        [InlineData("ID: 2147483648")]
        public void DiagnosticAndMalformedRowsDoNotCreateAccounts(string output)
        {
            Assert.Empty(HarmonyLookupService.ParseExplicitUserIds(output));
        }

        [Theory]
        [InlineData("")]
        [InlineData("ID: 100\n")]
        public void AllAccountParserEntrypointsPreserveUnknownInsteadOfInventingOwner(string output)
        {
            Assert.Equal(HarmonyLookupService.ParseExplicitUserIds(output), HarmonyLookupService.ParseUserIds(output));
        }

        [Fact]
        public void CompleteNativeAndCompatibilityRecordsRetainAllActualAccounts()
        {
            string output = "ID: 100\n    Name: test user 999\n    Serial Number: 1234\n"
                + "UserInfo{0:Owner:13} running\nUserInfo{id=101,name=Work} stopped\n"
                + "userId=12\nuser id: 14\nuser 1000\nlocalId=102\naccount_id: 103\n"
                + "userId=12\nerror: user 555 not found\n";
            Assert.Equal(new[] { 0, 12, 14, 100, 101, 102, 103, 1000 }, HarmonyLookupService.ParseExplicitUserIds(output));
        }

        [Theory]
        [InlineData(false, 0, "user 100 does not exist", "")]
        [InlineData(true, 1, "", "permission denied for userId=100")]
        [InlineData(false, 0, "ID: 100\n", "[Fail] transport disconnected")]
        [InlineData(true, 0, "[fail] transport disconnected", "ID: 100\n")]
        [InlineData(false, 1, "UserInfo{100:", "Owner:13}")]
        public async Task UnprovenAccountsCannotScopeInventoryOrAuthorizeLaunch(
            bool appsOnly, int exitCode, string stdout, string stderr)
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (command[0] == "acm") return Task.FromResult(new ProcessResult(exitCode, stdout, stderr));
                if (command[0] == "bm") return Task.FromResult(new ProcessResult(0, "com.example.game", ""));
                if (command[0] == "ps") return Task.FromResult(new ProcessResult(0, "PID NAME\n51 worker\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "command unavailable"));
            });
            var inventory = appsOnly ? null : await service.ListTargetsAsync("test-hdc", CancellationToken.None);
            var apps = appsOnly ? await service.ListAppsAsync("test-hdc", CancellationToken.None) : inventory.Apps;
            var app = Assert.Single(apps, candidate => candidate.BundleId == "com.example.game");
            Assert.Equal(-1, app.HarmonyUserId);
            Assert.False(app.CanAttemptLaunch);
            Assert.Contains(apps, candidate => candidate.ProcessPid == 51 && candidate.IsProcessOnly);
            if (inventory != null) Assert.NotEmpty(inventory.UserInventoryError);
            Assert.DoesNotContain(commands, command => HarmonyLookupService.CommandUserId(command) >= 0);
            commands.Clear();
            var result = await service.LaunchAppAsync("test-hdc", app, CancellationToken.None);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(commands);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        public async Task PartialAccountResultsRetainCompleteRecordsAndIsolateDiagnostics(int exitCode, bool stderr)
        {
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "acm")
                {
                    string records = "ID: 0\nID: 100\nuser 999 does not exist\n";
                    return Task.FromResult(new ProcessResult(exitCode, stderr ? "" : records, stderr ? records : ""));
                }
                if (command[0] == "bm") return Task.FromResult(new ProcessResult(0, "com.example.game", ""));
                if (command[0] == "aa" && command[1] == "start")
                {
                    starts.Add(command);
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                }
                return Task.FromResult(new ProcessResult(1, "", "command unavailable"));
            });
            var apps = await service.ListAppsAsync("test-hdc", CancellationToken.None);
            Assert.Equal(new[] { 0, 100 }, apps.Select(app => app.HarmonyUserId));
            foreach (var app in apps)
                Assert.Equal(0, (await service.LaunchAppAsync("test-hdc", app, CancellationToken.None)).ExitCode);
            Assert.Equal(new[] { 0, 100 }, starts.Select(HarmonyLookupService.CommandUserId));
        }

        [Fact]
        public async Task SuccessfulFallbackClearsEarlierUnknownAccountDiagnostic()
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "hidumper")
                    return Task.FromResult(new ProcessResult(0, "ID: 100\n", ""));
                if (command[0] == "bm")
                    return Task.FromResult(new ProcessResult(0, "com.example.game", ""));
                return Task.FromResult(new ProcessResult(1, "", "command unavailable"));
            });

            var inventory = await service.ListTargetsAsync("test-hdc", CancellationToken.None);

            Assert.Equal(100, Assert.Single(inventory.Apps).HarmonyUserId);
            Assert.Empty(inventory.UserInventoryError);
        }

        [Fact]
        public async Task DirectProcessInventoryUsesDiscoveredHarmonyUserScopes()
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (command[0] == "acm")
                    return Task.FromResult(new ProcessResult(0, "ID: 100\n", ""));
                if (command[0] == "ps")
                    return Task.FromResult(new ProcessResult(0, "PID NAME\n501 appspawn\n", ""));
                if (command[0] == "aa" && command[1] == "dump"
                    && command.Contains("-u") && command.Contains("100"))
                {
                    return Task.FromResult(new ProcessResult(0,
                        "{\"pid\":501,\"bundleName\":\"com.example.work\",\"processName\":\"appspawn\",\"userId\":100}\n",
                        ""));
                }
                if (command[0] == "sh")
                    return Task.FromResult(new ProcessResult(0, "", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fixture command"));
            });

            List<ProcessInfo> processes = await service.ListProcessesAsync("HDC-1", CancellationToken.None);

            ProcessInfo process = Assert.Single(processes);
            Assert.Equal(501, process.Pid);
            Assert.Equal("com.example.work", process.BundleId);
            Assert.Equal(100, process.HarmonyUserId);
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "aa", "dump", "-a", "-u", "100" }));
            Assert.Contains(commands, command => command.SequenceEqual(new[] { "aa", "dump", "-a", "--userId", "100" }));
        }

        [Fact]
        public async Task SingleDiscoveredUserScopesGlobalInstalledAppRecords()
        {
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "acm")
                    return Task.FromResult(new ProcessResult(0, "ID: 100\n", ""));
                if (command[0] == "bm" && command.Length == 3)
                    return Task.FromResult(new ProcessResult(0,
                        "bundleName: com.example.single\nmoduleName: entry\nabilityName: MainAbility\n", ""));
                if (command[0] == "aa" && command[1] == "start")
                {
                    starts.Add(command);
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                }
                if (command[0] == "ps") return Task.FromResult(new ProcessResult(0, "PID NAME\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fixture command"));
            });

            List<AppInfo> apps = await service.ListAppsAsync("HDC-1", CancellationToken.None);
            AppInfo app = Assert.Single(apps, candidate => candidate.BundleId == "com.example.single");

            Assert.Equal(100, app.HarmonyUserId);
            Assert.True(app.CanAttemptLaunch);
            Assert.Equal(0, (await service.LaunchAppAsync("HDC-1", app, CancellationToken.None)).ExitCode);
            Assert.NotEmpty(starts);
            Assert.All(starts, command => Assert.Equal(100, HarmonyLookupService.CommandUserId(command)));
        }

        [Fact]
        public async Task MultipleDiscoveredUsersKeepGlobalInstalledAppUnscoped()
        {
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "acm")
                    return Task.FromResult(new ProcessResult(0, "ID: 0\nID: 100\n", ""));
                if (command[0] == "bm" && command.Length == 3)
                    return Task.FromResult(new ProcessResult(0, "bundleName: com.example.shared\n", ""));
                if (command[0] == "ps") return Task.FromResult(new ProcessResult(0, "PID NAME\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "unsupported fixture command"));
            });

            AppInfo app = Assert.Single(await service.ListAppsAsync("HDC-1", CancellationToken.None),
                candidate => candidate.BundleId == "com.example.shared");

            Assert.Equal(-1, app.HarmonyUserId);
            Assert.False(app.CanAttemptLaunch);
        }
    }
}
