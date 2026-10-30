using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpIosPerfMonitor
{
    public static class ProcessRunner
    {
        public static async Task<ProcessResult> RunAsync(string fileName, string arguments, int timeoutMs, CancellationToken token)
        {
            return await RunAsync(
                delegate { return CreateStartInfo(fileName, arguments); },
                fileName + " " + arguments,
                timeoutMs,
                token);
        }

        public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, int timeoutMs, CancellationToken token)
        {
            string[] argumentArray = arguments == null ? Array.Empty<string>() : new List<string>(arguments).ToArray();
            StringBuilder display = new StringBuilder(fileName);
            foreach (string argument in argumentArray)
            {
                display.Append(' ').Append(argument);
            }
            return await RunAsync(
                delegate
                {
                    ProcessStartInfo startInfo = CreateStartInfo(fileName, "");
                    foreach (string argument in argumentArray) startInfo.ArgumentList.Add(argument ?? "");
                    return startInfo;
                },
                display.ToString(),
                timeoutMs,
                token);
        }

        private static async Task<ProcessResult> RunAsync(Func<ProcessStartInfo> startInfoFactory, string displayCommand, int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();
            TaskCompletionSource<int> completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> stdoutClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> stderrClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (Process process = StartProcess(
                startInfoFactory,
                delegate(Process started)
                {
                    started.EnableRaisingEvents = true;
                    started.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) stdout.AppendLine(e.Data);
                        else stdoutClosed.TrySetResult(true);
                    };
                    started.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) stderr.AppendLine(e.Data);
                        else stderrClosed.TrySetResult(true);
                    };
                    started.Exited += delegate(object sender, EventArgs e)
                    {
                        try { completion.TrySetResult(((Process)sender).ExitCode); }
                        catch { completion.TrySetCanceled(); }
                    };
                }))
            {
                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    Task drained = Task.WhenAll(completion.Task, stdoutClosed.Task, stderrClosed.Task);
                    Task timeoutTask = Task.Delay(timeoutMs, token);
                    Task finished = await Task.WhenAny(drained, timeoutTask);
                    if (finished != drained)
                    {
                        TryKill(process);
                        WaitForExit(process);
                        DrainAfterKill(stdoutClosed.Task, stderrClosed.Task);
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException(displayCommand + " timed out.");
                    }

                    await drained;
                    return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
                }
                finally
                {
                    TryKill(process);
                    WaitForExit(process);
                }
            }
        }

        public static Process StartStreaming(string fileName, string arguments)
        {
            return StartStreaming(delegate { return CreateStartInfo(fileName, arguments); });
        }

        public static Process StartStreaming(string fileName, IEnumerable<string> arguments)
        {
            string[] argumentArray = arguments == null ? Array.Empty<string>() : new List<string>(arguments).ToArray();
            return StartStreaming(delegate
            {
                ProcessStartInfo startInfo = CreateStartInfo(fileName, "");
                foreach (string argument in argumentArray)
                {
                    startInfo.ArgumentList.Add(argument ?? "");
                }
                return startInfo;
            });
        }

        public static async Task<ProcessResult> RunToFileAsync(string fileName, string arguments, string outputPath, int timeoutMs, CancellationToken token)
        {
            return await RunToFileAsync(
                delegate { return CreateStartInfo(fileName, arguments); },
                fileName + " " + arguments,
                outputPath,
                timeoutMs,
                token);
        }

        public static async Task<ProcessResult> RunToFileAsync(string fileName, IEnumerable<string> arguments, string outputPath, int timeoutMs, CancellationToken token)
        {
            string[] argumentArray = arguments == null ? Array.Empty<string>() : new List<string>(arguments).ToArray();
            StringBuilder display = new StringBuilder(fileName);
            foreach (string argument in argumentArray)
            {
                display.Append(' ').Append(argument);
            }
            return await RunToFileAsync(
                delegate
                {
                    ProcessStartInfo startInfo = CreateStartInfo(fileName, "");
                    foreach (string argument in argumentArray) startInfo.ArgumentList.Add(argument ?? "");
                    return startInfo;
                },
                display.ToString(),
                outputPath,
                timeoutMs,
                token);
        }

        private static async Task<ProcessResult> RunToFileAsync(Func<ProcessStartInfo> startInfoFactory, string displayCommand, string outputPath, int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using FileStream output = File.Create(outputPath);
            StringBuilder stderr = new StringBuilder();
            TaskCompletionSource<int> completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> stderrClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (Process process = StartProcess(
                startInfoFactory,
                delegate(Process started)
                {
                    started.EnableRaisingEvents = true;
                    started.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) stderr.AppendLine(e.Data);
                        else stderrClosed.TrySetResult(true);
                    };
                    started.Exited += delegate(object sender, EventArgs e)
                    {
                        try { completion.TrySetResult(((Process)sender).ExitCode); }
                        catch { completion.TrySetCanceled(); }
                    };
                }))
            {
                Task copyTask = null;
                try
                {
                    process.BeginErrorReadLine();
                    copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
                    Task timeoutTask = Task.Delay(timeoutMs, token);
                    Task drained = Task.WhenAll(completion.Task, copyTask, stderrClosed.Task);
                    Task finished = await Task.WhenAny(drained, timeoutTask);
                    if (finished != timeoutTask)
                    {
                        await drained;
                    }
                    else
                    {
                        TryKill(process);
                        WaitForExit(process);
                        try { await copyTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                        try { await stderrClosed.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException(displayCommand + " timed out.");
                    }
                    return new ProcessResult(process.ExitCode, "", stderr.ToString());
                }
                finally
                {
                    TryKill(process);
                    WaitForExit(process);
                    if (copyTask != null)
                    {
                        try { await copyTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                    }
                }
            }
        }

        public static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
            }
        }

        private static void WaitForExit(Process process)
        {
            try
            {
                if (process != null && !process.HasExited) process.WaitForExit(2000);
            }
            catch
            {
            }
        }

        private static void DrainAfterKill(Task stdoutClosed, Task stderrClosed)
        {
            try { Task.WhenAll(stdoutClosed, stderrClosed).Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        private static ProcessStartInfo CreateStartInfo(string fileName, string arguments)
        {
            string normalizedFileName = fileName ?? "";
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = normalizedFileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (Path.IsPathFullyQualified(normalizedFileName))
            {
                string fullPath = Path.GetFullPath(normalizedFileName);
                startInfo.FileName = fullPath;
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory)) startInfo.WorkingDirectory = directory;
            }
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";
            RuntimeTools.ConfigureProcessEnvironment(startInfo);
            return startInfo;
        }

        private static Process StartStreaming(Func<ProcessStartInfo> startInfoFactory)
        {
            return StartProcess(startInfoFactory, delegate(Process process) { process.EnableRaisingEvents = true; });
        }

        private static Process StartProcess(Func<ProcessStartInfo> startInfoFactory, Action<Process> configure)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ProcessStartInfo startInfo = startInfoFactory();
                try
                {
                    ValidateAbsoluteStartInfo(startInfo);
                }
                catch (Exception ex)
                {
                    throw CreateStartFailure(startInfo, ex);
                }
                Process process = new Process();
                try
                {
                    process.StartInfo = startInfo;
                    if (configure != null) configure(process);
                    process.Start();
                    return process;
                }
                catch (Exception ex) when (IsFileLookupFailure(ex) && attempt == 0)
                {
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    process.Dispose();
                    throw CreateStartFailure(startInfo, ex);
                }
            }

            throw new InvalidOperationException("Process start attempts exhausted.");
        }

        private static void ValidateAbsoluteStartInfo(ProcessStartInfo startInfo)
        {
            if (startInfo == null || !Path.IsPathFullyQualified(startInfo.FileName ?? "")) return;
            string fileName = Path.GetFullPath(startInfo.FileName);
            if (!File.Exists(fileName))
                throw new FileNotFoundException("启动程序不存在：" + fileName, fileName);
            string workingDirectory = startInfo.WorkingDirectory;
            if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException("启动工作目录不存在：" + workingDirectory);
        }

        private static bool IsFileLookupFailure(Exception exception)
        {
            Win32Exception win32 = exception as Win32Exception;
            return OperatingSystem.IsWindows() && win32 != null && (win32.NativeErrorCode == 2 || win32.NativeErrorCode == 3);
        }

        private static Exception CreateStartFailure(ProcessStartInfo startInfo, Exception inner)
        {
            string fileName = startInfo == null ? "" : startInfo.FileName ?? "";
            string workingDirectory = startInfo == null ? "" : startInfo.WorkingDirectory ?? "";
            string detail = "启动外部进程失败：" + fileName
                + "；工作目录：" + workingDirectory
                + "；程序存在：" + (Path.IsPathFullyQualified(fileName) && File.Exists(fileName) ? "是" : "否")
                + "；工作目录存在：" + (string.IsNullOrWhiteSpace(workingDirectory) || Directory.Exists(workingDirectory) ? "是" : "否");
            if (inner is Win32Exception win32) detail += "；Windows 错误码：" + win32.NativeErrorCode;
            if (inner != null) detail += "；原因：" + inner.Message;
            return new InvalidOperationException(detail, inner);
        }
    }

    public sealed class ProcessResult
    {
        public ProcessResult(int exitCode, string stdout, string stderr)
        {
            ExitCode = exitCode;
            Stdout = stdout;
            Stderr = stderr;
        }

        public int ExitCode { get; private set; }
        public string Stdout { get; private set; }
        public string Stderr { get; private set; }
    }
}
