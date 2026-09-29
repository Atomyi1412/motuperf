using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyIconHydrationTests : IDisposable
    {
        private const string Bundle = "com.example.icons";
        private readonly string _serial = "icon-tests-" + Guid.NewGuid().ToString("N");
        private readonly List<string> _cacheFiles = new List<string>();

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CachedIconsRemainWithTheirBundleAndUserInEitherOrder(bool reverse)
        {
            int[] users = { 0, 100, -1 };
            var paths = users.ToDictionary(user => user, user => WriteCachedIcon(user));
            var apps = users.Select(user => App(user)).ToList();
            apps.Add(App(100));
            var otherPlatformApp = App(0);
            otherPlatformApp.Platform = "android";
            otherPlatformApp.IconPath = "android-app-icon";
            apps.Add(otherPlatformApp);
            apps.Add(null);
            if (reverse) apps.Reverse();
            var processes = users.Select(user => Process(user)).ToList();
            var unmatched = Process(101);
            processes.Add(unmatched);
            var otherBundle = Process(0);
            otherBundle.BundleId = "com.example.other";
            processes.Add(otherBundle);
            var otherPlatform = Process(0);
            otherPlatform.Platform = "android";
            otherPlatform.IconPath = "android-process-icon";
            processes.Add(otherPlatform);
            processes.Add(null);
            var commandLog = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commandLog.Add(command);
                return Task.FromResult(new ProcessResult(1, "", "Permission denied"));
            });

            await service.HydrateAppIconsAsync(_serial, apps, processes, 1, CancellationToken.None);

            Assert.Empty(commandLog);
            Assert.All(apps.Where(app => app != null && app.Platform == "harmony"),
                app => Assert.Equal(paths[app.HarmonyUserId], app.IconPath));
            foreach (int user in users)
                Assert.Equal(paths[user], processes.Single(process => process != null
                    && process.Platform == "harmony" && process.BundleId == Bundle
                    && process.HarmonyUserId == user).IconPath);
            Assert.Empty(unmatched.IconPath);
            Assert.Empty(otherBundle.IconPath);
            Assert.Equal("android-app-icon", otherPlatformApp.IconPath);
            Assert.Equal("android-process-icon", otherPlatform.IconPath);
        }

        [Theory]
        [InlineData(0, 100)]
        [InlineData(100, -1)]
        [InlineData(-1, 0)]
        public async Task MissingUserIconDoesNotInheritAnotherUsersCache(int cachedUser, int missingUser)
        {
            string path = WriteCachedIcon(cachedUser);
            AppInfo cached = App(cachedUser);
            AppInfo missing = App(missingUser);
            ProcessInfo process = Process(missingUser);
            var service = DeniedService(new List<string[]>());

            await service.HydrateAppIconsAsync(_serial, new[] { cached, missing },
                new[] { process }, 1, CancellationToken.None);

            Assert.Equal(path, cached.IconPath);
            Assert.Empty(missing.IconPath);
            Assert.Empty(process.IconPath);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task FailedExtractionsConsumeBudgetButCachedAndDuplicateRowsDoNot(int limit)
        {
            WriteCachedIcon(0);
            var commands = new List<string[]>();
            var service = DeniedService(commands);
            var apps = new List<AppInfo> { null, App(0), App(0), App(100), App(100), App(101), App(102) };
            apps[1].Recommended = true;
            int count = apps.Count;

            await service.HydrateAppIconsAsync(_serial, apps, null, limit, CancellationToken.None);

            Assert.Equal(count, apps.Count);
            Assert.Equal(Enumerable.Range(100, limit), commands.Select(HarmonyLookupService.CommandUserId).Distinct());
            Assert.Equal(limit * 2, commands.Count);
            Assert.All(apps.Where(app => app != null && app.HarmonyUserId > 0), app => Assert.Empty(app.IconPath));
        }

        [Fact]
        public async Task FailedDuplicateBundleIsRetriedForEachDistinctUserOnlyOnce()
        {
            var commands = new List<string[]>();
            var service = DeniedService(commands);
            var apps = new[] { App(-1), App(-1), App(0), App(0), App(100), App(100) };

            await service.HydrateAppIconsAsync(_serial, apps, null, 10, CancellationToken.None);

            Assert.Equal(new[] { -1, 0, 100 }, commands.Select(HarmonyLookupService.CommandUserId).Distinct());
            Assert.Equal(5, commands.Count);
            Assert.All(apps, app => Assert.Empty(app.IconPath));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailuresAcrossDifferentBundlesStillStopAtTheAttemptLimit(bool throws)
        {
            var apps = Enumerable.Range(0, 10).Select(index => new AppInfo
            {
                Platform = "harmony", HarmonyUserId = 100,
                BundleId = "com.example.app" + index, Recommended = index == 9
            }).ToList();
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (throws) throw new IOException("Unavailable metadata");
                return Task.FromResult(new ProcessResult(1, "", "Permission denied"));
            });

            await service.HydrateAppIconsAsync(_serial, apps, null, 2, CancellationToken.None);

            Assert.Equal(new[] { "com.example.app9", "com.example.app0" },
                commands.Select(command => command[3]).Distinct());
            Assert.Equal(4, commands.Count);
            Assert.Equal(10, apps.Count);
            Assert.All(apps, app => Assert.Empty(app.IconPath));
        }

        [Fact]
        public async Task UnknownUserSentinelsShareOnlyTheUnknownCache()
        {
            string path = WriteCachedIcon(-1);
            var apps = new[] { App(-1), App(-2) };
            var process = Process(-2);
            var commands = new List<string[]>();

            await DeniedService(commands).HydrateAppIconsAsync(_serial, apps, new[] { process }, 1, CancellationToken.None);

            Assert.Empty(commands);
            Assert.All(apps, app => Assert.Equal(path, app.IconPath));
            Assert.Equal(path, process.IconPath);
        }

        [Fact]
        public async Task CacheFromAnotherDeviceCannotSupplyTheIcon()
        {
            WriteCachedIcon(0);
            var app = App(0);
            var process = Process(0);
            await DeniedService(new List<string[]>()).HydrateAppIconsAsync(_serial + "-other",
                new[] { app }, new[] { process }, 1, CancellationToken.None);
            Assert.Empty(app.IconPath);
            Assert.Empty(process.IconPath);
        }

        [Fact]
        public async Task DifferentAppIndexesDoNotShareIcons()
        {
            string indexZeroPath = WriteCachedIcon(0, 0);
            string indexOnePath = WriteCachedIcon(0, 1);
            AppInfo indexZero = App(0, 0);
            AppInfo indexOne = App(0, 1);
            ProcessInfo processZero = Process(0, 0);
            ProcessInfo processOne = Process(0, 1);

            await DeniedService(new List<string[]>()).HydrateAppIconsAsync(_serial,
                new[] { indexZero, indexOne }, new[] { processZero, processOne }, 1, CancellationToken.None);

            Assert.Equal(indexZeroPath, indexZero.IconPath);
            Assert.Equal(indexOnePath, indexOne.IconPath);
            Assert.Equal(indexZeroPath, processZero.IconPath);
            Assert.Equal(indexOnePath, processOne.IconPath);
        }

        [Fact]
        public async Task CancellationStopsFurtherOptionalQueries()
        {
            using var cancellation = new CancellationTokenSource();
            int commands = 0;
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands++;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new ProcessResult(1, "", "Permission denied"));
            });
            var apps = new[] { App(0), App(100) };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.HydrateAppIconsAsync(
                _serial, apps, null, 10, cancellation.Token));

            Assert.Equal(1, commands);
            Assert.All(apps, app => Assert.Empty(app.IconPath));
        }

        [Fact]
        public async Task BudgetTimeoutPreservesCachedIconsAndStopsOptionalQueries()
        {
            string cachedPath = WriteCachedIcon(100);
            int commands = 0;
            var service = new HarmonyLookupService(async (serial, command, timeout, token) =>
            {
                commands++;
                await Task.Delay(Timeout.Infinite, token);
                return new ProcessResult(1, "", "Permission denied");
            }, 40, 40);
            var apps = new[] { App(0), App(100) };

            await service.HydrateAppIconsAsync(_serial, apps, null, 10, CancellationToken.None);

            Assert.Equal(1, commands);
            Assert.Empty(apps[0].IconPath);
            Assert.Equal(cachedPath, apps[1].IconPath);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ExpandedProfilesCannotCopyAnotherUsersIcon(int originalUser)
        {
            AppInfo original = App(originalUser);
            original.IconPath = "original-user-icon";
            original.HarmonyUserIds = new List<int> { 0, 100 };

            var expanded = HarmonyLookupService.ExpandHarmonyUserInstances(new[] { original });

            Assert.Equal(2, expanded.Count);
            Assert.All(expanded, app => Assert.Equal(app.HarmonyUserId == originalUser
                ? original.IconPath : "", app.IconPath));
        }

        [Fact]
        public void ResolvingUnknownProfileCannotKeepUnknownUsersIcon()
        {
            AppInfo original = App(-1);
            original.IconPath = "unknown-user-icon";
            original.HarmonyUserIds = new List<int> { 100 };
            AppInfo resolved = Assert.Single(HarmonyLookupService.ExpandHarmonyUserInstances(new[] { original }));
            Assert.Equal(100, resolved.HarmonyUserId);
            Assert.Empty(resolved.IconPath);
        }

        private static AppInfo App(int user, int appIndex = -1)
        {
            return new AppInfo { Platform = "harmony", BundleId = Bundle,
                HarmonyUserId = user, HarmonyAppIndex = appIndex };
        }

        private ProcessInfo Process(int user, int appIndex = -1)
        {
            return new ProcessInfo { Platform = "harmony", DeviceUdid = _serial,
                BundleId = Bundle, HarmonyUserId = user, HarmonyAppIndex = appIndex,
                Pid = user + 1000 + Math.Max(0, appIndex) };
        }

        private static HarmonyLookupService DeniedService(List<string[]> commands)
        {
            return new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                return Task.FromResult(new ProcessResult(1, "", "Permission denied"));
            });
        }

        private string WriteCachedIcon(int user, int appIndex = -1)
        {
            string scope = user < 0 ? "unknown-user" : user.ToString(CultureInfo.InvariantCulture);
            string index = appIndex >= 0 ? "-" + appIndex.ToString(CultureInfo.InvariantCulture) : "";
            string path = Path.Combine(RuntimeTools.DataDirectory, "app-icons", _serial,
                scope + index + "-" + Bundle + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
            _cacheFiles.Add(path);
            return path;
        }

        public void Dispose()
        {
            foreach (string path in _cacheFiles) File.Delete(path);
            if (_cacheFiles.Count > 0) Directory.Delete(Path.GetDirectoryName(_cacheFiles[0]));
        }
    }
}
