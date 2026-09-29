using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyRealDeviceProbeTests
    {
        [Fact]
        public async Task ConnectedDeviceInventoryKeepsNativeServicesExtensionsAndForegroundApp()
        {
            string serial = Environment.GetEnvironmentVariable("MOTUPERF_HARMONY_REAL_SERIAL");
            if (string.IsNullOrWhiteSpace(serial))
            {
                Console.WriteLine("未设置 MOTUPERF_HARMONY_REAL_SERIAL，跳过鸿蒙真机探针；自动化夹具不替代真机验收。");
                return;
            }

            string expectedBundle = (Environment.GetEnvironmentVariable("MOTUPERF_HARMONY_REAL_BUNDLE") ?? "").Trim();
            string expectedExtensionBundle = (Environment.GetEnvironmentVariable("MOTUPERF_HARMONY_REAL_EXTENSION_BUNDLE") ?? "").Trim();
            var service = new HarmonyLookupService();
            HarmonyTargetInventory inventory = await service.ListTargetsAsync(serial, CancellationToken.None);

            Console.WriteLine("Harmony real-device apps=" + inventory.Apps.Count
                + " processes=" + inventory.Processes.Count
                + " appErrors=" + inventory.UserInventoryError
                + " processErrors=" + inventory.ProcessInventoryError);
            foreach (AppInfo app in inventory.Apps
                .Where(item => string.IsNullOrWhiteSpace(expectedBundle)
                    || item.BundleId == expectedBundle
                    || item.BundleId == "com.huawei.hmos.aidataservice"
                    || item.BundleId == "com.ohos.sceneboard")
                .OrderBy(item => item.BundleId)
                .ThenBy(item => item.HarmonyUserId))
            {
                Console.WriteLine("APP " + app.BundleId + " user=" + app.HarmonyUserId
                    + " launch=" + app.CanAttemptLaunch + " processOnly=" + app.IsProcessOnly
                    + " entries=" + (app.HarmonyLaunchEntries == null ? 0 : app.HarmonyLaunchEntries.Count)
                    + " nonUiEntries=" + (app.HarmonyLaunchEntries == null ? 0 : app.HarmonyLaunchEntries.Count(entry => entry != null && !entry.IsUiEntry))
                    + " recommended=" + app.Recommended);
            }
            foreach (ProcessInfo process in inventory.Processes
                .Where(item => string.IsNullOrWhiteSpace(expectedBundle)
                    || item.BundleId == expectedBundle
                    || item.BundleId == "com.huawei.hmos.aidataservice"
                    || item.BundleId == "com.ohos.sceneboard")
                .OrderBy(item => item.Pid))
            {
                Console.WriteLine("PROCESS " + process.Pid + " " + process.Name
                    + " bundle=" + process.BundleId + " user=" + process.HarmonyUserId
                    + " recommended=" + process.Recommended
                    + " ambiguous=" + process.OwnershipAmbiguous);
            }

            Assert.NotEmpty(inventory.Apps);
            Assert.NotEmpty(inventory.Processes);
            if (!string.IsNullOrWhiteSpace(expectedBundle))
            {
                Assert.Contains(inventory.Apps, item => item.BundleId == expectedBundle);
                Assert.Contains(inventory.Processes, item => item.BundleId == expectedBundle
                    && item.Pid > 0
                    && (item.Recommended || item.ForegroundApplication));
            }
            else
            {
                // A real device probe must use the current foreground evidence,
                // not a PID from an earlier run. The PID is expected to change
                // after an app restart, so only assert the live identity.
                ProcessInfo foreground = inventory.Processes.FirstOrDefault(item =>
                    item.Pid > 0 && item.ForegroundApplication && !string.IsNullOrWhiteSpace(item.BundleId));
                Assert.NotNull(foreground);
                Assert.Contains(inventory.Apps, item => item.BundleId == foreground.BundleId);
            }

            // The installed application inventory and the running process
            // inventory are different evidence sources. An application can be
            // installed but stopped, so the default auxiliary expectation only
            // verifies the app list. Use the opt-in running variable when a
            // caller wants to assert a live service/extension PID.
            string expectedAuxiliary = Environment.GetEnvironmentVariable("MOTUPERF_HARMONY_REAL_AUX_BUNDLES");
            foreach (string bundle in (expectedAuxiliary ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = bundle.Trim();
                Assert.Contains(inventory.Apps, item => item.BundleId == value);
            }

            string expectedRunningAuxiliary = Environment.GetEnvironmentVariable("MOTUPERF_HARMONY_REAL_RUNNING_AUX_BUNDLES");
            foreach (string bundle in (expectedRunningAuxiliary ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = bundle.Trim();
                Assert.Contains(inventory.Processes, item => item.BundleId == value && item.Pid > 0);
            }

            if (!string.IsNullOrWhiteSpace(expectedExtensionBundle))
            {
                AppInfo extensionApp = Assert.Single(inventory.Apps,
                    item => item.BundleId == expectedExtensionBundle);
                // The aggregate `bm dump -a` response on this device omits
                // extensionInfos. MoTuPerf intentionally loads per-Bundle
                // launch details lazily during the real start attempt, so a
                // complete app row must remain launch-capable without making
                // the picker issue hundreds of serial detail queries.
                Assert.True(extensionApp.CanAttemptLaunch);
            }
        }
    }
}
