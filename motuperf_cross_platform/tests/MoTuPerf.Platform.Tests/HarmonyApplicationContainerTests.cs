using System.Linq;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyApplicationContainerTests
    {
        [Fact]
        public void RecognizesVendorApplicationContainersAndKeepsEnclosingUserScope()
        {
            string output = "{\"userId\":100,"
                + "\"applications\":[\"launcher\"],"
                + "\"applicationInfos\":{\"systemui\":{\"versionName\":\"1.0\"},"
                + "\"com.example.game\":{\"versionCode\":2}},"
                + "\"packageList\":[\"com.example.compat\"],"
                + "\"metadata\":{\"packages\":[\"com.example.metadata\"]}}";

            var apps = HarmonyLookupService.ParseApps(output);

            Assert.Equal(
                new[] { "launcher", "systemui", "com.example.game", "com.example.compat" },
                apps.Select(app => app.BundleId));
            Assert.All(apps, app => Assert.Equal(100, app.HarmonyUserId));
            Assert.DoesNotContain(apps, app => app.BundleId == "com.example.metadata");
        }

        [Fact]
        public void RecognizesEmptyObjectKeysInsideApplicationInfoMap()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"userId\":100,\"applicationInfos\":{\"launcher\":{},\"com.example.game\":{}}}");

            Assert.Equal(new[] { "launcher", "com.example.game" }, apps.Select(app => app.BundleId));
            Assert.All(apps, app => Assert.Equal(100, app.HarmonyUserId));
        }

        [Fact]
        public void RecognizesSingularApplicationAndPackageObjectMaps()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"userId\":100,"
                + "\"application\":{\"launcher\":{},\"com.example.game\":{\"versionName\":\"1.0\"}},"
                + "\"package\":{\"systemui\":{}}}");

            Assert.Equal(
                new[] { "launcher", "com.example.game", "systemui" },
                apps.Select(app => app.BundleId));
            Assert.All(apps, app => Assert.Equal(100, app.HarmonyUserId));
            Assert.Equal("1.0", apps.Single(app => app.BundleId == "com.example.game").Version);
        }

        [Fact]
        public void RecognizesScalarApplicationListItemsWithoutReadingNestedLists()
        {
            var apps = HarmonyLookupService.ParseApps(
                "applications:\n"
                + "  - launcher\n"
                + "  - com.example.game\n"
                + "    abilityInfos:\n"
                + "      - com.example.fake.ability\n");

            Assert.Equal(new[] { "launcher", "com.example.game" }, apps.Select(app => app.BundleId));
            Assert.DoesNotContain(apps, app => app.BundleId == "com.example.fake.ability");
        }

        [Fact]
        public void DoesNotReadNestedListsFromApplicationObjectMapsAsApplications()
        {
            var apps = HarmonyLookupService.ParseApps(
                "applicationInfos:\n"
                + "  launcher:\n"
                + "    permissions:\n"
                + "      - com.example.fake\n");

            Assert.Empty(apps);
        }

        [Fact]
        public void DoesNotReadNestedListsBeforeTheBundleFieldAsApplications()
        {
            var apps = HarmonyLookupService.ParseApps(
                "applications:\n"
                + "  - permissions:\n"
                + "      - com.example.fake\n"
                + "    name: com.example.real\n");

            var app = Assert.Single(apps);
            Assert.Equal("com.example.real", app.BundleId);
            Assert.DoesNotContain(apps, candidate => candidate.BundleId == "com.example.fake");
        }

        [Fact]
        public void DoesNotTreatEmptyKeysInsideOrdinaryApplicationInfoAsApplications()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"applicationInfo\":{\"launcher\":{},\"com.example.fake\":{}}}");

            Assert.Empty(apps);
        }

        [Fact]
        public void KeepsMetadataArraysOutsideExplicitApplicationContainers()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"metadata\":{\"applications\":[\"com.example.fake\"],"
                + "\"applicationInfos\":{\"com.example.fake2\":{\"versionName\":\"1\"}},"
                + "\"packages\":[\"com.example.fake3\"]}}" );

            Assert.Empty(apps);
        }

        [Fact]
        public void KeepsMetadataBundleObjectsOutsideApplicationDiscovery()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"applications\":[{\"bundleName\":\"com.example.real\","
                + "\"metadata\":{\"bundleName\":\"com.example.fake\","
                + "\"versionName\":\"2.0\","
                + "\"abilityInfos\":[{\"bundleName\":\"com.example.fake.ability\","
                + "\"name\":\"FakeAbility\"}]}}]}" );

            var app = Assert.Single(apps);
            Assert.Equal("com.example.real", app.BundleId);
            Assert.DoesNotContain(apps, candidate => candidate.BundleId.StartsWith("com.example.fake", System.StringComparison.Ordinal));
            Assert.DoesNotContain(app.HarmonyLaunchEntries ?? new System.Collections.Generic.List<HarmonyLaunchEntryInfo>(),
                entry => entry.Ability == "FakeAbility");
        }

        [Fact]
        public void KeepsIndentedMetadataBundleObjectsOutsideApplicationDiscovery()
        {
            var apps = HarmonyLookupService.ParseApps(
                "applications:\n"
                + "  - name: com.example.real\n"
                + "    metadata:\n"
                + "      bundleName: com.example.fake\n"
                + "      versionName: 2.0\n"
                + "      abilityInfos:\n"
                + "        - name: FakeAbility\n");

            var app = Assert.Single(apps);
            Assert.Equal("com.example.real", app.BundleId);
            Assert.DoesNotContain(apps, candidate => candidate.BundleId == "com.example.fake");
            Assert.DoesNotContain(app.HarmonyLaunchEntries ?? new System.Collections.Generic.List<HarmonyLaunchEntryInfo>(),
                entry => entry.Ability == "FakeAbility");
        }

        [Fact]
        public void RecognizesApplicationContainerNamesInIndentedText()
        {
            var apps = HarmonyLookupService.ParseApps(
                "applications:\n"
                + "  - name: launcher\n"
                + "    versionName: 1.0\n"
                + "packageList:\n"
                + "  - name: systemui\n");

            Assert.Equal(new[] { "launcher", "systemui" }, apps.Select(app => app.BundleId));
        }

    }
}
