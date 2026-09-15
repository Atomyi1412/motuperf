using System;
using System.Collections.Generic;
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
            ProcessStartInfo startInfo = CreateStartInfo(fileName, arguments);
            return await RunAsync(startInfo, fileName + " " + arguments, timeoutMs, token);
        }

        public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, int timeoutMs, CancellationToken token)
        {
            ProcessStartInfo startInfo = CreateStartInfo(fileName, "");
            StringBuilder display = new StringBuilder(fileName);
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument ?? "");
                display.Append(' ').Append(argument);
            }
            return await RunAsync(startInfo, display.ToString(), timeoutMs, token);
        }

        private static async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, string displayCommand, int timeoutMs, CancellationToken token)
        {
            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.EnableRaisingEvents = true;
                StringBuilder stdout = new StringBuilder();
                StringBuilder stderr = new StringBuilder();
                TaskCompletionSource<int> completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> stdoutClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> stderrClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stdout.AppendLine(e.Data);
                    else stdoutClosed.TrySetResult(true);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stderr.AppendLine(e.Data);
                    else stderrClosed.TrySetResult(true);
                };
                process.Exited += delegate { completion.TrySetResult(process.ExitCode); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                Task timeoutTask = Task.Delay(timeoutMs, token);
                Task finished = await Task.WhenAny(completion.Task, timeoutTask);
                if (finished != completion.Task)
                {
                    TryKill(process);
                    WaitForExit(process);
                    DrainAfterKill(stdoutClosed.Task, stderrClosed.Task);
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException(displayCommand + " timed out.");
                }

                await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task);
                return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
            }
        }

        public static Process StartStreaming(string fileName, string arguments)
        {
            Process process = new Process();
            process.StartInfo = CreateStartInfo(fileName, arguments);
            process.EnableRaisingEvents = true;
            process.Start();
            return process;
        }

        public static Process StartStreaming(string fileName, IEnumerable<string> arguments)
        {
            Process process = new Process();
            ProcessStartInfo startInfo = CreateStartInfo(fileName, "");
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument ?? "");
            }
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
            process.Start();
            return process;
        }

        public static async Task<ProcessResult> RunToFileAsync(string fileName, string arguments, string outputPath, int timeoutMs, CancellationToken token)
        {
            ProcessStartInfo startInfo = CreateStartInfo(fileName, arguments);
            return await RunToFileAsync(startInfo, fileName + " " + arguments, outputPath, timeoutMs, token);
        }

        public static async Task<ProcessResult> RunToFileAsync(string fileName, IEnumerable<string> arguments, string outputPath, int timeoutMs, CancellationToken token)
        {
            ProcessStartInfo startInfo = CreateStartInfo(fileName, "");
            StringBuilder display = new StringBuilder(fileName);
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument ?? "");
                display.Append(' ').Append(argument);
            }
            return await RunToFileAsync(startInfo, display.ToString(), outputPath, timeoutMs, token);
        }

        private static async Task<ProcessResult> RunToFileAsync(ProcessStartInfo startInfo, string displayCommand, string outputPath, int timeoutMs, CancellationToken token)
        {
            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.EnableRaisingEvents = true;
                StringBuilder stderr = new StringBuilder();
                TaskCompletionSource<int> completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> stderrClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stderr.AppendLine(e.Data);
                    else stderrClosed.TrySetResult(true);
                };
                process.Exited += delegate { completion.TrySetResult(process.ExitCode); };

                process.Start();
                process.BeginErrorReadLine();
                Task copyTask;
                using (FileStream output = File.Create(outputPath))
                {
                    copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
                    Task timeoutTask = Task.Delay(timeoutMs, token);
                    Task finished = await Task.WhenAny(Task.WhenAll(completion.Task, copyTask), timeoutTask);
                    if (finished != timeoutTask)
                    {
                        await copyTask;
                        await stderrClosed.Task;
                    }
                    else
                    {
                        TryKill(process);
                        WaitForExit(process);
                        try { await copyTask.ConfigureAwait(false); } catch { }
                        try { await stderrClosed.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException(displayCommand + " timed out.");
                    }
                }

                return new ProcessResult(process.ExitCode, "", stderr.ToString());
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
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";
            RuntimeTools.ConfigureProcessEnvironment(startInfo);
            return startInfo;
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
