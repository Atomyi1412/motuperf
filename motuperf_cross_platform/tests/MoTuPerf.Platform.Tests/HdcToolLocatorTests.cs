using System;
using System.IO;
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
                Assert.Contains("MOTUPERF_HDC", exception.Message);
            }
            finally { DeleteDirectory(directory); }
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
