using System.Linq;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class AndroidTargetInventoryTests
    {
        [Fact]
        public void KeepsArbitraryPackagePrefixesAndRealNamedSystemProcesses()
        {
            var items = AndroidLookupService.ParsePs("PID NAME ARGS\n10 net.sample.game net.sample.game\n11 io.sample.tool io.sample.tool:worker\n12 daemon /system/bin/daemon\n13 kworker [kworker/0]\n");
            Assert.Equal(3, items.Count);
            Assert.Equal("net.sample.game", items[0].BundleId);
            Assert.Equal("io.sample.tool", items[1].BundleId);
            Assert.Equal("", items[2].BundleId);
            Assert.All(items, item => Assert.False(item.Recommended));
        }

        [Fact]
        public void InstalledAppListDoesNotInventWechatAndHostsAreNotForeground()
        {
            Assert.Empty(AndroidLookupService.ParseApps(""));
            var items = AndroidLookupService.ParseApps("package:/data/app/net.example/base.apk=net.example\n");
            Assert.Single(items);
            Assert.DoesNotContain(items, app => app.BundleId == "com.tencent.mm");
            var host = Assert.Single(AndroidLookupService.ParseApps("package:/data/app/mm/base.apk=com.tencent.mm"));
            Assert.False(host.Recommended);
            Assert.Equal("常用宿主", host.PickerBadge);
        }

        [Fact]
        public void IconCacheSeparatesDevicesAndInstalledApkPaths()
        {
            string first = AndroidLookupService.ScopedIconKey("device1", "io.game", "/app/v1/base.apk");
            Assert.NotEqual(first, AndroidLookupService.ScopedIconKey("device2", "io.game", "/app/v1/base.apk"));
            Assert.NotEqual(first, AndroidLookupService.ScopedIconKey("device1", "io.game", "/app/v2/base.apk"));
            Assert.Equal(first, AndroidLookupService.ScopedIconKey("device1", "io.game", "/app/v1/base.apk"));
        }
    }
}
