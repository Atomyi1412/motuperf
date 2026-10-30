using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using MoTuPerf.Platform;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class RuntimeToolResolverTests
    {
        [Fact]
        public void MacAppExecutableDirectoryResolvesContentsResources()
        {
            string app = Path.Combine(Path.GetTempPath(), "MoTuPerf.app");
            string executableDirectory = Path.Combine(app, "Contents", "MacOS");

            RuntimeToolResolver resolver = new RuntimeToolResolver(executableDirectory, true, "macos", "arm64");

            Assert.Equal(Path.GetFullPath(app), resolver.BundleDirectory);
            Assert.Equal(Path.Combine(app, "Contents", "Resources"), resolver.ResourceDirectory);
            Assert.Equal(Path.Combine(app, "Contents", "Resources", "runtime", "python", "bin", "python3"), resolver.PythonExecutable);
            Assert.Equal(Path.Combine(app, "Contents", "Resources", "runtime", "android", "adb"), resolver.AdbExecutable);
            Assert.Equal("macos-arm64", resolver.DescribeTarget());
            Assert.True(resolver.IsSupportedTarget);
        }

        [Theory]
        [InlineData("macos", "x64")]
        [InlineData("linux", "arm64")]
        [InlineData("windows", "arm64")]
        public void FirstReleaseRejectsUnsupportedTargets(string operatingSystem, string architecture)
        {
            RuntimeToolResolver resolver = new RuntimeToolResolver(Path.GetTempPath(), false, operatingSystem, architecture);

            Assert.False(resolver.IsSupportedTarget);
            Assert.NotEmpty(resolver.ValidatePackagedRuntime());
        }

        [Fact]
        public void DevelopmentBuildDoesNotRequireBundledRuntime()
        {
            RuntimeToolResolver resolver = new RuntimeToolResolver(Path.GetTempPath(), false, "macos", "arm64");

            Assert.Empty(resolver.ValidatePackagedRuntime());
        }

        [Fact]
        public void PythonResolutionRejectsWindowsAppsAliasOverride()
        {
            string alias = Path.Combine(Path.GetTempPath(), "Microsoft", "WindowsApps", "python.exe");

            FileNotFoundException error = Assert.Throws<FileNotFoundException>(delegate
            {
                DevelopmentExecutableResolver.ResolvePython(
                    "",
                    alias,
                    "windows",
                    "",
                    "",
                    "",
                    Array.Empty<string>(),
                    delegate(string path) { return !string.IsNullOrWhiteSpace(path); });
            });

            Assert.Contains("MOTUPERF_PYTHON", error.Message);
            Assert.Contains("WindowsApps", error.Message);
        }

        [Fact]
        public void PythonResolutionPrefersKnownRealInstallationOverWindowsAppsPath()
        {
            string local = Path.GetTempPath();
            string windowsApps = Path.Combine(local, "Microsoft", "WindowsApps");
            string expected = Path.Combine(local, "Programs", "Python", "Python311", "python.exe");
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                expected,
                Path.Combine(windowsApps, "python.exe")
            };

            string resolved = DevelopmentExecutableResolver.ResolvePython(
                "",
                "",
                "windows",
                local,
                "",
                "",
                new[] { windowsApps },
                delegate(string path) { return files.Contains(path); });

            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void PythonResolutionFallsBackToEnvironmentLocalAppDataRoot()
        {
            string local = Path.Combine(Path.GetTempPath(), "motuperf-profile-" + Guid.NewGuid().ToString("N"));
            string expected = Path.Combine(local, "Programs", "Python", "Python312", "python.exe");

            string resolved = DevelopmentExecutableResolver.ResolvePython(
                "",
                "",
                "windows",
                "",
                "",
                "",
                Array.Empty<string>(),
                delegate(string path) { return string.Equals(path, expected, StringComparison.OrdinalIgnoreCase); },
                new[] { local });

            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void PythonResolutionUsesRegisteredFallbackWhenFileProbeFails()
        {
            const string registered = @"C:\Users\tester\AppData\Local\Programs\Python\Python312\python.exe";

            string resolved = DevelopmentExecutableResolver.ResolvePython(
                "",
                "",
                "windows",
                "",
                "",
                "",
                Array.Empty<string>(),
                delegate(string path) { return false; },
                Array.Empty<string>(),
                new[] { registered });

            Assert.Equal(registered, resolved);
        }

        [Fact]
        public void PythonRegistryDiscoveryReadsOfficialPerUserInstallation()
        {
            if (!OperatingSystem.IsWindows()) return;

            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Python",
                "Python312",
                "python.exe");
            if (!File.Exists(expected)) return;

            string[] resolved = WindowsPythonRegistry.FindExecutablePaths().ToArray();

            Assert.Contains(resolved, path => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase));

            string fallback = DevelopmentExecutableResolver.ResolvePython(
                "",
                "",
                "windows",
                "",
                "",
                "",
                Array.Empty<string>(),
                delegate(string path) { return false; },
                Array.Empty<string>(),
                resolved);
            Assert.Equal(expected, fallback);
        }

        [Fact]
        public void RuntimePathRetriesAfterTransientResolutionFailure()
        {
            int attempts = 0;
            RetryableRuntimePath path = new RetryableRuntimePath(
                delegate
                {
                    attempts++;
                    if (attempts == 1) throw new FileNotFoundException("temporary profile failure");
                    return @"C:\tools\python.exe";
                },
                delegate(string candidate) { return !string.IsNullOrWhiteSpace(candidate); });

            Assert.Throws<FileNotFoundException>(delegate { return path.Value; });
            Assert.Equal(@"C:\tools\python.exe", path.Value);
            Assert.Equal(2, attempts);
        }

        [Fact]
        public void PythonResolutionSkipsWindowsAppsWhenScanningPath()
        {
            string local = Path.GetTempPath();
            string windowsApps = Path.Combine(local, "Microsoft", "WindowsApps");
            string realDirectory = Path.Combine(local, "custom-python");
            string expected = Path.Combine(realDirectory, "python.exe");
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(windowsApps, "python.exe"),
                expected
            };

            string resolved = DevelopmentExecutableResolver.ResolvePython(
                "",
                "",
                "windows",
                "",
                "",
                "",
                new[] { windowsApps, realDirectory },
                delegate(string path) { return files.Contains(path); });

            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void AdbResolutionUsesAndroidSdkRoot()
        {
            string sdk = Path.Combine(Path.GetTempPath(), "android-sdk");
            string expected = Path.Combine(sdk, "platform-tools", "adb.exe");

            string resolved = DevelopmentExecutableResolver.ResolveAdb(
                "",
                "",
                "windows",
                "",
                "",
                sdk,
                Array.Empty<string>(),
                delegate(string path) { return string.Equals(path, expected, StringComparison.OrdinalIgnoreCase); });

            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void ConfiguredPythonPathIsUsedWhenItIsARealFile()
        {
            string configured = Path.Combine(Path.GetTempPath(), "custom", "python.exe");

            string resolved = DevelopmentExecutableResolver.ResolvePython(
                "",
                configured,
                "windows",
                "",
                "",
                "",
                Array.Empty<string>(),
                delegate(string path) { return string.Equals(path, configured, StringComparison.OrdinalIgnoreCase); });

            Assert.Equal(Path.GetFullPath(configured), resolved);
        }

        [Fact]
        public void WindowsExit9009IsReportedAsRuntimeStartupFailure()
        {
            if (!OperatingSystem.IsWindows()) return;

            string message = PerfCollector.DescribeRunnerExit(9009);

            Assert.Contains("Windows 错误 9009", message);
            Assert.Contains("Python", message);
            Assert.Contains("ADB", message);
        }

        [Fact]
        public void WindowsDataDirectoryLivesUnderApplicationDirectory()
        {
            string application = Path.Combine(Path.GetTempPath(), "motuperf-app-" + Guid.NewGuid().ToString("N"));
            RuntimeToolResolver resolver = new RuntimeToolResolver(application, true, "windows", "x64");

            Assert.Equal(Path.Combine(Path.GetFullPath(application), "data"), resolver.UserDataDirectory);
        }

        [Fact]
        public void LegacyDataMovesWithoutOverwritingExistingFiles()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-migration-" + Guid.NewGuid().ToString("N"));
            string legacy = Path.Combine(root, "legacy");
            string target = Path.Combine(root, "target");
            try
            {
                Directory.CreateDirectory(Path.Combine(legacy, "opened", "session"));
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy-settings");
                File.WriteAllText(Path.Combine(legacy, "opened", "session", "shot.png"), "shot");
                File.WriteAllText(Path.Combine(target, "settings.json"), "current-settings");

                RuntimeDataMigration.MoveTree(legacy, target, System.Threading.CancellationToken.None);

                Assert.Equal("current-settings", File.ReadAllText(Path.Combine(target, "settings.json")));
                Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
                Assert.Equal("shot", File.ReadAllText(Path.Combine(target, "opened", "session", "shot.png")));
                Assert.False(File.Exists(Path.Combine(legacy, "opened", "session", "shot.png")));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task ChangingWindowsDataDirectoryMigratesExistingDataAndPersistsLocation()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-directory-change-" + Guid.NewGuid().ToString("N"));
            string application = Path.Combine(root, "app");
            string target = Path.Combine(root, "data-target");
            try
            {
                RuntimeToolResolver resolver = new RuntimeToolResolver(application, true, "windows", "x64");
                Directory.CreateDirectory(Path.Combine(resolver.UserDataDirectory, "logs"));
                File.WriteAllText(Path.Combine(resolver.UserDataDirectory, "logs", "collector.log"), "log");

                string changed = await resolver.ChangeUserDataDirectoryAsync(target, CancellationToken.None);

                Assert.Equal(Path.GetFullPath(target), changed);
                Assert.Equal("log", File.ReadAllText(Path.Combine(target, "logs", "collector.log")));
                Assert.True(File.Exists(resolver.DataLocationConfigPath));
                Assert.Equal(Path.GetFullPath(target), resolver.UserDataDirectory);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public void RuntimeDataManagerReportsAndDeletesOnlySelectedCategories()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-data-manager-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "logs"));
                Directory.CreateDirectory(Path.Combine(root, "screenshots"));
                File.WriteAllText(Path.Combine(root, "settings.json"), "settings");
                File.WriteAllText(Path.Combine(root, "logs", "a.log"), "12345");
                File.WriteAllText(Path.Combine(root, "screenshots", "a.png"), "123456789");

                var categories = RuntimeDataManager.GetCategories(root);
                Assert.Equal(7, categories.Count);
                Assert.Equal(5L, categories.Single(item => item.Id == "logs").SizeBytes);
                Assert.Equal(9L, categories.Single(item => item.Id == "screenshots").SizeBytes);

                RecordingCleanupProgress progress = new RecordingCleanupProgress();
                RuntimeDataCleanupResult result = RuntimeDataManager.Delete(root, new[] { "logs" }, progress);

                Assert.Equal(5L, result.DeletedBytes);
                Assert.False(File.Exists(Path.Combine(root, "logs", "a.log")));
                Assert.True(File.Exists(Path.Combine(root, "screenshots", "a.png")));
                Assert.True(File.Exists(Path.Combine(root, "settings.json")));
                Assert.NotEmpty(progress.Values);
                RuntimeDataCleanupProgress final = progress.Values.Last();
                Assert.Equal(5L, final.TotalBytes);
                Assert.Equal(5L, final.ProcessedBytes);
                Assert.Equal(5L, final.DeletedBytes);
                Assert.Equal(1, final.TotalFiles);
                Assert.Equal(1, final.ProcessedFiles);
                Assert.Equal(100, final.Percent);
                Assert.Empty(result.Failures);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public void DataMigrationReportsByteProgressAndFlattensLegacyDataFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-migration-progress-" + Guid.NewGuid().ToString("N"));
            string legacy = Path.Combine(root, "legacy");
            string target = Path.Combine(root, "target");
            try
            {
                Directory.CreateDirectory(Path.Combine(legacy, "data"));
                Directory.CreateDirectory(Path.Combine(legacy, "logs"));
                File.WriteAllText(Path.Combine(legacy, "data", "session.bin"), "12345");
                File.WriteAllText(Path.Combine(legacy, "logs", "collector.log"), "123");
                RecordingProgress recording = new RecordingProgress();

                RuntimeDataMigration.MoveTree(legacy, target, CancellationToken.None, recording);

                RuntimeDataMigrationProgress final = recording.Values.Last();
                Assert.Equal(8L, final.TotalBytes);
                Assert.Equal(8L, final.ProcessedBytes);
                Assert.Equal(2, final.TotalFiles);
                Assert.Equal(2, final.ProcessedFiles);
                Assert.Equal(100, final.Percent);
                Assert.True(File.Exists(Path.Combine(target, "session.bin")));
                Assert.True(File.Exists(Path.Combine(target, "logs", "collector.log")));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private sealed class RecordingProgress : IProgress<RuntimeDataMigrationProgress>
        {
            public List<RuntimeDataMigrationProgress> Values { get; } = new List<RuntimeDataMigrationProgress>();
            public void Report(RuntimeDataMigrationProgress value) { Values.Add(value); }
        }

        private sealed class RecordingCleanupProgress : IProgress<RuntimeDataCleanupProgress>
        {
            public List<RuntimeDataCleanupProgress> Values { get; } = new List<RuntimeDataCleanupProgress>();
            public void Report(RuntimeDataCleanupProgress value) { Values.Add(value); }
        }
    }
}
