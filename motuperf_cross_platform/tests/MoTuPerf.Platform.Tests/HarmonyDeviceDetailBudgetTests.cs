using System;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyDeviceDetailBudgetTests
    {
        [Fact]
        public async Task UserCancellationBeforeThePhaseStartsIsNotTreatedAsBudgetTimeout()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
                Task.FromResult(new ProcessResult(0, "unused", "")), 40, 40);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PopulateDeviceDetailsAsync(
                new DeviceInfo { Udid = "detail-cancel-test", Platform = "harmony" },
                cancellation.Token));
        }

        [Fact]
        public async Task BudgetTimeoutPreservesPropertiesAlreadyRead()
        {
            int commands = 0;
            var service = new HarmonyLookupService(async (serial, command, timeout, token) =>
            {
                commands++;
                if (command.Length >= 3
                    && string.Equals(command[0], "param", StringComparison.Ordinal)
                    && string.Equals(command[2], "const.product.model", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, "Harmony Test", "");
                }

                await Task.Delay(Timeout.Infinite, token);
                return new ProcessResult(1, "", "Permission denied");
            }, 40, 40);
            var device = new DeviceInfo
            {
                Udid = "detail-budget-test",
                Platform = "harmony",
                Name = "fallback-name"
            };

            await service.PopulateDeviceDetailsAsync(device, CancellationToken.None);

            Assert.Equal(2, commands);
            Assert.Equal("Harmony Test", device.Name);
            Assert.Equal("Harmony Test", device.MarketName);
            Assert.Empty(device.Brand);
            Assert.Empty(device.CpuInfo);
            Assert.Empty(device.GpuInfo);
            Assert.Empty(device.Resolution);
        }
    }
}
