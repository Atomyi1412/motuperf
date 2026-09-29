using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyCommandScopeTests
    {
        [Theory]
        [InlineData("aa start -u 100 -b com.example.game", 100)]
        [InlineData("aa start -U 100 -b com.example.game", -1)]
        [InlineData("aa start --URI 100 -b com.example.game", -1)]
        [InlineData("aa start --user 100 -b com.example.game", -1)]
        [InlineData("aa start --user-id 100 -b com.example.game", -1)]
        [InlineData("aa start --userId 100 -b com.example.game", -1)]
        [InlineData("aa start -U 99 -u 0 -b com.example.game", 0)]
        [InlineData("aa dump -r -u 100", 100)]
        [InlineData("aa dump -r --userId 100", 100)]
        [InlineData("aa dump -r --userid 100", -1)]
        [InlineData("aa dump -r --user-id 100", -1)]
        [InlineData("aa dump -r -U 100", -1)]
        [InlineData("bm dump -a -u 100", 100)]
        [InlineData("bm dump -a --user-id 100", 100)]
        [InlineData("bm dump -a --user 100", -1)]
        [InlineData("bm dump -a -U 100", -1)]
        [InlineData("bm dump -a --USER-ID 100", -1)]
        [InlineData("pm list packages -u 100", -1)]
        [InlineData("cmd package list packages -U 100", -1)]
        [InlineData("pm list packages --user 100", 100)]
        [InlineData("cmd package resolve-activity --brief --user 100 com.example.game", 100)]
        [InlineData("am start --user 100 -p com.example.game", 100)]
        [InlineData("am start -u 100 -p com.example.game", -1)]
        [InlineData("monkey --user 100 -p com.example.game 1", -1)]
        [InlineData("unknown -u 100", -1)]
        [InlineData("aa start -u -1 -b com.example.game", -1)]
        [InlineData("aa start -u invalid -b com.example.game", -1)]
        [InlineData("aa start -u", -1)]
        public void UserSelectorDependsOnCommandFamilyAndExactCase(string command, int expected)
        {
            Assert.Equal(expected, HarmonyLookupService.CommandUserId(command.Split(' ')));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(100, true)]
        [InlineData(0, false)]
        [InlineData(100, false)]
        public async Task SuccessfulNativeStartActuallyUsesSelectedAccount(int selectedUser, bool cachedAbility)
        {
            int launchedUser = -1;
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command.Take(2).SequenceEqual(new[] { "bm", "dump" }))
                {
                    if (string.Join(" ", command) == "bm dump -n com.example.game --user-id " + selectedUser)
                        return Task.FromResult(new ProcessResult(0,
                            "moduleName: entry\nabilityName: EntryAbility\n", ""));
                    return Task.FromResult(new ProcessResult(1, "", "unsupported option"));
                }
                if (command.Take(2).SequenceEqual(new[] { "aa", "start" }))
                {
                    starts.Add(command);
                    // Official aa accepts -U as URI and succeeds in the
                    // foreground account if lower-case -u was not supplied.
                    int index = Array.IndexOf(command, "-u");
                    launchedUser = index >= 0
                        ? int.Parse(command[index + 1], CultureInfo.InvariantCulture) : 101;
                    return Task.FromResult(new ProcessResult(0, "start ability successfully.", ""));
                }
                return Task.FromResult(new ProcessResult(1, "", "not supported"));
            });
            var app = new AppInfo
            {
                Platform = "harmony", BundleId = "com.example.game", HarmonyUserId = selectedUser
            };
            if (cachedAbility)
                app.HarmonyLaunchEntries.Add(new HarmonyLaunchEntryInfo
                    { Module = "entry", Ability = "EntryAbility", HarmonyUserId = selectedUser });

            var result = await service.LaunchAppAsync("test-hdc", app, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(selectedUser, launchedUser);
            string[] start = Assert.Single(starts);
            Assert.Contains("EntryAbility", start);
            Assert.DoesNotContain("-U", start);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(100)]
        public async Task RejectedStartsDoNotInvokeMonkeyOrLoseScope(int selectedUser)
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                // An accidentally invoked Monkey would launch the foreground
                // user's package. It has no documented --user option.
                return Task.FromResult(command[0] == "monkey"
                    ? new ProcessResult(0, "Events injected: 1", "")
                    : new ProcessResult(1, "", "permission denied"));
            });

            var result = await service.LaunchAppAsync("test-hdc", new AppInfo
            {
                Platform = "harmony", BundleId = "com.example.game", HarmonyUserId = selectedUser
            }, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain(commands, command => command[0] == "monkey");
            Assert.Contains(commands, command => command[0] == "am");
            Assert.All(commands, command => Assert.Equal(selectedUser, HarmonyLookupService.CommandUserId(command)));
        }

        [Fact]
        public void BuildHarmonyAppIndexStartArgsKeepsProfileAndCloneScope()
        {
            string[] command = HarmonyLookupService.BuildHarmonyAppIndexStartArgs(
                "com.example.game", "entry", "CloneAbility", 100, "--app-index", 2);

            Assert.Equal(
                new[]
                {
                    "aa", "start", "-u", "100", "-b", "com.example.game",
                    "-m", "entry", "-a", "CloneAbility", "--app-index", "2"
                },
                command);
        }

        [Fact]
        public async Task CloneStartUsesAdvertisedAppIndexAndMatchingAbility()
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (command.SequenceEqual(new[] { "aa", "start", "--help" }))
                    return Task.FromResult(new ProcessResult(0, "Options: --app-index <index>\n", ""));
                if (command.SequenceEqual(new[] { "aa", "help", "start" }))
                    return Task.FromResult(new ProcessResult(1, "", "unsupported"));
                if (command.Contains("--app-index") && command.Contains("CloneAbility"))
                    return Task.FromResult(new ProcessResult(0, "start ability successfully.", ""));
                return Task.FromResult(new ProcessResult(1, "", "unexpected command"));
            });

            var result = await service.LaunchAppAsync("test-hdc", new AppInfo
            {
                Platform = "harmony",
                BundleId = "com.example.game",
                HarmonyUserId = 100,
                HarmonyAppIndex = 2,
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo
                    {
                        Module = "entry",
                        Ability = "CloneAbility",
                        HarmonyUserId = 100,
                        HarmonyAppIndex = 2
                    },
                    new HarmonyLaunchEntryInfo
                    {
                        Module = "entry",
                        Ability = "PrimaryAbility",
                        HarmonyUserId = 100,
                        HarmonyAppIndex = 0
                    }
                }
            }, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(commands, command => command.Contains("--app-index")
                && command.Contains("2") && command.Contains("CloneAbility"));
            Assert.DoesNotContain(commands, command => command.Contains("PrimaryAbility"));
            Assert.DoesNotContain(commands, command => command[0] == "am");
        }

        [Fact]
        public async Task CloneStartWithoutAdvertisedOptionDoesNotFallbackToPrimaryOrAndroid()
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (command.SequenceEqual(new[] { "aa", "start", "--help" }))
                    return Task.FromResult(new ProcessResult(0, "unknown option --app-index\n", ""));
                if (command.SequenceEqual(new[] { "aa", "help", "start" }))
                    return Task.FromResult(new ProcessResult(0, "unrecognized option --app-clone-index\n", ""));
                return Task.FromResult(new ProcessResult(1, "", "unexpected launch command"));
            });

            var result = await service.LaunchAppAsync("test-hdc", new AppInfo
            {
                Platform = "harmony",
                BundleId = "com.example.game",
                HarmonyUserId = 100,
                HarmonyAppIndex = 2,
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo
                    {
                        Module = "entry",
                        Ability = "CloneAbility",
                        HarmonyUserId = 100,
                        HarmonyAppIndex = 2
                    }
                }
            }, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("appIndex=2", result.Stderr);
            Assert.DoesNotContain(commands, command => command[0] == "am");
            Assert.DoesNotContain(commands, command => command[0] == "pm");
            Assert.DoesNotContain(commands, command => command[0] == "cmd");
            Assert.DoesNotContain(commands, command => command[0] == "aa"
                && command.Length > 2
                && command[1] == "start"
                && !command.Contains("--help"));
        }

        [Theory]
        [InlineData("unknown option --app-index <index>\n")]
        [InlineData("unsupported option --app-clone-index\n")]
        [InlineData("unrecognized option --clone-index\n")]
        public void ParseHarmonyAppIndexStartOptionsIgnoresRejectedOptions(string output)
        {
            Assert.Empty(HarmonyLookupService.ParseHarmonyAppIndexStartOptions(output));
        }

        [Fact]
        public void ParseLaunchEntryPointsRequiresExactCloneIndex()
        {
            const string output = "{\"bundleName\":\"com.example.game\",\"abilityInfos\":["
                + "{\"moduleName\":\"entry\",\"name\":\"PrimaryAbility\",\"appIndex\":0},"
                + "{\"moduleName\":\"entry\",\"name\":\"CloneAbility\",\"appIndex\":2}]}";

            List<HarmonyLaunchEntryPoint> entries = HarmonyLookupService.ParseLaunchEntryPoints(
                output, "com.example.game", -1, 2);

            var entry = Assert.Single(entries);
            Assert.Equal("CloneAbility", entry.Ability);
            Assert.Equal(2, entry.HarmonyAppIndex);
        }
    }
}
