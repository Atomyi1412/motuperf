using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HdcToolLocatorTests
    {
        [Fact]
        public void ExplicitPathWinsOverBundledPathAndKnownSdkRoots()
        {
            string directory = CreateDirectory();
            try
            {
                string configured = Path.Combine(directory, "configured-hdc.exe");
                string bundled = Path.Combine(directory, "bundled-hdc.exe");
                File.WriteAllText(configured, "configured");
                File.WriteAllText(bundled, "bundled");

                Assert.Equal(
                    Path.GetFullPath(configured),
                    HdcToolLocator.ResolveFrom(bundled, configured, "", true, new[] { directory }));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void FindsHdcInDevEcoSdkRootWhenItIsNotOnPath()
        {
            string directory = CreateDirectory();
            try
            {
                string executable = Path.Combine(directory, "default", "openharmony", "toolchains", "hdc.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(executable));
                File.WriteAllText(executable, "hdc");

                Assert.Equal(
                    Path.GetFullPath(executable),
                    HdcToolLocator.ResolveFrom("", "", "relative-only", true, new[] { directory }));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void FindsHdcWhenBeginnerSelectsCommandLineToolsFolder()
        {
            string directory = CreateDirectory();
            try
            {
                string executable = Path.Combine(directory, "sdk", "default", "openharmony", "toolchains", "hdc.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(executable));
                File.WriteAllText(executable, "hdc");

                Assert.Equal(
                    Path.GetFullPath(executable),
                    HdcToolLocator.FindInSelectedDirectory(directory, true));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void SelectedDirectoryDoesNotAcceptAnUnrelatedExecutable()
        {
            string directory = CreateDirectory();
            try
            {
                File.WriteAllText(Path.Combine(directory, "other-tool.exe"), "not-hdc");

                Assert.Equal("", HdcToolLocator.FindInSelectedDirectory(directory, true));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void FindsMacHdcInToolchainsRoot()
        {
            string directory = CreateDirectory();
            try
            {
                string executable = Path.Combine(directory, "hdc");
                File.WriteAllText(executable, "hdc");

                Assert.Equal(
                    Path.GetFullPath(executable),
                    HdcToolLocator.ResolveFrom("", "", "", false, new[] { directory }));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void IgnoresWindowsBundledHdcWhenResolvingMacTool()
        {
            string directory = CreateDirectory();
            try
            {
                string bundled = Path.Combine(directory, "hdc.exe");
                string macHdc = Path.Combine(directory, "tools", "hdc");
                File.WriteAllText(bundled, "windows-hdc");
                Directory.CreateDirectory(Path.GetDirectoryName(macHdc));
                File.WriteAllText(macHdc, "mac-hdc");

                Assert.Equal(
                    Path.GetFullPath(macHdc),
                    HdcToolLocator.ResolveFrom(bundled, "", "", false, new[] { directory }));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void IgnoresMacBundledHdcWhenResolvingWindowsTool()
        {
            string directory = CreateDirectory();
            try
            {
                string bundled = Path.Combine(directory, "hdc");
                string windowsHdc = Path.Combine(directory, "bin", "hdc.exe");
                File.WriteAllText(bundled, "mac-hdc");
                Directory.CreateDirectory(Path.GetDirectoryName(windowsHdc));
                File.WriteAllText(windowsHdc, "windows-hdc");

                Assert.Equal(
                    Path.GetFullPath(windowsHdc),
                    HdcToolLocator.ResolveFrom(bundled, "", "", true, new[] { directory }));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void FindsHdcInCommonSdkToolsDirectory()
        {
            string directory = CreateDirectory();
            try
            {
                string executable = Path.Combine(directory, "tools", "hdc.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(executable));
                File.WriteAllText(executable, "hdc");

                Assert.Equal(
                    Path.GetFullPath(executable),
                    HdcToolLocator.ResolveFrom("", "", "relative-only", true, new[] { directory }));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void InvalidExplicitPathDoesNotSilentlyFallBackToAnotherHdc()
        {
            string directory = CreateDirectory();
            try
            {
                string knownHdc = Path.Combine(directory, "hdc.exe");
                File.WriteAllText(knownHdc, "hdc");

                FileNotFoundException exception = Assert.Throws<FileNotFoundException>(delegate
                {
                    HdcToolLocator.ResolveFrom(
                        "",
                        Path.Combine(directory, "missing-hdc.exe"),
                        "",
                        true,
                        new[] { directory });
                });
                Assert.Contains("保存的 HDC 文件不存在", exception.Message);
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void SavesAndReadsSelectedWindowsHdcPathForBeginnerFlow()
        {
            string directory = CreateDirectory();
            try
            {
                string executable = Path.Combine(directory, "toolchains", "hdc.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(executable));
                File.WriteAllText(executable, "hdc");

                HdcToolLocator.SaveConfiguredPath(directory, executable, true);

                Assert.Equal(Path.GetFullPath(executable), HdcToolLocator.ReadConfiguredPath(directory));
                Assert.Equal(
                    Path.GetFullPath(executable),
                    HdcToolLocator.ResolveFrom("", HdcToolLocator.ReadConfiguredPath(directory), "", true, new string[0]));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void SelectedHdcMustMatchCurrentComputerPlatform()
        {
            string directory = CreateDirectory();
            try
            {
                string windowsHdc = Path.Combine(directory, "hdc.exe");
                string macHdc = Path.Combine(directory, "hdc");
                File.WriteAllText(windowsHdc, "windows-hdc");
                File.WriteAllText(macHdc, "mac-hdc");

                Assert.True(HdcToolLocator.IsPlatformExecutablePath(windowsHdc, true));
                Assert.False(HdcToolLocator.IsPlatformExecutablePath(windowsHdc, false));
                Assert.True(HdcToolLocator.IsPlatformExecutablePath(macHdc, false));
                Assert.False(HdcToolLocator.IsPlatformExecutablePath(macHdc, true));
            }
            finally { DeleteDirectory(directory); }
        }

        [Fact]
        public void IncludesPerUserDevEcoInstallationRoots()
        {
            List<string> roots = HdcToolLocator.KnownSdkRoots(OperatingSystem.IsWindows()).ToList();
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (OperatingSystem.IsWindows())
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    Assert.Contains(Path.Combine(localAppData, "Huawei", "DevEco Studio"), roots);
                    Assert.Contains(Path.Combine(localAppData, "Huawei", "DevEco-Studio"), roots);
                }
            }
            else if (!string.IsNullOrWhiteSpace(userProfile))
            {
                Assert.Contains(Path.Combine(userProfile, "Library", "Huawei", "DevEco Studio"), roots);
                Assert.Contains(Path.Combine(userProfile, "Library", "Huawei", "DevEco-Studio"), roots);
            }

            if (!string.IsNullOrWhiteSpace(applicationData))
                Assert.Contains(Path.Combine(applicationData, "Huawei", "DevEco Studio"), roots);
            if (!string.IsNullOrWhiteSpace(documents))
                Assert.Contains(Path.Combine(documents, "Huawei", "DevEco Studio"), roots);
        }

        private static string CreateDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "motuperf-hdc-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }
    }
}
