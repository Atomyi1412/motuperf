using System.Collections.Generic;
using System.Linq;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyAppIndexTests
    {
        [Fact]
        public void ProcessInventoryKeepsAppIndexOnKeyValueAndColumnRows()
        {
            var keyed = Assert.Single(HarmonyLookupService.ParseProcesses(
                "pid=501 bundleName=com.example.clone userId=100 appIndex=2 cmd=/system/bin/com.example.clone:worker\n",
                "HARMONY-1"));
            Assert.Equal(100, keyed.HarmonyUserId);
            Assert.Equal(2, keyed.HarmonyAppIndex);

            var column = Assert.Single(HarmonyLookupService.ParseProcesses(
                "PID UID APP_INDEX BUNDLE ARGS\n"
                + "502 100 3 com.example.clone /system/bin/com.example.clone:renderer\n",
                "HARMONY-1"));
            Assert.Equal(3, column.HarmonyAppIndex);
            Assert.Equal("com.example.clone", column.BundleId);
        }

        [Fact]
        public void JsonInventoryCarriesNestedAppIndexToTheAppAndAbility()
        {
            var app = Assert.Single(HarmonyLookupService.ParseApps(
                "{\"bundleName\":\"com.example.clone\",\"userId\":100,"
                + "\"applicationInfo\":{\"appIndex\":2},"
                + "\"hapModuleInfos\":[{\"moduleName\":\"entry\","
                + "\"abilityInfos\":[{\"abilityName\":\"EntryAbility\"}]}]}"));

            Assert.Equal(100, app.HarmonyUserId);
            Assert.Equal(2, app.HarmonyAppIndex);
            var entry = Assert.Single(app.HarmonyLaunchEntries);
            Assert.Equal(2, entry.HarmonyAppIndex);
        }

        [Fact]
        public void TextInventoryKeepsSameBundleAppIndexesSeparate()
        {
            var apps = HarmonyLookupService.ParseApps(
                "appIndex: 0\n"
                + "bundleName: com.example.clone\n"
                + "moduleName: entry\n"
                + "abilityInfos:\n"
                + "  - name: MainAbility\n"
                + "appIndex: 1\n"
                + "bundleName: com.example.clone\n"
                + "moduleName: entry\n"
                + "abilityInfos:\n"
                + "  - name: CloneAbility\n");

            Assert.Equal(new[] { 0, 1 }, apps
                .Where(app => app.BundleId == "com.example.clone")
                .Select(app => app.HarmonyAppIndex)
                .OrderBy(index => index));
            Assert.Contains(apps, app => app.HarmonyAppIndex == 0
                && app.HarmonyLaunchEntries.Any(entry => entry.Ability == "MainAbility"));
            Assert.Contains(apps, app => app.HarmonyAppIndex == 1
                && app.HarmonyLaunchEntries.Any(entry => entry.Ability == "CloneAbility"));
        }

        [Fact]
        public void PackageInventoryCarriesAppIndexWithoutMergingClones()
        {
            var records = HarmonyLookupService.ParseAndroidPackageRecordsFromIndependentStreams(
                "package:com.example.clone appIndex=0\n"
                + "package:com.example.clone app_index=1\n",
                "");

            Assert.Equal(new[] { 0, 1 }, records
                .Where(record => record.BundleId == "com.example.clone")
                .Select(record => record.AppIndex)
                .OrderBy(index => index));
        }

        [Fact]
        public void ExpandedUserInstancesKeepEachConcreteAppIndex()
        {
            var source = new AppInfo
            {
                BundleId = "com.example.clone",
                Platform = "harmony",
                HarmonyUserIds = new System.Collections.Generic.List<int> { 0, 100 },
                HarmonyAppIndex = 1
            };

            var expanded = HarmonyLookupService.ExpandHarmonyUserInstances(new[] { source });

            Assert.Equal(2, expanded.Count);
            Assert.All(expanded, app => Assert.Equal(1, app.HarmonyAppIndex));
            Assert.Equal(new[] { 0, 100 }, expanded.Select(app => app.HarmonyUserId).OrderBy(id => id));
        }

        [Fact]
        public void ExpandedCloneInstancesRemainSelectableWhenUserEvidenceIsUnknown()
        {
            var source = new AppInfo
            {
                BundleId = "com.example.unknownclone",
                Platform = "harmony",
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo { Ability = "CloneOne", HarmonyAppIndex = 1 },
                    new HarmonyLaunchEntryInfo { Ability = "CloneTwo", HarmonyAppIndex = 2 }
                }
            };

            var expanded = HarmonyLookupService.ExpandHarmonyUserInstances(new[] { source });

            Assert.Equal(new[] { 1, 2 }, expanded.Select(app => app.HarmonyAppIndex).OrderBy(index => index));
            Assert.All(expanded, app => Assert.Equal(-1, app.HarmonyUserId));
            Assert.Contains(expanded, app => app.HarmonyAppIndex == 1
                && app.HarmonyLaunchEntries.Any(entry => entry.Ability == "CloneOne"));
            Assert.Contains(expanded, app => app.HarmonyAppIndex == 2
                && app.HarmonyLaunchEntries.Any(entry => entry.Ability == "CloneTwo"));
        }

        [Fact]
        public void ExplicitUserAndClonePairsDoNotCreateCrossProfileInstances()
        {
            var source = new AppInfo
            {
                BundleId = "com.example.pairedclone",
                Platform = "harmony",
                HarmonyUserIds = new List<int> { 0, 100 },
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo
                    {
                        Ability = "OwnerClone",
                        HarmonyUserId = 0,
                        HarmonyAppIndex = 1
                    },
                    new HarmonyLaunchEntryInfo
                    {
                        Ability = "WorkClone",
                        HarmonyUserId = 100,
                        HarmonyAppIndex = 2
                    }
                }
            };

            var expanded = HarmonyLookupService.ExpandHarmonyUserInstances(new[] { source });

            Assert.Equal(2, expanded.Count);
            Assert.Contains(expanded, app => app.HarmonyUserId == 0 && app.HarmonyAppIndex == 1);
            Assert.Contains(expanded, app => app.HarmonyUserId == 100 && app.HarmonyAppIndex == 2);
            Assert.DoesNotContain(expanded, app => app.HarmonyUserId == 0 && app.HarmonyAppIndex == 2);
            Assert.DoesNotContain(expanded, app => app.HarmonyUserId == 100 && app.HarmonyAppIndex == 1);
        }

        [Fact]
        public void NonMainCloneDoesNotInheritUnscopedMainAbility()
        {
            var source = new AppInfo
            {
                BundleId = "com.example.cloneentry",
                Platform = "harmony",
                HarmonyUserId = 0,
                HarmonyUserIds = new List<int> { 0 },
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo
                    {
                        Ability = "MainAbility",
                        HarmonyUserId = 0,
                        HarmonyAppIndex = -1
                    },
                    new HarmonyLaunchEntryInfo
                    {
                        Ability = "CloneAbility",
                        HarmonyUserId = 0,
                        HarmonyAppIndex = 2
                    }
                }
            };

            AppInfo clone = Assert.Single(HarmonyLookupService.ExpandHarmonyUserInstances(new[] { source }));

            Assert.Equal(2, clone.HarmonyAppIndex);
            Assert.Contains(clone.HarmonyLaunchEntries, entry => entry.Ability == "CloneAbility");
            Assert.DoesNotContain(clone.HarmonyLaunchEntries, entry => entry.Ability == "MainAbility");
        }

        [Fact]
        public void CloneLaunchCapabilityRequiresMatchingAbilityEvidence()
        {
            var clone = new AppInfo
            {
                BundleId = "com.example.clonecapability",
                Platform = "harmony",
                HarmonyUserId = 0,
                HarmonyAppIndex = 2,
                HarmonyLaunchEntries = new List<HarmonyLaunchEntryInfo>
                {
                    new HarmonyLaunchEntryInfo
                    {
                        Ability = "MainAbility",
                        HarmonyUserId = 0,
                        HarmonyAppIndex = -1
                    }
                }
            };

            Assert.False(clone.CanAttemptLaunch);

            clone.HarmonyLaunchEntries.Add(new HarmonyLaunchEntryInfo
            {
                Ability = "CloneAbility",
                HarmonyUserId = 0,
                HarmonyAppIndex = 2
            });

            Assert.True(clone.CanAttemptLaunch);
        }
    }
}
