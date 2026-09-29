using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyScalarInventoryScopeTests
    {
        [Theory]
        [InlineData("launcher", 0, 101)]
        [InlineData("systemui", 100, 101)]
        [InlineData("com.example.shared", 101, 100)]
        public void ScalarsAndObjectsKeepTheSameEnclosingUserScope(string bundle, int user, int childUser)
        {
            string scalarJson = "{\"userId\":" + user + ",\"bundles\":[\"" + bundle + "\"]}";
            string objectJson = "{\"userId\":" + user + ",\"bundles\":[{\"bundleName\":\"" + bundle + "\"}]}";
            AppInfo scalar = Assert.Single(HarmonyLookupService.ParseApps(scalarJson));
            AppInfo record = Assert.Single(HarmonyLookupService.ParseApps(objectJson));

            Assert.Equal(user, scalar.HarmonyUserId);
            Assert.Equal(record.HarmonyUserIds, scalar.HarmonyUserIds);
            Assert.True(scalar.CanAttemptLaunch);
            Assert.False(scalar.IsRunning);
            Assert.Equal(0, scalar.ProcessPid);
            Assert.Empty(scalar.HarmonyLaunchEntries);

            string nested = "{\"userId\":" + user + ",\"bundles\":[\"" + bundle
                + "\",{\"userId\":" + childUser + ",\"installedBundles\":[\"" + bundle + "\"]}]}";
            var profiles = HarmonyLookupService.ExpandHarmonyUserInstances(HarmonyLookupService.ParseApps(nested));
            Assert.Equal(new[] { user, childUser }.OrderBy(id => id), profiles.Select(app => app.HarmonyUserId));
            Assert.All(profiles, app => Assert.Equal(new[] { app.HarmonyUserId }, app.HarmonyUserIds));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ScalarProfilesSurviveInventoryPidBindingAndSelectedUserLaunch(bool appsOnly, bool reverseStreams)
        {
            string first = "{\"userId\":100,\"bundleInfos\":[\"com.example.shared\"]}";
            string second = "{\"userId\":101,\"bundles\":[\"com.example.shared\",\"launcher\"]}";
            var calls = new List<string>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                string key = string.Join(" ", command);
                calls.Add(key);
                // The payload's explicit profiles must outrank this query scope.
                if (key == "acm dump -a") return Task.FromResult(new ProcessResult(0, "ID: 0", ""));
                if (key.StartsWith("bm dump -a", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        reverseStreams ? second : first, reverseStreams ? first : second));
                if (command[0] == "ps") return Task.FromResult(new ProcessResult(0,
                    "USERID PID BUNDLE_NAME NAME\n100 42 com.example.shared first-game\n101 43 com.example.shared second-game\n", ""));
                if (key.StartsWith("aa start -u ", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessResult(0, "Ability started", ""));
                return Task.FromResult(new ProcessResult(1, "", "permission denied"));
            });

            var apps = appsOnly ? await service.ListAppsAsync("HDC-1", CancellationToken.None)
                : (await service.ListTargetsAsync("HDC-1", CancellationToken.None)).Apps;
            Assert.Equal(3, apps.Count);
            Assert.DoesNotContain(apps, app => app.HarmonyUserId < 0 || app.HarmonyUserId == 0);
            var shared = apps.Where(app => app.BundleId == "com.example.shared").OrderBy(app => app.HarmonyUserId).ToList();
            Assert.Equal(new[] { 100, 101 }, shared.Select(app => app.HarmonyUserId));
            Assert.Equal(new[] { 42, 43 }, shared.Select(app => app.ProcessPid));
            Assert.All(shared, app => Assert.True(app.IsRunning));
            AppInfo stopped = Assert.Single(apps, app => app.BundleId == "launcher");
            Assert.Equal(101, stopped.HarmonyUserId);
            Assert.True(stopped.CanAttemptLaunch);
            Assert.False(stopped.IsRunning);
            Assert.Empty(stopped.HarmonyLaunchEntries);
            Assert.Equal(0, stopped.ProcessPid);

            foreach (AppInfo app in apps)
            {
                calls.Clear();
                var result = await service.LaunchAppAsync("HDC-1", app, CancellationToken.None);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("aa start -u " + app.HarmonyUserId + " -b " + app.BundleId,
                    Assert.Single(calls, call => call.StartsWith("aa start", StringComparison.Ordinal)));
            Assert.All(calls, call =>
            {
                Assert.True(
                    call.Contains("-u " + app.HarmonyUserId, StringComparison.Ordinal)
                    || call.Contains("--user-id " + app.HarmonyUserId, StringComparison.Ordinal));
            });
            }
        }

        [Fact]
        public async Task UnscopedScalarCannotInheritASiblingUserOrLaunch()
        {
            var apps = HarmonyLookupService.ParseApps(
                "{\"bundles\":[{\"userId\":100,\"bundles\":[\"owned\"]},\"unknown\"],"
                + "\"metadata\":{\"userId\":101,\"label\":\"not_an_app\"}}");
            Assert.Equal(2, apps.Count);
            Assert.Equal(100, Assert.Single(apps, app => app.BundleId == "owned").HarmonyUserId);
            AppInfo unknown = Assert.Single(apps, app => app.BundleId == "unknown");
            Assert.Equal(-1, unknown.HarmonyUserId);
            Assert.Empty(unknown.HarmonyUserIds);
            Assert.False(unknown.CanAttemptLaunch);
            int calls = 0;
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                calls++;
                return Task.FromResult(new ProcessResult(0, "unexpected command", ""));
            });
            Assert.NotEqual(0, (await service.LaunchAppAsync("HDC-1", unknown, CancellationToken.None)).ExitCode);
            Assert.Equal(0, calls);
        }
    }
}
