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
    public sealed class HarmonyLaunchDetailScopeTests
    {
        private const string Bundle = "com.example.selected";

        private static string Detail(string bundle, int user, string ability, bool json)
        {
            if (json)
                return JsonSerializer.Serialize(new { bundleName = bundle, userId = user,
                    moduleName = "entry", abilityInfos = new[] { new { name = ability } } });
            // Scope after the entry must still apply to the entire record.
            return "bundleName: " + bundle + "\nmoduleName: entry\nabilityName: " + ability
                + "\nuserId: " + user + "\n";
        }

        [Theory]
        [InlineData(0, true, true)]
        [InlineData(100, true, true)]
        [InlineData(0, false, true)]
        [InlineData(100, false, true)]
        [InlineData(0, true, false)]
        [InlineData(100, true, false)]
        [InlineData(0, false, false)]
        [InlineData(100, false, false)]
        public async Task MismatchingDetailDoesNotHideMatchingAlternateQuery(int selectedUser, bool json, bool wrongUser)
        {
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (command[0] == "bm")
                    return Task.FromResult(new ProcessResult(0, command.Contains("--user-id")
                        ? Detail(Bundle, selectedUser, "SelectedAbility", json)
                        : Detail(wrongUser ? Bundle : "com.example.other", wrongUser ? 101 : selectedUser,
                            "WrongAbility", json), ""));
                return Task.FromResult(new ProcessResult(0, "Ability started", ""));
            });

            var result = await service.LaunchAppAsync("test-hdc", Bundle, new[] { selectedUser }, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            var start = Assert.Single(commands, command => command[0] == "aa");
            Assert.Contains("SelectedAbility", start);
            Assert.DoesNotContain(commands, command => command.Contains("WrongAbility"));
            Assert.Contains(commands, command => command[0] == "bm" && command.Contains("--user-id"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AggregateDetailsKeepMatchingBundleAndUser(bool json)
        {
            string wrongUser = Detail(Bundle, 0, "OwnerAbility", json);
            string wrongBundle = Detail("com.example.other", 100, "OtherAbility", json);
            string selected = Detail(Bundle, 100, "SelectedAbility", json);
            string payload = json ? "{\"bundleInfos\":[" + wrongUser + "," + wrongBundle + "," + selected + "]}"
                : wrongUser + wrongBundle + selected;
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "bm") return Task.FromResult(new ProcessResult(0, payload, ""));
                starts.Add(command);
                return Task.FromResult(new ProcessResult(0, "Ability started", ""));
            });

            var result = await service.LaunchAppAsync("test-hdc", Bundle, new[] { 100 }, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("SelectedAbility", Assert.Single(starts));
        }

        [Theory]
        [InlineData("{\"userId\":0,\"moduleName\":\"entry\",\"abilityName\":\"WrongAbility\"}")]
        [InlineData("{\"applicationInfo\":{\"uid\":12345},\"moduleName\":\"entry\",\"abilityName\":\"WrongAbility\"}")]
        [InlineData("userId: 0\nmoduleName: entry\nabilityName: WrongAbility\n")]
        [InlineData("moduleName: entry\nabilityName: WrongAbility\nuid: 12345\n")]
        [InlineData("userId: 0\nbundleName: com.example.selected\nmoduleName: entry\nabilityName: WrongAbility\n")]
        [InlineData("bundleName: com.example.selected\napplicationInfo:\n  userId: 0\nmoduleName: entry\nabilityName: WrongAbility\n")]
        [InlineData("bundleName: com.example.selected\nuserId: \nmoduleName: entry\nabilityName: WrongAbility\n")]
        [InlineData("{\"bundleName\":\"com.example.selected\",\"userId\":100,\"applicationInfo\":{\"uid\":12345},\"abilityName\":\"WrongAbility\"}")]
        public async Task AnonymousDetailsCannotOverrideExplicitWrongAccount(string payload)
        {
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "bm") return Task.FromResult(new ProcessResult(0, payload, ""));
                starts.Add(command);
                return Task.FromResult(new ProcessResult(1, "", "launch rejected"));
            });

            var result = await service.LaunchAppAsync("test-hdc", Bundle, new[] { 100 }, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain(starts, command => command.Contains("WrongAbility"));
            Assert.All(starts, command => Assert.Equal(100, HarmonyLookupService.CommandUserId(command)));
        }

        [Theory]
        [InlineData("{\"bundleInfo\":{\"applicationInfo\":{\"bundleName\":\"com.example.other\"},\"moduleName\":\"entry\",\"abilityName\":\"WrongAbility\"}}")]
        [InlineData("{\"bundleName\":\"com.example.selected\",\"launcherAbilityInfo\":{\"abilityName\":\"WrongAbility\",\"elementName\":{\"bundleName\":\"com.example.other\"}}}")]
        [InlineData("{\"bundleName\":\"com.example.selected\",\"targetAbilityInfo\":{\"abilityName\":\"WrongAbility\",\"targetInfo\":{\"bundleName\":\"com.example.other\"}}}")]
        public void NestedOfficialAbilityIdentityCannotCrossBundleScope(string payload)
        {
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(payload, Bundle, 100);

            Assert.DoesNotContain(entries, entry => entry.Ability == "WrongAbility");
        }

        [Fact]
        public void RealHarmonyBundleDetailAppIdentifiersDoNotHideAbilityEntries()
        {
            // Harmony's real bm dump includes a signature-derived appId and a
            // numeric appIdentifier beside the actual bundleName. Neither is
            // the selected Bundle identity and they must not reject the
            // abilityInfos that provide the safe launch entry.
            string payload = "{\"appId\":\"com.example.selected_BENsignature\","
                + "\"appIdentifier\":\"5765880207854109023\","
                + "\"bundleName\":\"com.example.selected\","
                + "\"hapModuleInfos\":[{\"moduleName\":\"entry\","
                + "\"abilityInfos\":[{\"name\":\"MainAbility\",\"type\":1}]}]}";

            var entry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(payload, Bundle, 100));
            Assert.Equal("entry", entry.Module);
            Assert.Equal("MainAbility", entry.Ability);
            Assert.True(entry.IsUiEntry);
        }

        [Theory]
        [InlineData("moduleName: entry\nabilityName: SelectedAbility\n")]
        [InlineData("{\"moduleName\":\"entry\",\"abilityName\":\"SelectedAbility\"}")]
        [InlineData("{\"applicationInfo\":{\"uid\":20010123},\"moduleName\":\"entry\",\"abilityName\":\"SelectedAbility\"}")]
        [InlineData("{\"bundleName\":\"com.example.selected\",\"abilityInfos\":[{\"name\":\"SelectedAbility\",\"userId\":100,\"moduleName\":\"entry\"}]}")]
        [InlineData("{\"bundleInfos\":{\"com.example.other\":{\"mainAbility\":\"WrongAbility\"},\"com.example.selected\":{\"mainAbility\":\"SelectedAbility\"}}}")]
        [InlineData("userId: 100\nbundleName: com.example.selected\nmoduleName: entry\nabilityName: SelectedAbility\n")]
        [InlineData("bundleName: com.example.selected\napplicationInfo:\n  uid: 20010123\nmoduleName: entry\nabilityName: SelectedAbility\n")]
        [InlineData("bundleName: com.example.selected\nuserId: 100\nmoduleName: entry\nabilityInfos:\n  - name: SelectedAbility\n    metadata:\n      userId: 0\n      bundleName: com.example.analytics\n")]
        [InlineData("bundleName: com.example.selected\nuserId: 100\nmoduleName: entry\nmetadata:\n  userId: 0\n  abilityName: FakeAbility\nabilityName: SelectedAbility\n")]
        [InlineData("{\"bundleName\":\"com.example.selected\",\"userId\":100,\"metadata\":{\"abilityName\":\"FakeAbility\"},\"moduleName\":\"entry\",\"abilityName\":\"SelectedAbility\"}")]
        public async Task AnonymousScopedDetailsRemainUsable(string payload)
        {
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "bm") return Task.FromResult(new ProcessResult(0, payload, ""));
                starts.Add(command);
                return Task.FromResult(new ProcessResult(0, "Ability started", ""));
            });
            var result = await service.LaunchAppAsync("test-hdc", Bundle, new[] { 100 }, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("SelectedAbility", Assert.Single(starts));
        }

        [Fact]
        public void MixedPayloadKeepsEveryMatchingEntryAndIgnoresMetadataIdentity()
        {
            string payload = "{\"bundleName\":\"com.example.other\",\"mainAbility\":\"WrongAbility\"}\n"
                + "{\"bundleName\":\"com.example.selected\",\"userId\":100,\"moduleName\":\"entry\","
                + "\"metadata\":{\"abilityName\":\"FakeAbility\"},\"abilityInfos\":[{\"name\":\"FirstAbility\"},{\"name\":\"SecondAbility\"}]}\n"
                + "bundleName: com.example.selected\nuserId: 100\nmoduleName: extra\nabilityName: ThirdAbility\n";
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(payload, Bundle, 100);
            Assert.Equal(new[] { "extra/ThirdAbility", "entry/FirstAbility", "entry/SecondAbility" }.OrderBy(value => value),
                entries.Select(entry => entry.Module + "/" + entry.Ability).OrderBy(value => value));
        }

        [Fact]
        public async Task TruncatedFailureDiagnosticsNeverBecomeAnonymousLaunchEntries()
        {
            string payload = "moduleName: entry\nabilityName: WrongAbility\nlabel: "
                + new string('x', 5000) + "\nuserId: 0\n";
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "bm") return Task.FromResult(new ProcessResult(0, payload, ""));
                starts.Add(command);
                return Task.FromResult(new ProcessResult(1, "", "launch rejected"));
            });
            await service.LaunchAppAsync("test-hdc", Bundle, new[] { 100 }, CancellationToken.None);
            Assert.DoesNotContain(starts, command => command.Contains("WrongAbility"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task OtherUsersUiEntryCannotEnableFallbackForSelectedBackgroundAbility(bool json)
        {
            string other = Detail(Bundle, 0, "OtherUiAbility", json);
            string selected = json
                ? "{\"bundleName\":\"com.example.selected\",\"userId\":100,\"moduleName\":\"service\",\"serviceAbilityInfos\":[{\"name\":\"SyncService\"}]}"
                : "bundleName: com.example.selected\nuserId: 100\nmoduleName: service\nserviceAbilityInfos:\n  - name: SyncService\n";
            var starts = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                if (command[0] == "bm") return Task.FromResult(new ProcessResult(0, other + "\n" + selected, ""));
                starts.Add(command);
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });
            await service.LaunchAppAsync("test-hdc", Bundle, new[] { 100 }, CancellationToken.None);
            Assert.NotEmpty(starts);
            Assert.All(starts, command =>
            {
                Assert.Equal("aa", command[0]);
                Assert.Contains("SyncService", command);
            });
        }
    }
}
