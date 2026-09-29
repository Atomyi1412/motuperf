using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Core.Tests
{
    public sealed class DevicePlatformNamesTests
    {
        [Theory]
        [InlineData("harmony")]
        [InlineData("openharmony")]
        [InlineData("harmonyos")]
        [InlineData("HARMONYOS")]
        public void HarmonyAliasesShareDisplayAndExportSemantics(string platform)
        {
            Assert.True(DevicePlatformNames.IsHarmony(platform));
            Assert.Equal("鸿蒙 设备", new DeviceInfo { Platform = platform }.PickerLabel);

            AppInfo app = new AppInfo { Platform = platform, HarmonyUserId = 0 };
            Assert.Equal("可尝试启动", app.LaunchAvailability);
        }

        [Fact]
        public void UnknownPlatformKeepsItsOriginalLabel()
        {
            Assert.False(DevicePlatformNames.IsHarmony("custom-os"));
            Assert.Equal("custom-os 设备", new DeviceInfo { Platform = "custom-os" }.PickerLabel);
        }

        [Fact]
        public void HarmonyPickerHidesInternalUserIdForSingleProfile()
        {
            AppInfo app = new AppInfo
            {
                Platform = "harmony",
                BundleId = "com.example.game",
                HarmonyUserId = 100
            };

            Assert.Equal("com.example.game", app.PickerTargetIdentifier);
            Assert.Contains("用户 100", app.TargetIdentifier);
            app.ShowHarmonyUserScope = true;
            Assert.Equal("com.example.game · 用户 100", app.PickerTargetIdentifier);

            ProcessInfo process = new ProcessInfo
            {
                Platform = "harmony",
                BundleId = "com.example.game",
                Name = "com.example.game",
                HarmonyUserId = 100
            };
            Assert.Equal("com.example.game", process.PickerSubtitle);
            process.ShowHarmonyUserScope = true;
            Assert.Equal("com.example.game · 用户 100", process.PickerSubtitle);
        }
    }
}
