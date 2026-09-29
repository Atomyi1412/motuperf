using System.Collections.Generic;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyRecommendationProjectionTests
    {
        [Fact]
        public void ForegroundProcessMarksExistingBundleAppAsRecommended()
        {
            var apps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.example.game",
                    Name = "Example Game",
                    Platform = "harmony",
                    HarmonyUserId = 100,
                    HarmonyUserIds = new List<int> { 100 }
                }
            };
            var process = new ProcessInfo
            {
                Pid = 35685,
                Name = "com.example.game",
                BundleId = "com.example.game",
                Platform = "harmony",
                HarmonyUserId = 100,
                ForegroundApplication = true,
                Recommended = true
            };

            HarmonyLookupService.MergeProcessApps(apps, new[] { process });

            AppInfo app = Assert.Single(apps);
            Assert.True(app.Recommended);
            Assert.True(app.IsRunning);
            Assert.Equal(35685, app.ProcessPid);
        }

        [Fact]
        public void HarmonyAppRunningRecordPromotesForegroundBundleIntoBothPickerProjections()
        {
            var bindings = HarmonyLookupService.ParseAbilityProcessBindings(
                "AppRunningRecord ID #19\n"
                + "  process name [com.m2.xzlr.hwhm]\n"
                + "  pid #11085 uid #20020190\n"
                + "  state #FOREGROUND\n");

            var process = new ProcessInfo
            {
                Pid = 11085,
                Name = "com.m2.xzlr.hwhm",
                BundleId = "com.m2.xzlr.hwhm",
                OwnerBundleId = "com.m2.xzlr.hwhm",
                Platform = "harmony",
                HarmonyUserId = 100
            };

            HarmonyLookupService.ApplyAbilityProcessBindings(new[] { process }, bindings);

            Assert.True(process.ForegroundApplication);
            Assert.True(process.Recommended);
            Assert.Equal(100, process.HarmonyUserId);

            var apps = new List<AppInfo>
            {
                new AppInfo
                {
                    BundleId = "com.m2.xzlr.hwhm",
                    Name = "星之旅人",
                    Platform = "harmony",
                    HarmonyUserId = 100
                }
            };

            HarmonyLookupService.MergeProcessApps(apps, new[] { process });

            AppInfo app = Assert.Single(apps);
            Assert.True(app.Recommended);
            Assert.True(app.IsRunning);
            Assert.Equal(11085, app.ProcessPid);
        }
    }
}
