using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class ProcessRunnerTests
    {
        [Fact]
        public async Task StreamingUsesExecutableDirectoryAndPreservesStructuredArguments()
        {
            string commandShell = ShellPath();
            using Process process = ProcessRunner.StartStreaming(
                commandShell,
                ShellArguments(OperatingSystem.IsWindows() ? "cd" : "pwd -L"));
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(Path.GetDirectoryName(commandShell), process.StartInfo.WorkingDirectory);
            Assert.Equal(process.StartInfo.WorkingDirectory, (await stdout).Trim(), ignoreCase: OperatingSystem.IsWindows());
            Assert.Equal("", await stderr);
        }

        [Fact]
        public async Task RunToFileWritesStdoutAndKeepsStderrSeparate()
        {
            string directory = Path.Combine(Path.GetTempPath(), "motuperf-output-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "capture data.bin");
            try
            {
                string command = OperatingSystem.IsWindows() ? "echo payload&echo diagnostic 1>&2" : "echo payload; echo diagnostic 1>&2";
                ProcessResult result = await ProcessRunner.RunToFileAsync(
                    ShellPath(), ShellArguments(command), path, 15000, CancellationToken.None);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("payload", File.ReadAllText(path).Trim());
                Assert.Equal("diagnostic", result.Stderr.Trim());
                Assert.Equal("", result.Stdout);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public async Task RunAsyncUsesExecutableDirectoryAsWorkingDirectory()
        {
            string commandShell = ShellPath();
            string expectedDirectory = Path.GetDirectoryName(Path.GetFullPath(commandShell));
            ProcessResult result = await ProcessRunner.RunAsync(
                commandShell,
                ShellArguments(OperatingSystem.IsWindows() ? "cd" : "pwd -L"),
                15000,
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expectedDirectory, result.Stdout.Trim(), ignoreCase: OperatingSystem.IsWindows());
        }

        [Fact]
        public async Task UnwritableOutputIsRejectedBeforeLaunchingCommand()
        {
            string directory = Path.Combine(Path.GetTempPath(), "motuperf-no-output-" + Guid.NewGuid().ToString("N"));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ProcessRunner.RunToFileAsync(
                Path.Combine(directory, "missing-program"), Array.Empty<string>(), Path.Combine(directory, "output.bin"), 15000, CancellationToken.None));
        }

        [Fact]
        public async Task PreCanceledCallsDoNotLaunchProgramsOrTruncateFiles()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            string path = Path.GetTempFileName();
            File.WriteAllText(path, "keep");
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(
                    "missing-program", "", 15000, cancellation.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunToFileAsync(
                    "missing-program", "", path, 15000, cancellation.Token));
                Assert.Equal("keep", File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task TimeoutAndCancellationReleaseOutputAndStopOwnedCommands()
        {
            string command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";
            string path = Path.GetTempFileName();
            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(
                    ShellPath(), ShellArguments(command), 150, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
                using var cancellation = new CancellationTokenSource(150);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunToFileAsync(
                    ShellPath(), ShellArguments(command), path, 15000, cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(10));
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void WindowsLaunchFailurePreservesNativeErrorCode()
        {
            if (!OperatingSystem.IsWindows()) return;
            string path = Path.Combine(Path.GetTempPath(), "motuperf-invalid-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(path, "Not a PE executable");
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => ProcessRunner.StartStreaming(path, ""));
                var cause = Assert.IsType<System.ComponentModel.Win32Exception>(error.InnerException);
                Assert.NotEqual(0, cause.NativeErrorCode);
                Assert.Contains(cause.NativeErrorCode.ToString(), error.Message);
                Assert.Contains(cause.Message, error.Message);
                Assert.Contains(path, error.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task MissingExecutableReportsPathAndUnderlyingReasonAcrossEntryPoints()
        {
            string path = Path.Combine(Path.GetTempPath(), "missing-program-" + Guid.NewGuid().ToString("N"));
            var streaming = Assert.Throws<InvalidOperationException>(() => ProcessRunner.StartStreaming(path, ""));
            var query = await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessRunner.RunAsync(path, "", 15000, CancellationToken.None));
            foreach (var error in new[] { streaming, query })
            {
                Assert.IsType<FileNotFoundException>(error.InnerException);
                Assert.Contains(path, error.Message);
                Assert.Contains(Path.GetDirectoryName(path), error.Message);
                Assert.Contains(error.InnerException.Message, error.Message);
            }
        }

        private static string ShellPath()
        {
            return OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
                : "/bin/sh";
        }

        private static string[] ShellArguments(string command)
        {
            return OperatingSystem.IsWindows() ? new[] { "/d", "/c", command } : new[] { "-c", command };
        }
    }
}
