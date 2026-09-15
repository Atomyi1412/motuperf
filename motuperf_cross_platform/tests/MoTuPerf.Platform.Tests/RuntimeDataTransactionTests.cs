using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class RuntimeDataTransactionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "motuperf-transaction-" + Guid.NewGuid().ToString("N"));
        private string PathFor(string name) => Path.Combine(_root, name);
        private RuntimeToolResolver Resolver(string os = "windows") => new RuntimeToolResolver(PathFor("app"), true, os,
            os == "macos" ? "arm64" : "x64", PathFor("home"), PathFor("local"));

        [Fact]
        public async Task ConflictPreservesAllOriginalFilesAndOriginalConfiguration()
        {
            RuntimeToolResolver resolver = Resolver();
            string source = resolver.UserDataDirectory;
            File.WriteAllText(Path.Combine(source, "a.txt"), "first");
            File.WriteAllText(Path.Combine(source, "z.txt"), "second");
            Directory.CreateDirectory(PathFor("target"));
            File.WriteAllText(PathFor("target/z.txt"), "different");

            await Assert.ThrowsAsync<IOException>(() => resolver.ChangeUserDataDirectoryAsync(PathFor("target"), CancellationToken.None));

            Assert.Equal(source, resolver.UserDataDirectory);
            Assert.Equal("first", File.ReadAllText(Path.Combine(source, "a.txt")));
            Assert.Equal("second", File.ReadAllText(Path.Combine(source, "z.txt")));
            Assert.Equal("different", File.ReadAllText(PathFor("target/z.txt")));
            Assert.False(File.Exists(PathFor("target/a.txt")));
            Assert.False(File.Exists(resolver.DataLocationConfigPath));
        }

        [Fact]
        public async Task ConfigurationWriteFailureDoesNotDeleteSource()
        {
            RuntimeToolResolver resolver = Resolver();
            string source = resolver.UserDataDirectory;
            File.WriteAllText(Path.Combine(source, "a.txt"), "first");
            Directory.CreateDirectory(resolver.DataLocationConfigPath);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resolver.ChangeUserDataDirectoryAsync(PathFor("target"), CancellationToken.None));

            Assert.Equal("first", File.ReadAllText(Path.Combine(source, "a.txt")));
            Assert.False(File.Exists(PathFor("target/a.txt")));
            Assert.Equal(source, resolver.UserDataDirectory);
        }

        [Fact]
        public async Task CancellationAfterFirstCopyRollsBackOnlyOwnedCopies()
        {
            RuntimeToolResolver resolver = Resolver();
            string source = resolver.UserDataDirectory;
            File.WriteAllText(Path.Combine(source, "a.txt"), "first");
            File.WriteAllText(Path.Combine(source, "b.txt"), "second");
            using CancellationTokenSource cancel = new CancellationTokenSource();
            var progress = new CallbackProgress(p => { if (p.ProcessedFiles > 0) cancel.Cancel(); });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ChangeUserDataDirectoryAsync(PathFor("target"), cancel.Token, progress));

            Assert.Equal(2, Directory.GetFiles(source).Length);
            Assert.Empty(Directory.GetFiles(PathFor("target"), "*", SearchOption.AllDirectories));
            Assert.Equal(source, resolver.UserDataDirectory);
        }

        [Fact]
        public async Task IdenticalCopyFromInterruptedRunCanBeRetriedWithoutFlatteningData()
        {
            RuntimeToolResolver resolver = Resolver();
            string source = resolver.UserDataDirectory;
            Directory.CreateDirectory(Path.Combine(source, "data"));
            File.WriteAllText(Path.Combine(source, "data", "a.txt"), "same");
            Directory.CreateDirectory(PathFor("target/data"));
            File.WriteAllText(PathFor("target/data/a.txt"), "same");

            await resolver.ChangeUserDataDirectoryAsync(PathFor("target"), CancellationToken.None);

            Assert.Equal("same", File.ReadAllText(PathFor("target/data/a.txt")));
            Assert.False(File.Exists(Path.Combine(source, "data", "a.txt")));
            Assert.Equal(PathFor("target"), Resolver().UserDataDirectory);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DirectoryLinksCannotMoveExternalFiles(bool targetLink)
        {
            RuntimeToolResolver resolver = Resolver();
            string source = resolver.UserDataDirectory;
            Directory.CreateDirectory(PathFor("outside"));
            File.WriteAllText(PathFor("outside/marker.txt"), "external");
            string link = targetLink ? PathFor("target") : Path.Combine(source, "linked");
            CreateLink(link, PathFor("outside"));
            try
            {
                await Assert.ThrowsAsync<IOException>(() => resolver.ChangeUserDataDirectoryAsync(PathFor("target"), CancellationToken.None));
                Assert.Equal("external", File.ReadAllText(PathFor("outside/marker.txt")));
                Assert.Equal(source, resolver.UserDataDirectory);
                if (!targetLink)
                    Assert.False(RuntimeDataMigration.MoveTree(source, PathFor("legacy-target"), CancellationToken.None));
            }
            finally { Directory.Delete(link); }
        }

        [Fact]
        public async Task MacCustomLocationSurvivesRestartAndLegacyMigrationLeavesConfigurationAlone()
        {
            RuntimeToolResolver resolver = Resolver("macos");
            string source = resolver.UserDataDirectory;
            File.WriteAllText(Path.Combine(source, "settings.json"), "settings");
            string support = Path.GetDirectoryName(resolver.DataLocationConfigPath);
            Directory.CreateDirectory(Path.Combine(support, "logs"));
            File.WriteAllText(Path.Combine(support, "logs", "old.log"), "legacy");

            await resolver.ChangeUserDataDirectoryAsync(PathFor("custom"), CancellationToken.None);
            RuntimeToolResolver restarted = Resolver("macos");
            await restarted.MigrateLegacyDataAsync(CancellationToken.None);

            Assert.Equal(PathFor("custom"), restarted.UserDataDirectory);
            Assert.Equal("settings", File.ReadAllText(PathFor("custom/settings.json")));
            Assert.Equal("legacy", File.ReadAllText(PathFor("custom/logs/old.log")));
            Assert.True(File.Exists(resolver.DataLocationConfigPath));
            Assert.False(Directory.Exists(PathFor("app")));
            Assert.Equal(PathFor("custom"), Resolver("macos").UserDataDirectory);
        }

        [Fact]
        public async Task ChangedSourceDuringMigrationIsNotRemoved()
        {
            RuntimeToolResolver resolver = Resolver();
            string source = resolver.UserDataDirectory;
            string file = Path.Combine(source, "a.txt");
            File.WriteAllText(file, "before");
            var progress = new CallbackProgress(p => { if (p.Stage == "校验文件") File.WriteAllText(file, "after"); });

            await Assert.ThrowsAsync<IOException>(() => resolver.ChangeUserDataDirectoryAsync(PathFor("target"), CancellationToken.None, progress));

            Assert.Equal("after", File.ReadAllText(file));
            Assert.False(File.Exists(PathFor("target/a.txt")));
            Assert.Equal(source, resolver.UserDataDirectory);
        }

        [Fact]
        public void MacReadsLegacyBundleConfigurationIntoStableSupportDirectory()
        {
            Directory.CreateDirectory(PathFor("app"));
            File.WriteAllText(PathFor("app/data-location.json"), System.Text.Json.JsonSerializer.Serialize(new { Directory = PathFor("custom") }));
            RuntimeToolResolver resolver = Resolver("macos");

            Assert.Equal(PathFor("custom"), resolver.UserDataDirectory);
            Assert.True(File.Exists(resolver.DataLocationConfigPath));
            File.Delete(PathFor("app/data-location.json"));
            Assert.Equal(PathFor("custom"), Resolver("macos").UserDataDirectory);
        }

        [Fact]
        public void TransactionRejectsIdenticalOrNestedPhysicalDirectories()
        {
            Directory.CreateDirectory(PathFor("data"));
            File.WriteAllText(PathFor("data/a.txt"), "keep");
            Assert.Throws<IOException>(() => RuntimeDataMigration.CopyTreeAndCommit(PathFor("data"), PathFor("data"), () => { }, CancellationToken.None));
            Assert.Throws<IOException>(() => RuntimeDataMigration.CopyTreeAndCommit(PathFor("data"), PathFor("data/nested"), () => { }, CancellationToken.None));
            Assert.Equal("keep", File.ReadAllText(PathFor("data/a.txt")));
            Assert.Empty(Directory.GetFiles(PathFor("data"), ".motuperf-boundary-*"));
        }

        private static void CreateLink(string link, string destination)
        {
            if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, destination); return; }
            using Process process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + destination + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        private sealed class CallbackProgress : IProgress<RuntimeDataMigrationProgress>
        {
            private readonly Action<RuntimeDataMigrationProgress> _callback;
            public CallbackProgress(Action<RuntimeDataMigrationProgress> callback) { _callback = callback; }
            public void Report(RuntimeDataMigrationProgress value) => _callback(value);
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
