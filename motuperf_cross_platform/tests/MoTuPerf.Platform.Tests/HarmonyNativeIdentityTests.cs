using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyNativeIdentityTests
    {
        [Theory]
        [InlineData("UID", "20010123", 100)]
        [InlineData("UID", "20020169", 100)]
        [InlineData("USER", "20210123", 101)]
        [InlineData("UID", "12345", 0)]
        [InlineData("UID", "2000", -1)]
        [InlineData("USER", "100", -1)]
        [InlineData("USERID", "100", 100)]
        [InlineData("USER", "u100_a123", 100)]
        public void UsesNativeUidRangeAndSeparatesProfileColumns(string column, string value, int expected)
        {
            var process = Assert.Single(HarmonyLookupService.ParseProcesses(
                column + " PID NAME\n" + value + " 42 com.example.native\n", "hdc-1"));
            Assert.Equal(expected, process.HarmonyUserId);
        }

        [Theory]
        [InlineData("{\"bundleName\":\"com.example.native\",\"uid\":20010123}")]
        [InlineData("bundleName: com.example.native\nuid: 20010123\n")]
        public void BundleUidHasSameProfileAsNativeProcess(string output)
        {
            var rows = HarmonyLookupService.ExpandHarmonyUserInstances(HarmonyLookupService.ParseApps(output));
            Assert.Equal(100, Assert.Single(rows).HarmonyUserId);
        }

        [Fact]
        public void ParsesOfficialAcmDumpWithoutTreatingOtherNumbersAsUsers()
        {
            var ids = HarmonyLookupService.ParseUserIds(
                "ID: 100\n    Name: Tester\n    Type: admin\n    isForeground: 1\n"
                + "    Serial Number: 1234567\nID: 101\n    Status: inactive\n");
            Assert.Equal(new[] { 100, 101 }, ids);
        }

        [Theory]
        [InlineData("acm dump -a")]
        [InlineData("hidumper -s 200 -a -os_account_infos")]
        public async Task NativeOnlyDeviceRetainsStoppedAppsFromEveryDiscoveredAccount(string accountCommand)
        {
            Task<ProcessResult> Execute(string serial, string[] command, int timeout, CancellationToken token)
            {
                string key = string.Join(" ", command);
                if (key == accountCommand)
                    return Task.FromResult(new ProcessResult(0, "ID: 100\n    Type: admin\nID: 101\n    Type: normal\n", ""));
                if (key == "bm dump -a -u 100" || key == "bm dump -a -u 101")
                    return Task.FromResult(new ProcessResult(0, "com.example.stopped\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "command unavailable"));
            }
            var inventory = await new HarmonyLookupService(Execute).ListTargetsAsync("hdc-1", CancellationToken.None);
            Assert.Equal(new[] { 100, 101 }, inventory.Apps.Select(app => app.HarmonyUserId).OrderBy(id => id));
            Assert.All(inventory.Apps, app => Assert.False(app.IsRunning));
        }

        [Fact]
        public async Task SameAbilityInDifferentProfilesRemainsAvailableToSelectedProfile()
        {
            var commands = new List<string>();
            Task<ProcessResult> Execute(string serial, string[] command, int timeout, CancellationToken token)
            {
                string key = string.Join(" ", command);
                commands.Add(key);
                return Task.FromResult(key == "aa start -u 100 -b com.example.native -m entry -a EntryAbility"
                    ? new ProcessResult(0, "Ability started", "")
                    : new ProcessResult(1, "", "permission denied"));
            }
            var result = await new HarmonyLookupService(Execute).LaunchAppAsync("hdc-1", new AppInfo
            {
                Platform = "harmony", BundleId = "com.example.native", HarmonyUserId = 100,
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo { Module = "entry", Ability = "EntryAbility", HarmonyUserId = 0 },
                    new HarmonyLaunchEntryInfo { Module = "entry", Ability = "EntryAbility", HarmonyUserId = 100 }
                }
            }, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Single(commands);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(100)]
        public async Task KnownAccountNeverFallsBackToDefaultAccount(int userId)
        {
            var commands = new List<string[]>();
            Task<ProcessResult> Execute(string serial, string[] command, int timeout, CancellationToken token)
            {
                commands.Add(command);
                return Task.FromResult(new ProcessResult(1, "", "unknown option"));
            }
            var result = await new HarmonyLookupService(Execute).LaunchAppAsync("hdc-1", new AppInfo
            {
                Platform = "harmony", BundleId = "com.example.native", HarmonyUserId = userId
            }, CancellationToken.None);
            Assert.NotEqual(0, result.ExitCode);
            Assert.NotEmpty(commands);
            Assert.All(commands, command => Assert.Equal(userId, HarmonyLookupService.CommandUserId(command)));
        }

        [Fact]
        public async Task DoesNotRetryWithoutUserSelectorWhenAaRejectsKnownUserScope()
        {
            var commands = new List<string>();
            Task<ProcessResult> Execute(string serial, string[] command, int timeout, CancellationToken token)
            {
                string key = string.Join(" ", command);
                commands.Add(key);
                if (key == "aa start -u 100 -b com.example.native -m entry -a MainAbility")
                    return Task.FromResult(new ProcessResult(1, "", "fail: unknown option.\nusage: aa start <options>"));
                return Task.FromResult(new ProcessResult(1, "", "unexpected command"));
            }

            var service = new HarmonyLookupService(Execute);
            ProcessResult result = await service.LaunchAppAsync("hdc-1", new AppInfo
            {
                Platform = "harmony",
                BundleId = "com.example.native",
                HarmonyUserId = 100,
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo
                    {
                        Module = "entry",
                        Ability = "MainAbility",
                        IsUiEntry = true,
                        HarmonyUserId = 100
                    }
                }
            }, CancellationToken.None);

            Assert.Contains("aa start -u 100 -b com.example.native -m entry -a MainAbility", commands);
            Assert.DoesNotContain(commands,
                command => command == "aa start -b com.example.native -m entry -a MainAbility");
            Assert.DoesNotContain(commands,
                command => command.StartsWith("aa start -b ", StringComparison.Ordinal));
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("停止无范围重试", result.Stderr);
        }
    }
}
