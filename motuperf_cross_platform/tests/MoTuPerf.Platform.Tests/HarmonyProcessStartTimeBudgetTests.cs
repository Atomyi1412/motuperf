using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyProcessStartTimeBudgetTests
    {
        [Fact]
        public async Task StartTimeBudgetTimeoutKeepsProcessRowsSelectable()
        {
            int statCommands = 0;
            var service = new HarmonyLookupService(async (serial, command, timeout, token) =>
            {
                if (command.Length > 0 && string.Equals(command[0], "acm", StringComparison.Ordinal))
                    return new ProcessResult(0, "ID: 0\n", "");

                if (command.Length > 0 && string.Equals(command[0], "ps", StringComparison.Ordinal))
                    return new ProcessResult(0, "PID ARGS\n123 /system/bin/com.example.game\n", "");

                string shell = command.Length >= 3 && string.Equals(command[0], "sh", StringComparison.Ordinal)
                    ? command[2] ?? ""
                    : "";
                if (shell.Contains("cat /proc/$p/stat", StringComparison.Ordinal))
                {
                    statCommands++;
                    await Task.Delay(Timeout.Infinite, token);
                }

                return new ProcessResult(0, "", "");
            }, 40, 40, 40);

            List<ProcessInfo> processes = await service.ListProcessesAsync("start-budget-test", CancellationToken.None);

            ProcessInfo process = Assert.Single(processes);
            Assert.Equal(123, process.Pid);
            Assert.Equal("com.example.game", process.Name);
            Assert.Equal(0, process.HarmonyStartTimeTicks);
            Assert.Equal(1, statCommands);
        }

        [Fact]
        public async Task StartTimeBudgetTimeoutKeepsCompletedEarlierBatch()
        {
            int statCommands = 0;
            var service = new HarmonyLookupService(async (serial, command, timeout, token) =>
            {
                if (command.Length > 0 && string.Equals(command[0], "acm", StringComparison.Ordinal))
                    return new ProcessResult(0, "ID: 0\n", "");

                if (command.Length > 0 && string.Equals(command[0], "ps", StringComparison.Ordinal))
                {
                    string rows = string.Join("\n", Enumerable.Range(1, 65)
                        .Select(pid => pid + " com.example.app" + pid));
                    return new ProcessResult(0, "PID ARGS\n" + rows + "\n", "");
                }

                string shell = command.Length >= 3 && string.Equals(command[0], "sh", StringComparison.Ordinal)
                    ? command[2] ?? ""
                    : "";
                if (shell.Contains("cat /proc/$p/stat", StringComparison.Ordinal))
                {
                    statCommands++;
                    if (statCommands == 1)
                    {
                        string stats = string.Join("\n", Enumerable.Range(1, 64)
                            .Select(pid => pid + " (p" + pid + ") " + string.Join(" ", Enumerable.Repeat("0", 19)) + " " + (1000 + pid)));
                        return new ProcessResult(0, stats + "\n", "");
                    }

                    await Task.Delay(Timeout.Infinite, token);
                }

                return new ProcessResult(0, "", "");
            }, 40, 40, 40);

            List<ProcessInfo> processes = await service.ListProcessesAsync("start-budget-batch-test", CancellationToken.None);

            Assert.Equal(2, statCommands);
            Assert.Equal(1001, processes.Single(process => process.Pid == 1).HarmonyStartTimeTicks);
            Assert.Equal(0, processes.Single(process => process.Pid == 65).HarmonyStartTimeTicks);
        }

        [Fact]
        public async Task CallerCancellationDuringStartTimeHydrationIsPropagated()
        {
            var service = new HarmonyLookupService(async (serial, command, timeout, token) =>
            {
                if (command.Length > 0 && string.Equals(command[0], "acm", StringComparison.Ordinal))
                    return new ProcessResult(0, "ID: 0\n", "");

                if (command.Length > 0 && string.Equals(command[0], "ps", StringComparison.Ordinal))
                    return new ProcessResult(0, "PID ARGS\n123 /system/bin/com.example.game\n", "");

                string shell = command.Length >= 3 && string.Equals(command[0], "sh", StringComparison.Ordinal)
                    ? command[2] ?? ""
                    : "";
                if (shell.Contains("cat /proc/$p/stat", StringComparison.Ordinal))
                    await Task.Delay(Timeout.Infinite, token);

                return new ProcessResult(0, "", "");
            }, 10000, 10000, 10000);

            using var cancellation = new CancellationTokenSource();
            Task cancelTask = Task.Run(async () =>
            {
                await Task.Delay(20);
                cancellation.Cancel();
            });

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => service.ListProcessesAsync("start-budget-cancel-test", cancellation.Token));
            await cancelTask;
        }
    }
}
