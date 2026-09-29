using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyLaunchEntryCaseTests
    {
        private const string Bundle = "com.example.cases";

        private static string Inventory(int user, bool moduleVariant)
        {
            return JsonSerializer.Serialize(new
            {
                bundleName = Bundle,
                userId = user,
                hapModuleInfos = new[]
                {
                    new { moduleName = "entry", mainAbility = "MainAbility" },
                    new { moduleName = moduleVariant ? "Entry" : "entry",
                        mainAbility = moduleVariant ? "MainAbility" : "mainAbility" }
                }
            });
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void JsonInventoryRetainsCaseDistinctEntriesButCoalescesExactDuplicates(bool moduleVariant, bool indented)
        {
            string json = Inventory(100, moduleVariant);
            if (indented)
            {
                using var document = JsonDocument.Parse(json);
                json = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            // Repeated records exercise app-level merging as well as detail parsing.
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(json + "\n" + json, Bundle, 100);
            Assert.Equal(2, entries.Count);
            var app = Assert.Single(HarmonyLookupService.ParseApps(json + "\n" + json));
            Assert.Equal(2, app.HarmonyLaunchEntries.Count);
            Assert.Contains(app.HarmonyLaunchEntries, entry => entry.Module == "entry" && entry.Ability == "MainAbility");
            Assert.Contains(app.HarmonyLaunchEntries, entry => entry.Module == (moduleVariant ? "Entry" : "entry")
                && entry.Ability == (moduleVariant ? "MainAbility" : "mainAbility"));
            Assert.All(app.HarmonyLaunchEntries, entry => Assert.Equal(100, entry.HarmonyUserId));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TextInventoryRetainsCaseDistinctEntries(bool indented)
        {
            string entries = indented
                ? "abilityInfos:\n  - name: MainAbility\n  - name: mainAbility\n  - name: MainAbility\n"
                : "abilityName: MainAbility\nabilityName: mainAbility\nabilityName: MainAbility\n";
            string payload = "bundleName: " + Bundle + "\nuserId: 100\nmoduleName: entry\n" + entries;
            Assert.Equal(2, HarmonyLookupService.ParseLaunchEntryPoints(payload, Bundle, 100).Count);
            Assert.Equal(2, Assert.Single(HarmonyLookupService.ParseApps(payload)).HarmonyLaunchEntries.Count);
        }

        [Fact]
        public void UiAndServiceEntriesWithDifferentCaseKeepTheirOwnClassification()
        {
            string payload = "{\"bundleName\":\"" + Bundle + "\",\"userId\":100,\"moduleName\":\"entry\","
                + "\"abilityInfos\":[{\"name\":\"MainAbility\"}],\"serviceAbilityInfos\":[{\"name\":\"mainAbility\"}]}";
            var app = Assert.Single(HarmonyLookupService.ParseApps(payload));
            Assert.Equal(2, app.HarmonyLaunchEntries.Count);
            Assert.True(Assert.Single(app.HarmonyLaunchEntries, entry => entry.Ability == "MainAbility").IsUiEntry);
            Assert.False(Assert.Single(app.HarmonyLaunchEntries, entry => entry.Ability == "mainAbility").IsUiEntry);
        }

        [Theory]
        [InlineData(0, false, "cached")]
        [InlineData(100, true, "cached")]
        [InlineData(100, false, "inventory")]
        [InlineData(0, true, "inventory")]
        [InlineData(0, false, "detail")]
        [InlineData(100, true, "detail")]
        public async Task RejectedFirstEntryDoesNotHideWorkingCaseDistinctEntry(int user, bool moduleVariant, string source)
        {
            string module = moduleVariant ? "Entry" : "entry";
            string ability = moduleVariant ? "MainAbility" : "mainAbility";
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "acm") return Task.FromResult(new ProcessResult(0, "ID: " + user, ""));
                if (command[0] == "bm" && (source == "detail" || source == "inventory"))
                    return Task.FromResult(new ProcessResult(0, Inventory(user, moduleVariant), ""));
                if (command[0] == "aa" && command[1] == "start")
                {
                    starts.Add(command);
                    bool correct = Value(command, "-m") == module && Value(command, "-a") == ability;
                    return Task.FromResult(new ProcessResult(correct ? 0 : 1, correct ? "Ability started" : "", ""));
                }
                return Task.FromResult(new ProcessResult(1, "", "unsupported test command"));
            });
            AppInfo app = new AppInfo { BundleId = Bundle, Platform = "harmony", HarmonyUserId = user };
            if (source == "cached")
            {
                app.HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo { Module = "entry", Ability = "MainAbility", HarmonyUserId = user },
                    new HarmonyLaunchEntryInfo { Module = module, Ability = ability, HarmonyUserId = user },
                    new HarmonyLaunchEntryInfo { Module = module, Ability = ability, HarmonyUserId = user },
                    new HarmonyLaunchEntryInfo { Module = "foreign", Ability = "ForeignAbility", HarmonyUserId = user + 1 }
                };
            }
            else if (source == "inventory")
            {
                var snapshot = await service.ListTargetsAsync("test-hdc", CancellationToken.None);
                app = Assert.Single(snapshot.Apps);
                Assert.Equal(2, app.HarmonyLaunchEntries.Count);
            }

            var result = await service.LaunchAppAsync("test-hdc", app, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(2, starts.Count);
            Assert.Equal(ability, Value(starts[1], "-a"));
            Assert.Equal(module, Value(starts[1], "-m"));
            Assert.All(starts, command => Assert.Equal(user, HarmonyLookupService.CommandUserId(command)));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(100, false)]
        public async Task ModulelessRetryPreservesCaseDistinctAbilityNames(int user, bool isUiEntry)
        {
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "aa" && command[1] == "start")
                {
                    starts.Add(command);
                    bool correct = !command.Contains("-m") && Value(command, "-a") == "mainAbility";
                    return Task.FromResult(new ProcessResult(correct ? 0 : 1, correct ? "Ability started" : "", ""));
                }
                return Task.FromResult(new ProcessResult(1, "", "unsupported test command"));
            });
            var app = new AppInfo
            {
                BundleId = Bundle, Platform = "harmony", HarmonyUserId = user,
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo { Module = "first", Ability = "MainAbility", HarmonyUserId = user, IsUiEntry = isUiEntry },
                    new HarmonyLaunchEntryInfo { Module = "second", Ability = "mainAbility", HarmonyUserId = user, IsUiEntry = isUiEntry }
                }
            };
            var result = await service.LaunchAppAsync("test-hdc", app, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(4, starts.Count);
            Assert.Contains(starts, command => !command.Contains("-m") && Value(command, "-a") == "mainAbility");
            Assert.All(starts, command => Assert.Equal(user, HarmonyLookupService.CommandUserId(command)));
        }

        private static string Value(string[] command, string option)
        {
            int index = Array.IndexOf(command, option);
            return index >= 0 && index + 1 < command.Length ? command[index + 1] : "";
        }
    }
}
